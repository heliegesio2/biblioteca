# Auditoria de domínio

Requisito: registrar eventos de auditoria para criação/alteração/desativação de livro e
criação/devolução/cancelamento de empréstimo, cada um com entidade, identificador, ação,
ator, timestamp UTC, identificador de correlação e informação suficiente para entender a
mudança. **Logs técnicos não substituem a trilha.**

## Por que não é log

Log e trilha de auditoria resolvem problemas diferentes, e tratá-los como a mesma coisa
é o erro que o requisito antecipa:

| | Log técnico | Trilha de auditoria |
|---|---|---|
| Público | Quem opera o sistema | Quem responde pelo negócio |
| Destino | stdout → coletor externo | Tabela no PostgreSQL |
| Retenção | Dias | Anos |
| Consulta | Ferramenta de observabilidade | `GET /audit-events`, SQL |
| Garantia | *Best-effort* (buffer, sampling, perda aceitável) | **Transacional**: existe se, e somente se, a mudança existe |
| Formato | Texto/JSON livre | Esquema estável e versionável |

A diferença decisiva é a última. Um log pode se perder num buffer quando o pod morre —
e tudo bem, para um log. Uma trilha de auditoria que perde um empréstimo não serve para
nada, porque ninguém consegue confiar nela sem saber *quais* eventos faltam.

## Gravação transacional

O `AuditWriter` insere o evento usando **o mesmo `DbContext` e a mesma transação** do
comando. Não há fila, não há outbox, não há chamada externa.

```
BEGIN
 ├─ UPDATE books SET available_copies = available_copies - 1 …
 ├─ INSERT INTO loans …
 ├─ INSERT INTO audit_events (…, 'LoanCreated', …)   ◀── mesma transação
 └─ UPDATE idempotency_keys …
COMMIT
```

Consequência: **se o empréstimo existe, o evento existe.** Não há caminho em que um
commite e o outro não. Se a transação falha, os dois somem juntos — e nenhum estado
inconsistente é registrado como se tivesse acontecido.

O custo é que um `INSERT` a mais entra no caminho crítico da escrita. É o preço da
garantia, e é pequeno: uma linha, sem índice único, em tabela append-only.

## Esquema do evento

| Campo | Exemplo | Observação |
|---|---|---|
| `id` | `4821` | `bigint identity` — ordem total de escrita, paginação estável |
| `entityType` | `Loan` | `Book` \| `Loan` \| `User` |
| `entityId` | `01923c...` | Identificador da entidade afetada |
| `action` | `LoanCreated` | Vocabulário fechado em `AuditActions` |
| `actor` | `ana@example.com` | Identidade do JWT (`sub`/`email`); `system` para rotinas internas |
| `occurredAt` | `2026-09-12T14:03:11Z` | **Sempre UTC**, de `TimeProvider` |
| `correlationId` | `0f9b2c1e-...` | O mesmo dos logs e da resposta HTTP |
| `payload` | `{ … }` | `jsonb` com o que basta para entender a mudança |

`action` é um vocabulário fechado — constantes em `AuditActions`, não strings soltas nos
handlers. Uma trilha em que o mesmo fato aparece como `LoanCreated`, `loan_created` e
`CreateLoan` não é consultável.

## Eventos cobertos

| Ação | Quando | `payload` |
|---|---|---|
| `BookCreated` | `POST /books` | Estado inicial: `isbn`, `title`, `author`, `totalCopies` |
| `BookUpdated` | `PATCH /books/{id}` | **Só os campos alterados**, em pares `{ "campo": { "from": …, "to": … } }` |
| `BookDeactivated` | `DELETE /books/{id}` | `{ "activeLoansAtDeactivation": 0 }` |
| `LoanCreated` | `POST /loans` | `bookId`, `userId`, `dueAt`, `availableCopiesAfter` |
| `LoanReturned` | `POST /loans/{id}/return` | `returnedAt`, `dueAt`, `daysLate`, `availableCopiesAfter` |
| `LoanCancelled` | `POST /loans/{id}/cancel` | `reason` (informado pelo cliente), `availableCopiesAfter` |

Duas escolhas de conteúdo que valem justificativa:

- **`BookUpdated` grava `from`/`to` apenas dos campos que mudaram.** Gravar a entidade
  inteira infla a tabela e obriga quem investiga a comparar dois JSONs para descobrir o
  que aconteceu. A pergunta que a trilha responde é "o que mudou", não "como estava tudo".
- **`availableCopiesAfter` aparece em todo evento de empréstimo.** É o que permite
  reconstruir a série histórica do estoque e auditar a invariante depois do fato — sem
  ele, a trilha registra que houve empréstimo, mas não permite verificar a contagem.

`daysLate` em `LoanReturned` é derivado (`returnedAt - dueAt`, mínimo 0) e é gravado
mesmo sem existir regra de multa: é a informação que torna a trilha útil para a decisão
futura de cobrar ou não.

## Consulta

`GET /audit-events` (papel `librarian`), com filtros `entityType`, `entityId`, `action`,
`actor`, `from`/`to` e paginação keyset por `id` decrescente. Contrato completo em
[api-contract.md](api-contract.md#auditoria).

Os índices que sustentam os filtros — `(entity_type, entity_id, id DESC)`,
`(occurred_at DESC)`, `(action, id DESC)` — estão em
[data-integrity.md](data-integrity.md).

Caso de uso típico: "por que este livro está com 0 disponíveis se só há 1 empréstimo
ativo?" → `GET /audit-events?entityType=Book&entityId={id}` devolve, em ordem, toda
alteração de quantidade e todo empréstimo que afetou aquele livro, com ator e correlação.

## Relação com `correlationId`

O `correlationId` é o que costura as três visões do mesmo fato:

```
requisição HTTP  ──▶  resposta (header X-Correlation-Id)
        │
        ├──▶ logs estruturados  (correlationId, bookId, userId, loanId)
        ├──▶ trace OpenTelemetry (correlation.id como atributo do span)
        └──▶ audit_events.correlation_id
```

Da linha da trilha ("quem emprestou e quando") se chega ao log técnico daquela
requisição ("o que o sistema fez, quanto demorou, quais queries rodaram"), e vice-versa.
Ver [observability.md](observability.md).

## Preservação do histórico

Nada é apagado, em nenhum caminho da aplicação:

- `DELETE /books/{id}` **desativa** (`is_active = false`). A FK `ON DELETE RESTRICT` em
  `loans` garante estruturalmente que nem um `DELETE` manual apagaria um livro com
  histórico.
- Devolução e cancelamento **mudam o status** do empréstimo e preenchem as datas; a linha
  continua existindo, com `borrowedAt` e `dueAt` intactos.
- `audit_events` é append-only: o código não emite `UPDATE` nem `DELETE` contra ela em
  lugar nenhum (exceto retenção, que não existe hoje).

Isso é verificado por teste — `LoanHistoryTests` confirma que, após devolução e após
cancelamento, o empréstimo continua aparecendo em `GET /books/{id}/history` e em
`GET /users/{id}/loans`, com o status novo, e que os eventos correspondentes estão em
`GET /audit-events`.

## Limitações

- **A trilha não é imutável por construção.** A aplicação só insere, mas quem tiver
  acesso direto ao banco pode alterar a tabela. Em produção: role da aplicação com
  permissão `INSERT`-only em `audit_events`, e/ou encadeamento por hash (cada linha
  guarda o hash da anterior) para tornar adulteração detectável.
- **Sem particionamento nem retenção.** Em volume real, `audit_events` cresce sem
  limite; o caminho natural é partição mensal com arquivamento.
- **O ator é a identidade do token**, sem IP nem user-agent. Ambos são fáceis de
  acrescentar ao `payload` e foram deixados de fora por não constarem do requisito.

Todas estão listadas no [README](../README.md#9-limitações-conhecidas).
