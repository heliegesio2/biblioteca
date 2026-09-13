# Modelo de domínio

## Visão geral

```
┌──────────────┐          ┌──────────────┐          ┌──────────────┐
│    Book      │ 1      N │    Loan      │ N      1 │    User      │
│──────────────│◀────────▶│──────────────│◀────────▶│──────────────│
│ Isbn (único) │          │ BookId       │          │ Email (único)│
│ Title        │          │ UserId       │          │ Name         │
│ Author       │          │ Status       │          │ IsActive     │
│ TotalCopies  │          │ BorrowedAt   │          └──────────────┘
│ AvailCopies  │          │ DueAt        │
│ IsActive     │          │ ReturnedAt?  │
└──────────────┘          │ CancelledAt? │
                          └──────────────┘
                                 │
                          ┌──────▼────────┐
                          │  AuditEvent   │  toda mudança relevante,
                          │  (append-only)│  gravada na mesma transação
                          └───────────────┘
```

Exemplares são uma **contagem**, não entidades. O enunciado identifica o livro por
"título, ISBN, autor e quantidade de exemplares" — não há operação que dependa de
saber *qual* exemplar físico foi emprestado. Modelar uma tabela `copies` dobraria o
volume de código e mudaria a concorrência de "decrementar um contador" para "escolher
e travar uma linha livre", sem nenhum requisito que justifique. A decisão está
registrada em [ADR-0004](adr/0004-exemplares-como-contagem.md) e listada nas limitações
do [README](../README.md#9-limitações-conhecidas).

## Book

| Campo | Tipo | Regra |
|---|---|---|
| `Id` | `Guid` (v7) | Gerado pela aplicação; v7 é ordenável no tempo, o que evita fragmentação de índice |
| `Isbn` | `string` | **Único**, normalizado (só dígitos e `X` final, maiúsculo), ISBN-10 ou ISBN-13 com dígito verificador válido |
| `Title` | `string` | 1..300 caracteres, obrigatório |
| `Author` | `string` | 1..200 caracteres, obrigatório |
| `TotalCopies` | `int` | `>= 0` |
| `AvailableCopies` | `int` | `>= 0` e `<= TotalCopies` |
| `IsActive` | `bool` | `false` = desativado (soft delete) |
| `CreatedAt` / `UpdatedAt` | `timestamptz` | Sempre UTC |
| `xmin` | `uint` (sistema) | Token de concorrência, exposto como `ETag` |

### Invariantes

1. `0 <= AvailableCopies <= TotalCopies` — garantida em três camadas: no tipo
   (`Book.Borrow()` recusa quando não há exemplar), no comando SQL condicional
   (`WHERE available_copies > 0`) e por `CHECK` constraint no PostgreSQL.
2. `Isbn` é único entre livros, inclusive entre livros desativados. Reativar é
   possível; cadastrar um segundo livro com o mesmo ISBN não é.
3. **Reduzir `TotalCopies` abaixo do número de exemplares emprestados é rejeitado**
   (`422`). Emprestados = `TotalCopies - AvailableCopies`; a operação que violaria
   `AvailableCopies >= 0` não acontece.
4. Livro desativado (`IsActive = false`) **não pode** ser emprestado. Devoluções e
   cancelamentos de empréstimos já existentes continuam funcionando — desativar um
   livro não pode travar a devolução de quem está com ele.

### Alteração de quantidade

`PATCH /books/{id}` com `totalCopies` novo aplica o **delta** a `AvailableCopies`:

```
delta            = novoTotal - totalAtual
disponíveisNovo  = disponíveisAtual + delta      // rejeitado se < 0
```

Aumentar de 3 para 5 com 1 disponível resulta em 3 disponíveis. Diminuir de 5 para 2
com 1 disponível seria `-2` → rejeitado com `422` e mensagem explicando quantos
exemplares estão emprestados. Nunca se recalcula `AvailableCopies` "do zero" a partir
de uma contagem de empréstimos: isso abriria uma janela de leitura-escrita não atômica
exatamente na linha mais disputada do sistema.

## User

| Campo | Tipo | Regra |
|---|---|---|
| `Id` | `Guid` (v7) | |
| `Name` | `string` | 1..200, obrigatório |
| `Email` | `string` | **Único**, case-insensitive (`citext`), formato validado |
| `IsActive` | `bool` | Usuário inativo não pode criar empréstimo |
| `CreatedAt` | `timestamptz` | UTC |

Usuário não é apagado: o histórico de empréstimos depende dele. Desativação está fora
do escopo de endpoints (o campo existe e é respeitado pela política de empréstimo).

## Loan

| Campo | Tipo | Regra |
|---|---|---|
| `Id` | `Guid` (v7) | |
| `BookId` / `UserId` | `Guid` | FK obrigatórias, `ON DELETE RESTRICT` |
| `Status` | `LoanStatus` | `Active` \| `Returned` \| `Cancelled` |
| `BorrowedAt` | `timestamptz` | UTC, definido na criação |
| `DueAt` | `timestamptz` | `BorrowedAt + Loans:LoanPeriodDays` (default 14) |
| `ReturnedAt` | `timestamptz?` | Preenchido apenas na devolução |
| `CancelledAt` | `timestamptz?` | Preenchido apenas no cancelamento |
| `CreatedAt` / `UpdatedAt` | `timestamptz` | UTC |

### Máquina de estados

```
                    POST /loans
                         │
                         ▼
                    ┌─────────┐
        ┌───────────│ Active  │───────────┐
        │           └─────────┘           │
   POST /return                      POST /cancel
        │                                 │
        ▼                                 ▼
  ┌──────────┐                     ┌───────────┐
  │ Returned │                     │ Cancelled │
  └──────────┘                     └───────────┘
     (final)                          (final)
```

- Estados finais são finais. `POST /loans/{id}/return` em um empréstimo já devolvido
  responde `409 Conflict` (`loan-not-active`), **não** `200`. A operação não é
  idempotente por natureza e fingir que é esconderia um erro do cliente: devolver duas
  vezes o mesmo empréstimo indica um bug em quem chama.
- **Nenhuma transição apaga nada.** O empréstimo permanece na tabela com o status novo
  e as datas preservadas — é isso que sustenta `GET /books/{id}/history` e
  `GET /users/{id}/loans` depois da devolução.
- `Returned` e `Cancelled` devolvem o exemplar ao estoque
  (`available_copies = available_copies + 1`), na mesma transação da transição.

### Diferença entre devolver e cancelar

Ambos liberam o exemplar, mas contam histórias diferentes, e a auditoria precisa
distingui-las: *devolver* significa que o empréstimo cumpriu seu ciclo; *cancelar*
significa que ele não deveria ter existido (erro de balcão, desistência). Por isso são
dois status e dois eventos de auditoria — e não um campo booleano `returned`.

## LoanPolicy

Regras centralizadas em um tipo puro, sem I/O, testado unitariamente. Parâmetros vêm de
configuração (`Loans:*`), nunca hard-coded.

| Regra | Default | Erro quando violada |
|---|---|---|
| Prazo de devolução | 14 dias corridos | — |
| Máximo de empréstimos ativos por usuário | 5 | `422 user-loan-limit-reached` |
| Usuário precisa estar ativo | — | `422 user-inactive` |
| Livro precisa estar ativo | — | `422 book-inactive` |
| Precisa haver exemplar disponível | — | `409 book-unavailable` |
| Um usuário não pode ter dois empréstimos ativos do mesmo livro | — | `409 duplicate-active-loan` |

A última regra também existe como **índice único parcial** no banco
(`UNIQUE (book_id, user_id) WHERE status = 'Active'`): a verificação na aplicação dá a
mensagem boa, e o índice garante a invariante mesmo sob concorrência. Ver
[data-integrity.md](data-integrity.md).

`book-unavailable` é `409` (conflito com o estado atual do recurso, resolúvel por
retry quando alguém devolver) enquanto os demais são `422` (a requisição é
semanticamente inválida e repetir não ajuda). A distinção é intencional e está
documentada em [api-contract.md](api-contract.md).

## AuditEvent

| Campo | Tipo | Descrição |
|---|---|---|
| `Id` | `bigint` (identity) | Sequencial — dá ordem total de escrita e paginação keyset estável |
| `EntityType` | `string` | `Book` \| `Loan` \| `User` |
| `EntityId` | `Guid` | Identificador da entidade afetada |
| `Action` | `string` | Vocabulário fechado (`AuditActions`) |
| `Actor` | `string` | Identidade do JWT (`sub`/`name`) ou `system` |
| `OccurredAt` | `timestamptz` | **Sempre UTC** |
| `CorrelationId` | `string` | O mesmo da requisição e dos logs |
| `Payload` | `jsonb` | Informação suficiente para entender a mudança |

Ações cobertas: `BookCreated`, `BookUpdated`, `BookDeactivated`, `LoanCreated`,
`LoanReturned`, `LoanCancelled`. Detalhes do conteúdo de `Payload` e da consulta:
[auditing.md](auditing.md).

## Datas e fuso

Todo instante é `timestamptz` no banco e `DateTimeOffset`/UTC na aplicação. O relógio
vem de `TimeProvider` injetado — não de `DateTime.UtcNow` — o que permite congelar o
tempo nos testes de prazo e vencimento sem esperar 14 dias.

## Regras fora do escopo

Multas por atraso, reservas/fila de espera, renovação e notificação de vencimento não
foram implementadas: nenhuma aparece nos requisitos funcionais do enunciado. A estrutura
comporta todas — `LoanPolicy` é o ponto de extensão natural e `AuditEvent` já registraria
as transições novas.
