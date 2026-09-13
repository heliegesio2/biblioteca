# Contrato da API

Base: `http://localhost:8080`. Corpo em `application/json`, UTF-8. Instantes em ISO-8601
UTC (`2026-09-12T14:03:11Z`).

## Desvios do contrato sugerido

O enunciado permite ajustar o contrato desde que a decisão seja documentada. São três
desvios, todos deliberados:

| Desvio | Motivo |
|---|---|
| `DELETE /books/{id}` **desativa** o livro em vez de apagá-lo, sempre (`204`), e responde `409` se houver empréstimo **ativo** | O enunciado exige "desativar ou rejeitar a remoção" e proíbe apagar histórico silenciosamente. Apagar de verdade quebraria as FKs de `loans` e a trilha de auditoria |
| `PATCH /books/{id}` **exige** `If-Match` | Sem isso, duas edições concorrentes se sobrescrevem silenciosamente. Ver [concurrency.md](concurrency.md) |
| `POST /loans` **exige** `Idempotency-Key` (`400` se ausente) | O enunciado diz "deve aceitar"; tornar obrigatório elimina a ambiguidade de ter dois comportamentos no mesmo endpoint. Ver [idempotency.md](idempotency.md) |

## Cabeçalhos

| Cabeçalho | Direção | Uso |
|---|---|---|
| `Authorization: Bearer <jwt>` | entrada | Obrigatório, exceto em `/health/*` e `POST /auth/token` |
| `X-Correlation-Id` | entrada/saída | Aceito do cliente; gerado se ausente; **sempre** devolvido |
| `Idempotency-Key` | entrada | Obrigatório em `POST /loans` |
| `Idempotency-Replayed: true` | saída | Resposta servida do registro de idempotência |
| `ETag` | saída | `GET /books/{id}` — valor do `xmin` da linha |
| `If-Match` | entrada | Obrigatório em `PATCH /books/{id}` |
| `If-None-Match` | entrada | `GET /books/{id}` → `304` quando bate |

## Erros — RFC 9457 (Problem Details)

Toda resposta de erro tem o mesmo formato, `Content-Type: application/problem+json`:

```json
{
  "type": "https://biblioteca.dev/errors/book-unavailable",
  "title": "Não há exemplar disponível",
  "status": 409,
  "detail": "O livro 'Dom Casmurro' não possui exemplares disponíveis no momento.",
  "instance": "/loans",
  "code": "book-unavailable",
  "correlationId": "0f9b2c1e-...",
  "traceId": "00-4bf92f...-01"
}
```

Erros de validação acrescentam `errors` (campo → lista de mensagens), no formato de
`ValidationProblemDetails`.

### Códigos de erro de negócio

| `code` | Status | Significado |
|---|---|---|
| `validation-failed` | 400 | Corpo ou query string inválidos |
| `idempotency-key-missing` | 400 | `POST /loans` sem `Idempotency-Key` |
| `unauthenticated` | 401 | Token ausente, expirado ou inválido |
| `forbidden` | 403 | Papel sem permissão para a operação |
| `book-not-found` / `user-not-found` / `loan-not-found` | 404 | Recurso inexistente |
| `isbn-already-exists` | 409 | ISBN já cadastrado em outro livro |
| `email-already-exists` | 409 | E-mail já cadastrado |
| `book-unavailable` | 409 | Sem exemplar disponível **agora** |
| `duplicate-active-loan` | 409 | O usuário já tem esse livro emprestado |
| `loan-not-active` | 409 | Devolver/cancelar um empréstimo que já está finalizado |
| `book-has-active-loans` | 409 | Desativação recusada: há exemplares emprestados |
| `idempotency-key-reuse` | 409 | Mesma chave com payload diferente |
| `idempotency-in-progress` | 409 | Requisição com a mesma chave ainda em andamento |
| `book-inactive` / `user-inactive` | 422 | Entidade desativada |
| `user-loan-limit-reached` | 422 | Limite de empréstimos ativos atingido |
| `copies-below-active-loans` | 422 | `totalCopies` menor que os exemplares emprestados |
| `precondition-failed` | 412 | `If-Match` não confere (edição concorrente) |
| `precondition-required` | 428 | `PATCH` sem `If-Match` |

**409 vs. 422**: `409` significa conflito com o estado atual — repetir mais tarde pode
funcionar (alguém devolve o livro). `422` significa que a requisição é semanticamente
inválida — repetir igual nunca vai funcionar. A distinção dá ao cliente uma política de
retry inequívoca.

---

## Catálogo

### `POST /books` — criar livro
Papel: `librarian`.

```json
{ "isbn": "978-85-359-0277-5", "title": "Dom Casmurro",
  "author": "Machado de Assis", "totalCopies": 3 }
```

`201 Created` + `Location: /books/{id}`, corpo = `BookResponse`.
Erros: `400 validation-failed`, `409 isbn-already-exists`.

O ISBN é normalizado antes de gravar (hífens e espaços removidos, `X` maiúsculo) e o
dígito verificador é validado — `978-85-359-0277-5` e `9788535902775` são o mesmo livro.

### `GET /books` — listar/buscar
Papel: qualquer autenticado.

| Query | Default | Descrição |
|---|---|---|
| `search` | — | Busca parcial em título, autor ou ISBN |
| `includeInactive` | `false` | Inclui livros desativados |
| `availableOnly` | `false` | Apenas com `availableCopies > 0` |
| `cursor` | — | Cursor opaco da página seguinte |
| `limit` | `20` | 1..100 |

```json
{ "items": [ { "...": "BookResponse" } ], "nextCursor": "eyJpZCI6..." }
```

Paginação **keyset** (cursor por `(title, id)`), não `offset`: com `OFFSET` grande o
PostgreSQL varre e descarta as linhas puladas, e uma inserção concorrente faz um item
aparecer duas vezes ou sumir entre páginas. Esta listagem **não é cacheada** — ver
[caching.md](caching.md).

### `GET /books/{id}` — detalhe
Cacheado. Responde `ETag`; com `If-None-Match` que bate, `304 Not Modified` sem corpo.

```json
{ "id": "01923f...", "isbn": "9788535902775", "title": "Dom Casmurro",
  "author": "Machado de Assis", "totalCopies": 3, "availableCopies": 1,
  "isActive": true, "createdAt": "...", "updatedAt": "..." }
```

### `PATCH /books/{id}` — atualização parcial
Papel: `librarian`. **Exige** `If-Match: "<etag>"`.

Campos opcionais; ausente = não alterado. `null` não é usado para apagar valor.

```json
{ "title": "Dom Casmurro (edição comentada)", "totalCopies": 5 }
```

`200 OK` com o livro atualizado e `ETag` novo.
Erros: `412 precondition-failed` (alguém editou antes), `428 precondition-required`
(sem `If-Match`), `422 copies-below-active-loans`, `409 isbn-already-exists`.

`totalCopies` ajusta `availableCopies` pelo **delta** — ver
[domain-model.md](domain-model.md#alteração-de-quantidade).

### `DELETE /books/{id}` — desativar
Papel: `librarian`.

- Sem empréstimos ativos → `204 No Content`, `isActive = false`. O livro some da
  listagem padrão, continua acessível por `GET /books/{id}` e todo o histórico permanece.
- Com empréstimos ativos → `409 book-has-active-loans`.
- Já desativado → `204` (esta operação *é* idempotente: o estado final é o desejado).

### `GET /books/{id}/availability` — disponibilidade
Cacheado com TTL curto (30s). A consulta mais chamada do sistema.

```json
{ "bookId": "01923f...", "title": "Dom Casmurro", "isActive": true,
  "totalCopies": 3, "availableCopies": 1, "activeLoans": 2,
  "asOf": "2026-09-12T14:03:11Z" }
```

`asOf` é o instante em que o valor foi lido do banco, não o instante da resposta: deixa
explícito ao cliente quão fresco é o dado. **Este número é informativo.** A decisão de
emprestar é sempre tomada no PostgreSQL, dentro da transação —
ver [concurrency.md](concurrency.md).

### `GET /books/{id}/history` — histórico do livro
Todos os empréstimos do livro, incluindo devolvidos e cancelados, do mais recente para o
mais antigo. Filtro opcional `status`. Paginação keyset. Nunca cacheado.

---

## Usuários

### `POST /users`
```json
{ "name": "Ana Ribeiro", "email": "ana@example.com" }
```
`201 Created` + `Location: /users/{id}`. Erro: `409 email-already-exists`.

### `GET /users/{id}/loans`
Empréstimos do usuário, incluindo finalizados. Filtro `status=Active|Returned|Cancelled`,
paginação keyset. Um `member` só pode consultar os próprios empréstimos; `librarian`
consulta qualquer um. Ver [security.md](security.md).

---

## Empréstimos

### `POST /loans` — criar empréstimo
Papel: `librarian` ou o próprio `member`. **Exige `Idempotency-Key`.**

```http
POST /loans
Idempotency-Key: 7d0b9f2e-3b51-4d2a-9a1c-2f0b6d4e8a11
Content-Type: application/json

{ "bookId": "01923f...", "userId": "01923a..." }
```

`201 Created` + `Location: /loans/{id}`:

```json
{ "id": "01923c...", "bookId": "01923f...", "userId": "01923a...",
  "status": "Active", "borrowedAt": "2026-09-12T14:03:11Z",
  "dueAt": "2026-09-26T14:03:11Z", "returnedAt": null, "cancelledAt": null }
```

| Situação | Resposta |
|---|---|
| Repetição com a mesma chave e mesmo corpo | `201` com o corpo original + `Idempotency-Replayed: true` |
| Mesma chave, corpo diferente | `409 idempotency-key-reuse` |
| Sem exemplar disponível | `409 book-unavailable` |
| Usuário já tem esse livro | `409 duplicate-active-loan` |
| Limite de empréstimos atingido | `422 user-loan-limit-reached` |
| Livro ou usuário inativo | `422 book-inactive` / `422 user-inactive` |

### `POST /loans/{id}/return` — devolver
`200 OK` com o empréstimo em `Returned` e `returnedAt` preenchido. O exemplar volta ao
estoque na mesma transação. Empréstimo não-ativo → `409 loan-not-active`.

### `POST /loans/{id}/cancel` — cancelar
Papel: `librarian`. Corpo opcional `{ "reason": "erro de registro no balcão" }`, que vai
para o `payload` do evento de auditoria. `200 OK` com o empréstimo em `Cancelled`.

O registro **não é apagado**: continua aparecendo no histórico do livro e do usuário,
com `status: "Cancelled"` e `cancelledAt` preenchido.

---

## Auditoria

### `GET /audit-events`
Papel: `librarian`.

| Query | Descrição |
|---|---|
| `entityType` | `Book` \| `Loan` \| `User` |
| `entityId` | Filtra uma entidade específica |
| `action` | `LoanCreated`, `BookUpdated`, … |
| `actor` | Quem executou |
| `from` / `to` | Intervalo em UTC sobre `occurredAt` |
| `cursor` / `limit` | Paginação keyset por `id` decrescente |

```json
{ "items": [ {
    "id": 4821, "entityType": "Loan", "entityId": "01923c...",
    "action": "LoanCreated", "actor": "ana@example.com",
    "occurredAt": "2026-09-12T14:03:11Z", "correlationId": "0f9b2c1e-...",
    "payload": { "bookId": "01923f...", "userId": "01923a...",
                 "dueAt": "2026-09-26T14:03:11Z", "availableCopiesAfter": 0 } } ],
  "nextCursor": "4802" }
```

---

## Autenticação

### `POST /auth/token`
Disponível apenas fora de `Production`. Emite um JWT para exercitar autorização e o
campo `actor` da auditoria. Substituto declarado de um IdP real — ver
[security.md](security.md).

```json
{ "userId": "01923a...", "role": "librarian" }
```
```json
{ "accessToken": "eyJhbGci...", "expiresAt": "2026-09-12T18:03:11Z", "tokenType": "Bearer" }
```

---

## Health

| Endpoint | Verifica | Sem auth |
|---|---|---|
| `GET /health/live` | Só o processo. **Não** consulta PostgreSQL nem Redis | sim |
| `GET /health/ready` | PostgreSQL (crítico). Redis é reportado, mas **não** derruba a readiness | sim |

O motivo de Redis não reprovar a readiness está em
[observability.md](observability.md#health-checks) — resumo: a API funciona sem cache, e
derrubar todas as réplicas por causa de um componente degradável transforma perda de
desempenho em indisponibilidade total.

## OpenAPI

Gerado pelo suporte nativo do .NET 10 em `/openapi/v1.json`, com UI em `/scalar`
(somente fora de `Production`). Os exemplos de erro do documento OpenAPI são os mesmos
`code`s da tabela acima.
