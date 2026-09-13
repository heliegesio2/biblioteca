# Idempotência

Requisito: `POST /loans` aceita `Idempotency-Key`; repetir a mesma requisição com a mesma
chave **não pode** criar dois empréstimos nem reduzir a disponibilidade duas vezes, e a
API deve devolver a resposta anterior (ou equivalente).

## O problema, concretamente

O cliente envia `POST /loans`, a API cria o empréstimo, e a resposta se perde — timeout,
conexão derrubada, pod removido durante o deploy. O cliente não sabe se o empréstimo
existe e faz o que qualquer cliente sensato faz: **repete**. Sem proteção, o segundo
request cria um segundo empréstimo e decrementa o estoque de novo.

Note que isso é diferente do problema de [concorrência](concurrency.md): lá, duas
requisições **distintas** disputam o mesmo exemplar, e a resposta certa é uma rejeição.
Aqui é a **mesma** requisição chegando duas vezes, e a resposta certa é repetir a
resposta original.

## Onde a chave mora, e por quê

Na tabela `idempotency_keys` do **PostgreSQL**, não no Redis, não em memória.

| Opção | Por que não |
|---|---|
| Memória do processo | Com 2 a 11 réplicas, o retry cai em outro pod e a chave não existe lá. Não protege nada. |
| Redis | Compartilhado entre réplicas, mas **não transacional com o efeito**. Existiria uma janela: chave gravada e transação com rollback (ou o contrário). Um retry legítimo seria rejeitado para sempre, ou um duplicado passaria. |
| **PostgreSQL, na mesma transação** | A chave e o empréstimo commitam juntos ou não commitam. Sem janela. |

A regra que decide: **a garantia precisa estar no mesmo domínio transacional que o efeito
que ela protege.** Ver [ADR-0005](adr/0005-idempotencia-no-postgres.md).

## Protocolo

```
POST /loans
Idempotency-Key: 7d0b9f2e-3b51-4d2a-9a1c-2f0b6d4e8a11
{ "bookId": "...", "userId": "..." }
```

O `IdempotencyDecorator` roda **dentro** da transação aberta pelo `TransactionDecorator`
([architecture.md](architecture.md#o-pipeline-cqrs)):

```
BEGIN
 ├─ INSERT INTO idempotency_keys (key, endpoint, request_hash, status)
 │  VALUES (@key, 'POST /loans', @hash, 'InProgress')
 │
 ├── sucesso ──▶ executa o handler ──▶ UPDATE idempotency_keys
 │                                     SET status='Completed',
 │                                         response_status=201,
 │                                         response_body=@json,
 │                                         resource_id=@loanId
 │
 └── violação de unicidade (23505) ──▶ SELECT a linha existente
        ├─ hash diferente          → 409 idempotency-key-reuse
        ├─ status = 'Completed'    → replay: devolve response_status + response_body
        └─ status = 'InProgress'   → 409 idempotency-in-progress + Retry-After: 1
COMMIT
```

`request_hash` é o SHA-256 do corpo canonicalizado (propriedades ordenadas, espaços
normalizados) mais a identidade do chamador. Sem ele, um cliente que reutilizasse a
mesma chave para um livro diferente receberia, silenciosamente, o empréstimo errado — o
que é pior do que um erro.

## Os quatro cenários

### 1. Repetição sequencial (o caso comum)

A primeira requisição já comitou. A segunda tenta inserir a chave, colide com a PK, lê a
linha `Completed` e devolve **a resposta original**: mesmo `201`, mesmo corpo, mesmo
`loanId`, acrescido de `Idempotency-Replayed: true`. O handler **não roda**, o estoque
**não** é decrementado de novo.

A métrica `biblioteca.idempotency.replayed` é incrementada — o requisito de
observabilidade pede contagem de operações idempotentes repetidas, e essa contagem também
é um bom sinal de saúde: uma subida repentina indica timeouts em algum ponto da malha.

### 2. Repetição concorrente (duas requisições ao mesmo tempo)

Este é o caso que quase todas as implementações erram. Aqui ele se resolve sozinho, pelo
comportamento do índice único:

```
tempo ──▶
T1: BEGIN  INSERT key ✓   … cria empréstimo …   UPDATE key→Completed   COMMIT
T2: BEGIN  INSERT key ⏸ bloqueada no índice único ───────────────────▶ erro 23505
                                                    SELECT key → Completed
                                                    devolve a resposta de T1
```

O PostgreSQL bloqueia o segundo `INSERT` até que o primeiro commite ou reverta — não
retorna a violação de imediato. Quando T1 comita, T2 recebe `23505`, lê a linha e
encontra a resposta **já pronta**. O cliente recebe o mesmo `201` e o mesmo `loanId`,
sem duplicar nada.

Se T1 tivesse dado rollback, a linha não existiria e o `INSERT` de T2 teria sucesso — T2
criaria o empréstimo normalmente, que é o comportamento correto.

O estado `InProgress` só é observável quando as requisições estão em **transações
diferentes que não se bloqueiam** (por exemplo, retry chegando depois de o pod original
travar sem commitar nem reverter). Nesse caso a resposta é `409 idempotency-in-progress`
com `Retry-After`, dizendo ao cliente que a operação está em andamento — e nunca
duplicando.

### 3. Mesma chave, corpo diferente

`409 idempotency-key-reuse`. A chave é um identificador **daquela** requisição, não um
token genérico. Reutilizá-la para outro conteúdo é bug do cliente, e a API diz isso em
vez de adivinhar.

### 4. Falha no meio do processamento

Rollback. A chave desaparece junto com o empréstimo, porque foram gravados na mesma
transação. O cliente pode repetir com a **mesma** chave e ter sucesso — que é
exatamente o comportamento desejado: idempotência não pode transformar uma falha
transitória em erro permanente.

## Interação com rejeições de negócio

Se o empréstimo é rejeitado por regra (`409 book-unavailable`, `422 user-loan-limit-reached`),
a transação inteira sofre rollback — **inclusive a chave**. Consequência: repetir com a
mesma chave tenta de novo, em vez de repetir o erro armazenado.

É a escolha certa aqui: `book-unavailable` é um estado transitório, e o cliente que
repete dez minutos depois, quando alguém devolveu o livro, deve conseguir o empréstimo.
Armazenar o erro congelaria a chave em um `409` eterno. O enunciado exige que a resposta
anterior seja devolvida no caso de repetição bem-sucedida — não que erros transitórios
sejam memorizados.

## Retenção e limpeza

`expires_at = created_at + Idempotency:RetentionHours` (default 24h, coberto com folga
pelo retry de qualquer cliente sensato). Um `BackgroundService` remove as expiradas
periodicamente:

```sql
DELETE FROM idempotency_keys WHERE expires_at < now();
```

O comando é idempotente e barato, então rodar em todas as réplicas é inofensivo — não
há eleição de líder nem agendamento preso a um pod. Em produção, isso seria um `CronJob`
fora do processo da API; está listado nas
[limitações](../README.md#9-limitações-conhecidas).

## Por que só `POST /loans`

- `GET`, `PATCH`, `DELETE` já são idempotentes por natureza (e `PATCH` é protegido por
  `If-Match`).
- `POST /users` e `POST /books` têm chave natural de unicidade (e-mail, ISBN): repetir
  devolve `409` da constraint, que já comunica bem. Não há risco de efeito duplicado.
- `POST /loans/{id}/return` e `/cancel` operam sobre um recurso identificado, e a
  transição condicional (`WHERE status='Active'`) garante efeito único: o segundo
  request recebe `409 loan-not-active`.

Só `POST /loans` cria um recurso novo com efeito colateral em outro agregado (o estoque)
sem chave natural. Aplicar o mecanismo onde ele não agrega seria custo sem benefício —
a decisão está registrada em [ADR-0005](adr/0005-idempotencia-no-postgres.md).

## Como isso é testado

`IdempotencyTests` (integração, Postgres real):

| Teste | Verifica |
|---|---|
| `SameKey_SameBody_Sequential` | Segundo `POST` devolve `201` + mesmo `loanId` + `Idempotency-Replayed: true`; apenas 1 empréstimo e estoque decrementado **uma** vez |
| `SameKey_Concurrent_TenRequests` | 10 requisições paralelas com a mesma chave → 1 empréstimo no banco, `available_copies` reduzido em 1, todas as respostas com o mesmo `loanId` |
| `SameKey_DifferentBody` | `409 idempotency-key-reuse` |
| `MissingKey` | `400 idempotency-key-missing` |
| `KeyRolledBack_AfterBusinessRejection` | Após `409 book-unavailable`, repetir com a mesma chave quando há exemplar → `201` |
