# ADR-0005 — Chave de idempotência no PostgreSQL, na mesma transação

**Status:** Aceita · **Data:** 2026-09-12

## Contexto

`POST /loans` deve aceitar `Idempotency-Key`, e repetir a requisição não pode criar dois
empréstimos nem reduzir a disponibilidade duas vezes. A API roda em 2 a 11 réplicas, e o
retry do cliente pode cair em qualquer uma delas.

O problema não é concorrência (requisições **distintas** disputando um exemplar), e sim a
**mesma** requisição chegando duas vezes — timeout, conexão derrubada, pod removido no
meio do deploy.

## Decisão

Tabela `idempotency_keys` no **PostgreSQL**, com a chave como **chave primária**, gravada
**na mesma transação** que cria o empréstimo.

```
BEGIN
 ├─ INSERT idempotency_keys (key, endpoint, request_hash, status='InProgress')
 │    └─ violação 23505 → SELECT existente → replay | 409 reuse | 409 in-progress
 ├─ … cria o empréstimo, decrementa estoque, grava auditoria …
 └─ UPDATE idempotency_keys SET status='Completed', response_status, response_body
COMMIT
```

O `IdempotencyDecorator` roda **dentro** do `TransactionDecorator` — essa ordem é a
decisão, não um detalhe de implementação.

Escopo: apenas `POST /loans`. Os demais endpoints ou já são idempotentes por natureza, ou
têm chave natural de unicidade (ISBN, e-mail), ou usam transição condicional
(`WHERE status='Active'`) que garante efeito único.

## Consequências

**Positivas**

- **Sem janela.** A chave e o efeito commitam juntos ou não commitam. Não existe estado
  em que a chave foi registrada e o empréstimo não (ou o contrário).
- **Repetição concorrente resolve sozinha**: o segundo `INSERT` bloqueia no índice único
  até o commit do primeiro e então encontra a resposta pronta para replay.
- **Falha permite retry**: rollback remove a chave, e o cliente pode repetir com a mesma
  chave e ter sucesso — idempotência não transforma falha transitória em erro permanente.
- Funciona igual com 2 ou 11 réplicas, sem coordenação adicional.

**Negativas**

- Uma tabela a mais, que cresce e precisa de expiração (`BackgroundService` com `DELETE`
  condicional; em produção, um `CronJob`).
- Dois comandos SQL a mais no caminho crítico do endpoint mais sensível a latência.
- A resposta armazenada é um snapshot: se o formato do DTO mudar entre versões, um replay
  durante o deploy pode devolver o formato antigo. Aceitável dentro da janela de retenção
  (24h) e mitigável versionando o corpo armazenado.

## Alternativas descartadas

| Alternativa | Por que não |
|---|---|
| Chave em memória do processo | Com N réplicas, o retry cai em outro pod e a chave não existe lá. Não protege nada |
| Chave no Redis | Compartilhada, mas **não transacional com o efeito**. Existiria janela entre gravar a chave e commitar o empréstimo — um retry legítimo seria rejeitado para sempre, ou um duplicado passaria |
| Redis com lock + confirmação no banco | Duas fontes de verdade para a mesma garantia, com todos os modos de falha de cada uma |
| Deduplicação por (bookId, userId, janela de tempo) | Heurística: rejeitaria um segundo empréstimo legítimo e não cobriria retries fora da janela |
| Idempotência em todos os `POST` | Custo sem benefício onde já há chave natural de unicidade |

## Regra geral extraída

> A garantia precisa estar no mesmo domínio transacional que o efeito que ela protege.

É a mesma razão pela qual a auditoria é gravada na transação ([auditing.md](../auditing.md))
e pela qual o cache — que **não** é transacional — nunca decide nada
([ADR-0007](0007-cache-invalidacao-pos-commit.md)).

Protocolo completo e cenários: [idempotency.md](../idempotency.md).
