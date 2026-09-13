# Integridade dos dados

Princípio: **toda invariante que o banco consegue garantir, o banco garante.** A
validação na aplicação existe para dar mensagem de erro boa; a constraint existe para
que a invariante continue verdadeira mesmo com bug na aplicação, script manual ou
requisição concorrente por um caminho que ninguém previu.

## Schema

Nomes em `snake_case` (convenção do PostgreSQL), `timestamptz` em tudo que é instante.

### `books`

```sql
CREATE TABLE books (
    id               uuid        PRIMARY KEY,
    isbn             varchar(13) NOT NULL,
    title            varchar(300) NOT NULL,
    author           varchar(200) NOT NULL,
    total_copies     integer     NOT NULL,
    available_copies integer     NOT NULL,
    is_active        boolean     NOT NULL DEFAULT true,
    created_at       timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL,

    CONSTRAINT ck_books_total_copies     CHECK (total_copies >= 0),
    CONSTRAINT ck_books_available_copies CHECK (available_copies >= 0
                                            AND available_copies <= total_copies)
);

CREATE UNIQUE INDEX ux_books_isbn ON books (isbn);
CREATE INDEX ix_books_title_id     ON books (title, id);          -- paginação keyset
CREATE INDEX ix_books_title_trgm   ON books USING gin (title gin_trgm_ops);
CREATE INDEX ix_books_author_trgm  ON books USING gin (author gin_trgm_ops);
```

- `ck_books_available_copies` é a rede de segurança do requisito "nenhuma quantidade
  negativa". Mesmo que alguém, um dia, troque o `UPDATE` condicional por um
  `read-modify-write`, o banco recusa o valor inválido.
- O índice único de ISBN cobre também livros desativados: reativar é possível, duplicar
  não.
- `gin_trgm_ops` (extensão `pg_trgm`) faz o `ILIKE '%termo%'` da busca usar índice.
  Suficiente para o volume do desafio; acima disso, `tsvector`.

### `users`

```sql
CREATE TABLE users (
    id         uuid        PRIMARY KEY,
    name       varchar(200) NOT NULL,
    email      citext      NOT NULL,
    is_active  boolean     NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL
);

CREATE UNIQUE INDEX ux_users_email ON users (email);
```

`citext` resolve a unicidade case-insensitive no banco. A alternativa —
`UNIQUE (lower(email))` — funciona igual, mas obriga toda query a lembrar do `lower()`.

### `loans`

```sql
CREATE TABLE loans (
    id           uuid        PRIMARY KEY,
    book_id      uuid        NOT NULL REFERENCES books (id) ON DELETE RESTRICT,
    user_id      uuid        NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
    status       varchar(16) NOT NULL,
    borrowed_at  timestamptz NOT NULL,
    due_at       timestamptz NOT NULL,
    returned_at  timestamptz NULL,
    cancelled_at timestamptz NULL,
    created_at   timestamptz NOT NULL,
    updated_at   timestamptz NOT NULL,

    CONSTRAINT ck_loans_status CHECK (status IN ('Active','Returned','Cancelled')),
    CONSTRAINT ck_loans_due_after_borrow CHECK (due_at > borrowed_at),
    CONSTRAINT ck_loans_terminal_dates CHECK (
        (status = 'Active'    AND returned_at IS NULL AND cancelled_at IS NULL) OR
        (status = 'Returned'  AND returned_at IS NOT NULL) OR
        (status = 'Cancelled' AND cancelled_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_loans_active_book_user
    ON loans (book_id, user_id) WHERE status = 'Active';

CREATE INDEX ix_loans_book_id_id ON loans (book_id, id DESC);
CREATE INDEX ix_loans_user_id_id ON loans (user_id, id DESC);
CREATE INDEX ix_loans_active_user ON loans (user_id) WHERE status = 'Active';
```

Três decisões que merecem destaque:

- **`ON DELETE RESTRICT`** nas duas FKs: o banco recusa apagar livro ou usuário com
  histórico. É a garantia estrutural de que "o histórico não pode ser apagado
  silenciosamente" — mesmo por um `DELETE` manual em produção.
- **`ux_loans_active_book_user`** (índice único parcial) impede que o mesmo usuário
  tenha dois empréstimos ativos do mesmo livro, **sob concorrência**, sem precisar de
  lock. Depois da devolução o registro sai do índice (status muda) e um novo empréstimo
  é permitido — exatamente a semântica desejada.
- **`ck_loans_terminal_dates`** amarra status e datas: um empréstimo `Returned` sem
  `returned_at` é impossível de gravar. Sem isso, um bug de transição produziria
  histórico mentiroso, que é pior do que histórico ausente.

`status` é `varchar` com `CHECK`, não `enum` do PostgreSQL: adicionar um valor a um
`enum` exige DDL e trava o tipo, e o mapeamento do EF fica mais frágil. O `CHECK`
oferece a mesma garantia com migration trivial.

### `audit_events`

```sql
CREATE TABLE audit_events (
    id             bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entity_type    varchar(32) NOT NULL,
    entity_id      uuid        NOT NULL,
    action         varchar(64) NOT NULL,
    actor          varchar(200) NOT NULL,
    occurred_at    timestamptz NOT NULL,
    correlation_id varchar(64) NOT NULL,
    payload        jsonb       NOT NULL
);

CREATE INDEX ix_audit_entity      ON audit_events (entity_type, entity_id, id DESC);
CREATE INDEX ix_audit_occurred_at ON audit_events (occurred_at DESC);
CREATE INDEX ix_audit_action      ON audit_events (action, id DESC);
```

`bigint identity` em vez de `uuid`: a trilha precisa de **ordem total de escrita** e de
paginação keyset estável, e a sequência dá as duas coisas de graça. A tabela é
append-only pela aplicação — nenhum `UPDATE` ou `DELETE` é emitido contra ela em lugar
nenhum do código.

### `idempotency_keys`

```sql
CREATE TABLE idempotency_keys (
    key             varchar(128) PRIMARY KEY,
    endpoint        varchar(64)  NOT NULL,
    request_hash    char(64)     NOT NULL,      -- SHA-256 do corpo canonicalizado
    status          varchar(16)  NOT NULL,      -- InProgress | Completed
    response_status integer      NULL,
    response_body   jsonb        NULL,
    resource_id     uuid         NULL,
    created_at      timestamptz  NOT NULL,
    expires_at      timestamptz  NOT NULL,

    CONSTRAINT ck_idem_status CHECK (status IN ('InProgress','Completed')),
    CONSTRAINT ck_idem_completed CHECK (
        status <> 'Completed' OR (response_status IS NOT NULL AND response_body IS NOT NULL))
);

CREATE INDEX ix_idem_expires_at ON idempotency_keys (expires_at);
```

A chave é **PK**, e é o índice único dela que serializa duas requisições concorrentes
com a mesma `Idempotency-Key`. Ver [idempotency.md](idempotency.md).

## Concorrência otimista com `xmin`

`rowversion` é do SQL Server e não existe no PostgreSQL. A alternativa nativa é a coluna
de sistema **`xmin`** (o id da transação que escreveu a linha), exposta pelo Npgsql:

```csharp
modelBuilder.Entity<Book>().UseXminAsConcurrencyToken();
```

O EF passa a incluir `WHERE id = @id AND xmin = @xmin` nos `UPDATE`s da entidade e
lança `DbUpdateConcurrencyException` quando 0 linhas são afetadas. O valor é exposto ao
cliente como `ETag` e exigido de volta em `If-Match` no `PATCH /books/{id}`.

Vantagem sobre uma coluna `version` mantida pela aplicação: `xmin` já existe, é mantido
pelo próprio PostgreSQL e não pode ser esquecido em um `UPDATE` escrito à mão.

**`xmin` é usado para edição do catálogo, não para empréstimo.** O porquê está em
[concurrency.md](concurrency.md) — resumo: no empréstimo, um conflito de `xmin` seria um
falso positivo (duas retiradas simultâneas com 50 exemplares deveriam ambas passar).

## Transações

- Todo comando roda dentro de uma transação explícita aberta pelo `TransactionDecorator`
  ([architecture.md](architecture.md#o-pipeline-cqrs)). Query nenhuma abre transação.
- Nível de isolamento: **`READ COMMITTED`** (default do PostgreSQL). Ele basta porque a
  decisão crítica é uma única instrução atômica, e não uma sequência
  ler-decidir-escrever. `SERIALIZABLE` só traria aborts `40001` e retries para resolver
  um problema já resolvido.
- Mudança de estado e evento de auditoria **sempre** na mesma transação. Não existe
  caminho em que o empréstimo é criado e o evento não, ou vice-versa.
- Retry automático apenas para `40001` (serialization failure) e `40P01` (deadlock),
  com backoff exponencial e no máximo 3 tentativas. Erro de negócio **não** é retentado.
- `CancellationToken` propagado em toda a cadeia: cliente que desiste não deixa
  transação aberta segurando linha.

## Migrations

Versionadas em `src/Biblioteca.Api/Infrastructure/Persistence/Migrations/`, aplicadas
**fora** do processo da API (serviço `migrator` no Compose, `Job` com hook
`pre-install`/`pre-upgrade` no Helm). Com 2 a 11 réplicas subindo juntas, aplicar
migration no `Program.cs` é uma corrida entre pods. Ver [operations.md](operations.md).

Constraints que o EF não gera sozinho (`CHECK`, índices parciais, `citext`, `pg_trgm`)
são declaradas em `IEntityTypeConfiguration` com `HasCheckConstraint`/`HasFilter` ou,
quando necessário, `migrationBuilder.Sql(...)` — nunca aplicadas manualmente no banco,
para que o schema do ambiente seja sempre reproduzível a partir do Git.

## O que é verificado por teste

| Invariante | Teste |
|---|---|
| `available_copies` nunca negativo | `LastCopyConcurrencyTests` (20 requisições paralelas) |
| ISBN único | `CreateBookTests.DuplicateIsbn_Returns409` |
| Histórico preservado após devolução/cancelamento | `LoanHistoryTests` |
| Livro com empréstimo ativo não é desativado | `DeactivateBookTests` |
| Dois empréstimos ativos do mesmo livro pelo mesmo usuário | `CreateLoanTests.DuplicateActiveLoan_Returns409` |
| `If-Match` desatualizado → `412` | `UpdateBookConcurrencyTests` |

Detalhes em [testing.md](testing.md).
