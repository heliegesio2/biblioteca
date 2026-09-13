# Concorrência

O requisito central do desafio: **com um exemplar disponível, duas requisições
simultâneas de empréstimo devem produzir exatamente um sucesso, uma rejeição clara de
regra de negócio, e nenhum estado inconsistente** — com a API rodando em várias réplicas.

## O problema

A versão ingênua:

```csharp
var book = await db.Books.FindAsync(bookId);      // lê available_copies = 1
if (book.AvailableCopies == 0) return Unavailable();
book.AvailableCopies--;                            // decide em memória
db.Loans.Add(new Loan(...));
await db.SaveChangesAsync();                       // escreve
```

Entre a leitura e a escrita existe uma janela. Duas réplicas leem `1`, as duas passam
pelo `if`, as duas gravam `0` e criam empréstimo: dois empréstimos, um exemplar. Nenhum
`lock` em memória do processo resolve isso, porque os processos são diferentes — e é
exatamente por isso que o enunciado insiste em "múltiplas réplicas".

A janela existe porque **ler, decidir e escrever são três operações separadas**. A
solução é fazer as três serem uma só.

## A estratégia adotada: atualização condicional atômica

```sql
UPDATE books
   SET available_copies = available_copies - 1,
       updated_at       = @now
 WHERE id = @bookId
   AND is_active
   AND available_copies > 0;
```

No EF Core 10:

```csharp
var affected = await db.Books
    .Where(b => b.Id == bookId && b.IsActive && b.AvailableCopies > 0)
    .ExecuteUpdateAsync(s => s
        .SetProperty(b => b.AvailableCopies, b => b.AvailableCopies - 1)
        .SetProperty(b => b.UpdatedAt, now), ct);

if (affected == 0)
    return Result.Fail(LoanErrors.BookUnavailable);   // → 409, rejeição de negócio
```

A condição (`available_copies > 0`) e o efeito (`- 1`) estão na **mesma instrução**, e o
valor novo é calculado **pelo banco a partir do valor atual da linha** — nunca a partir
de um número que a aplicação leu antes. Não há janela.

### Por que isso é correto em `READ COMMITTED`

O detalhe que faz funcionar está na documentação do PostgreSQL sobre `READ COMMITTED`:
quando duas transações tentam atualizar a mesma linha, a segunda **bloqueia** até a
primeira terminar. Se a primeira comitar, a segunda **reavalia a cláusula `WHERE` sobre
a versão atualizada da linha** (`EvalPlanQual`) e, se a condição deixou de valer, não
atualiza nada.

```
tempo ──▶
T1: BEGIN  UPDATE … WHERE available>0  ✓ (1 linha, available: 1 → 0)  COMMIT
T2: BEGIN  UPDATE … WHERE available>0  ⏸ bloqueada ──────────────────▶ reavalia:
                                          available = 0 → 0 linhas → 409
```

Portanto, das duas requisições: uma afeta 1 linha e cria o empréstimo; a outra afeta 0
linhas e recebe `409 book-unavailable`. `available_copies` nunca fica negativo, nem
existe empréstimo sem exemplar correspondente.

Vale notar que esse comportamento é específico de `READ COMMITTED`. Em
`REPEATABLE READ`, a segunda transação receberia `40001` em vez de reavaliar — também
seguro, mas mais caro (rollback + retry). `READ COMMITTED` entrega a mesma garantia sem
abortar transação.

### A transação inteira

Tudo o que compõe o empréstimo acontece em **uma** transação:

```
BEGIN                                            (TransactionDecorator, READ COMMITTED)
 ├─ INSERT idempotency_keys (InProgress)         reserva a chave
 ├─ SELECT … FROM books  WHERE id = @bookId      validações de negócio (ativo, existe)
 ├─ SELECT count(*) FROM loans
 │     WHERE user_id = @u AND status='Active'    limite por usuário
 ├─ UPDATE books SET available = available - 1
 │     WHERE id=@b AND is_active AND available>0 ◀── a decisão atômica; 0 linhas → 409
 ├─ INSERT loans (…, status='Active')            índice único parcial impede duplicata
 ├─ INSERT audit_events (LoanCreated)            trilha na mesma transação
 └─ UPDATE idempotency_keys (Completed, snapshot)
COMMIT
                                                 → invalidação do cache (pós-commit)
```

As validações de negócio antes do `UPDATE` existem para dar **mensagem certa**
(`user-loan-limit-reached`, `book-inactive`). Elas não são a garantia de corretude — a
garantia é o `UPDATE` condicional e as constraints. Se uma validação passar por
condição de corrida, o `UPDATE`, o índice único parcial ou o `CHECK` recusam. As três
camadas concordam; nenhuma depende da outra estar certa.

### Rede de segurança no banco

```sql
CHECK (available_copies >= 0 AND available_copies <= total_copies)
UNIQUE (book_id, user_id) WHERE status = 'Active'
```

Se um caminho futuro errar a conta, a transação falha em vez de gravar estado
impossível. Ver [data-integrity.md](data-integrity.md).

## Alternativas consideradas

| Alternativa | Avaliação |
|---|---|
| **`SELECT … FOR UPDATE`** e depois `UPDATE` | Correto. Mas o lock de linha dura o round-trip inteiro da aplicação (validações, `INSERT`s, auditoria) em vez de uma instrução, reduzindo a vazão na linha mais disputada. Vale a pena se um dia a decisão precisar de várias leituras consistentes antes de escrever; hoje, não precisa. Continua sendo o plano B natural. |
| **Token de concorrência (`xmin`) no livro** | Gera **falso conflito**: com 50 exemplares disponíveis, duas retiradas simultâneas são ambas legítimas, mas a segunda falharia porque o `xmin` mudou. Em linha quente, vira retry em cascata e latência instável. É a ferramenta certa quando o conflito é semanticamente real — edição do catálogo — e é lá que a usamos. |
| **`SERIALIZABLE`** | Resolve, e resolveria também invariantes multi-linha mais complexas. Custo: aborts `40001` sob carga e obrigação de retry em toda a aplicação, para um problema que uma instrução já resolve. Overkill aqui. |
| **Lock distribuído no Redis** (`SET NX` / Redlock) | Colocaria a corretude nas mãos de um componente que queremos manter descartável, com expiração de lock, *clock skew* e necessidade de *fencing token*. O banco já oferece a garantia de graça. Redis aqui só acelera leitura. |
| **Fila serializando empréstimos** | Elimina a concorrência, mas troca uma resposta síncrona `201/409` por processamento assíncrono, mudando o contrato da API e a experiência do cliente. Desproporcional ao problema. |
| **`rowversion`** | Recurso exclusivo do SQL Server; não existe no PostgreSQL. Explicitamente descartado pelo enunciado. |

## Onde usamos concorrência otimista: `PATCH /books/{id}`

Editar o catálogo é um problema **diferente** de retirar exemplar. Se dois bibliotecários
abrem o mesmo livro e editam campos, o último `PATCH` sobrescreveria silenciosamente a
alteração do primeiro (*lost update*) — e aqui o conflito é real: as duas intenções são
incompatíveis e alguém precisa saber disso.

```
GET /books/{id}          →  200  ETag: "12345"        (12345 = xmin da linha)
PATCH /books/{id}        ←  If-Match: "12345"
   UPDATE … WHERE id=@id AND xmin=12345
   0 linhas afetadas     →  412 Precondition Failed
```

- `If-Match` é **obrigatório**; sem ele, `428 Precondition Required`. Não existe caminho
  de escrita "cega" no catálogo.
- O EF detecta o conflito via `UseXminAsConcurrencyToken()` e lança
  `DbUpdateConcurrencyException`, traduzida para `412 precondition-failed`.
- Resolver o conflito é decisão do cliente: ele relê, compara e reenvia. A API não faz
  merge automático — merge silencioso é exatamente o que estamos evitando.

Resumindo a escolha: **pessimista onde o conflito é disputa por recurso escasso
(exemplar), otimista onde o conflito é divergência de intenção (edição)**.

## Devolução e cancelamento

Mesma técnica, condição diferente — a transição de status é que é atômica:

```sql
UPDATE loans
   SET status = 'Returned', returned_at = @now, updated_at = @now
 WHERE id = @loanId AND status = 'Active';
-- 0 linhas → 409 loan-not-active
```

Só depois de confirmar a transição (1 linha afetada) o exemplar volta ao estoque
(`available_copies + 1`), na mesma transação. Duas devoluções simultâneas do mesmo
empréstimo: uma vence, a outra recebe `409` — e o exemplar é devolvido **uma** vez, não
duas. Sem isso, um duplo clique inflaria o estoque.

## Idempotência como camada complementar

Concorrência resolve "duas requisições diferentes disputando o mesmo exemplar".
Idempotência resolve "a **mesma** requisição chegando duas vezes" (retry de cliente,
timeout de rede, *load balancer* reenviando). São problemas distintos, com soluções
distintas, e o empréstimo precisa das duas. Ver [idempotency.md](idempotency.md).

## Como isso é testado

`LastCopyConcurrencyTests`, em `Biblioteca.IntegrationTests`, contra PostgreSQL real
(Testcontainers) — banco em memória não reproduz `EvalPlanQual` e tornaria o teste
inútil:

```csharp
// livro com total_copies = 1, available_copies = 1
var barrier = new Barrier(20);
var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
{
    barrier.SignalAndWait();                      // dispara as 20 no mesmo instante
    return await client.PostAsJsonAsync("/loans", new { bookId, userId = users[i] },
        idempotencyKey: Guid.NewGuid().ToString());
}));

results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(19);
(await GetBook(bookId)).AvailableCopies.Should().Be(0);
(await CountActiveLoans(bookId)).Should().Be(1);
```

Verificações: exatamente 1 criado, 19 rejeitados com erro de negócio (`book-unavailable`,
não erro genérico), `available_copies = 0` (nunca negativo), exatamente 1 empréstimo
ativo no banco. O teste usa 20 usuários distintos para que o índice único parcial não
seja o que rejeita — o que está sendo testado é o `UPDATE` condicional.

Complementos: `ReturnLoanConcurrencyTests` (duas devoluções simultâneas → 1 sucesso, 1
`409`, estoque +1) e `UpdateBookConcurrencyTests` (dois `PATCH` com o mesmo `ETag` → 1
sucesso, 1 `412`).

## Limites da abordagem

- **Não há transação distribuída.** Tudo o que precisa ser atômico está no mesmo banco.
  Se um dia o catálogo e os empréstimos forem serviços separados, esta estratégia não se
  aplica e o desenho passa a exigir saga ou outbox.
- **Linha quente**: um best-seller com muitos exemplares serializa em uma única linha de
  `books`. Para o volume do desafio isso é irrelevante; em escala muito maior, a saída
  seria dividir o contador em *buckets* ou migrar para exemplares individuais.
- **O cache pode indicar disponível o que já acabou.** É aceito por construção: a
  decisão nunca vem do cache. O pior caso é o cliente tentar e receber `409` — nunca um
  empréstimo inválido. Ver [caching.md](caching.md).
