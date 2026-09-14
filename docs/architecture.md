# Arquitetura

## Princípio

O desafio é pequeno e as decisões difíceis estão concentradas em **um caso de uso**:
criar um empréstimo. O enunciado diz que a adoção de um padrão não é avaliada
isoladamente — então a estrutura existe para resolver problemas concretos daqui.

Duas escolhas se combinam:

1. **Organização por feature** (vertical slices) em um único projeto de API. Cada
   feature carrega seu domínio, seus comandos, suas queries e seus endpoints.
2. **CQRS explícito** dentro de cada feature: escrita e leitura são tipos diferentes,
   com pipelines diferentes. Ver [ADR-0001](adr/0001-cqrs-vertical-slices.md).

A separação comando/query não é decoração: os dois caminhos têm exigências
**opostas** neste sistema.

| | Comando (`POST`, `PATCH`, `DELETE`) | Query (`GET`) |
|---|---|---|
| Transação | Explícita, obrigatória, com retry | Nenhuma |
| Rastreamento do EF | Sim | `AsNoTracking` |
| Fonte | Sempre PostgreSQL | Cache (Redis) quando aplicável |
| Idempotência | `Idempotency-Key` em `POST /loans` | Irrelevante (já é idempotente) |
| Auditoria | Escreve evento na mesma transação | Não escreve |
| Cache | Invalida depois do commit | Popula |

Amarrar esses dois fluxos no mesmo "service" obrigaria cada método a decidir em
runtime se abre transação, se invalida cache, se grava auditoria. Separando-os, cada
preocupação vira um **decorator** aplicado só a quem precisa — e a ordem entre elas
fica escrita em um lugar só, em vez de repetida em cada handler.

A regra de negócio **não** mora nos endpoints nem nos handlers: ela vive em tipos puros
(`Book`, `Loan`, `LoanPolicy`) que não conhecem EF Core, HTTP ou Redis. É isso que
torna os testes unitários rápidos e a política de empréstimo legível em um só lugar.

## Organização

```
src/Biblioteca.Api/
├── Program.cs                              # composição: DI, pipeline HTTP, endpoints
├── Features/
│   ├── Catalog/
│   │   ├── Domain/
│   │   │   ├── Book.cs                     # entidade + invariantes (sem EF)
│   │   │   └── Isbn.cs                     # value object: normalização e validação
│   │   ├── Commands/
│   │   │   ├── CreateBook.cs               # command + validator + handler
│   │   │   ├── UpdateBook.cs               # PATCH com If-Match (xmin)
│   │   │   └── DeactivateBook.cs           # DELETE = soft delete
│   │   ├── Queries/
│   │   │   ├── GetBookById.cs              # cacheada + ETag
│   │   │   ├── SearchBooks.cs              # paginada, não cacheada
│   │   │   ├── GetAvailability.cs          # cacheada, TTL curto
│   │   │   └── GetBookHistory.cs
│   │   ├── Contracts/                      # DTOs de request/response do slice
│   │   ├── BookCache.cs                    # chaves, TTLs e invalidação do slice
│   │   └── CatalogEndpoints.cs             # rotas /books → dispatcher
│   ├── Users/
│   │   ├── Domain/User.cs
│   │   ├── Commands/CreateUser.cs
│   │   ├── Queries/GetUserLoans.cs
│   │   └── UserEndpoints.cs
│   ├── Loans/
│   │   ├── Domain/
│   │   │   ├── Loan.cs                     # entidade + máquina de estados
│   │   │   └── LoanPolicy.cs               # prazo, limite por usuário, transições
│   │   ├── Commands/
│   │   │   ├── CreateLoan.cs               # o caso de uso crítico
│   │   │   ├── ReturnLoan.cs
│   │   │   └── CancelLoan.cs
│   │   └── LoanEndpoints.cs
│   ├── Audit/
│   │   ├── Domain/AuditEvent.cs
│   │   ├── AuditActions.cs                 # vocabulário fechado de ações
│   │   ├── AuditWriter.cs                  # grava na transação corrente
│   │   ├── Queries/SearchAuditEvents.cs
│   │   └── AuditEndpoints.cs               # GET /audit-events
│   └── Auth/
│       ├── Commands/IssueToken.cs          # POST /auth/token (só em Development)
│       └── AuthEndpoints.cs
├── Infrastructure/
│   ├── Cqrs/
│   │   ├── ICommand.cs                     # ICommand<TResult>, IQuery<TResult>
│   │   ├── IHandlers.cs                    # ICommandHandler<,>, IQueryHandler<,>
│   │   ├── Dispatcher.cs                   # resolve o handler pelo DI
│   │   ├── CqrsRegistration.cs             # scan de handlers + ordem dos decorators
│   │   └── Decorators/
│   │       ├── MetricsDecorator.cs
│   │       ├── ValidationDecorator.cs
│   │       ├── CacheInvalidationDecorator.cs
│   │       ├── TransactionDecorator.cs
│   │       └── IdempotencyDecorator.cs
│   ├── Persistence/
│   │   ├── BibliotecaDbContext.cs
│   │   ├── Configurations/                 # IEntityTypeConfiguration por entidade
│   │   └── Migrations/                     # versionadas no Git
│   ├── Idempotency/
│   │   ├── IdempotencyKey.cs
│   │   └── IdempotencyStore.cs             # reserva/replay dentro da transação
│   ├── Caching/
│   │   ├── CacheSetup.cs                   # HybridCache + Redis, TTLs, serialização
│   │   └── CacheInvalidationQueue.cs       # fila scoped, drenada após o commit
│   ├── Observability/
│   │   ├── TelemetrySetup.cs               # OpenTelemetry, logs, health checks
│   │   ├── LoanMetrics.cs                  # métricas de domínio
│   │   └── CorrelationIdMiddleware.cs
│   └── Http/
│       ├── ProblemDetailsSetup.cs          # RFC 9457 + mapeamento de erros de domínio
│       ├── ETag.cs                         # xmin → ETag, validação de If-Match
│       ├── CurrentActor.cs                 # JWT → ator da auditoria
│       └── ResultExtensions.cs             # Result<T> do handler → IResult HTTP
├── appsettings.json
└── appsettings.Development.json
```

Regras de dependência (verificadas por um teste de arquitetura):

- `Features/*` pode usar `Infrastructure/*`; o contrário nunca acontece.
- Um slice não referencia os tipos internos de outro slice. O que for compartilhado sobe
  para `Infrastructure/`.
  Exceções declaradas:
  - `Loans` lê e escreve `Book.AvailableCopies` — na prática é o mesmo agregado
    transacional, e fingir o contrário exigiria uma indireção inútil.
  - `Catalog` lê `Loans` (status ativo) em três pontos: `DeactivateBook` precisa
    recusar (`409 book-has-active-loans`) quando o livro tem empréstimo ativo,
    `GetAvailability` conta empréstimos ativos para o campo `activeLoans`, e
    `GetBookHistory` lista os empréstimos do livro. As três são leituras diretas via
    `BibliotecaDbContext` (sem chamar handler ou domínio de `Loans`) — é a mesma
    natureza da consulta que já atravessa tabelas livremente, não um acoplamento de
    regra de negócio entre os dois slices.
  - `Users` lê `Loans` (todos os status) em `GetUserLoans` — mesma natureza de
    projeção do item anterior.
  - `Loans` usa `Catalog.BookCache` (nomes de chave, não lógica) para enfileirar a
    invalidação do livro afetado em `CreateLoan`/`ReturnLoan`/`CancelLoan` — é o
    mesmo par chave/livro que `Catalog` já invalida em `UpdateBook`/`DeactivateBook`;
    duplicar a convenção de nome em vez de compartilhá-la é que criaria divergência.
- `Features/*/Domain/*` não referencia `Microsoft.EntityFrameworkCore`, `HttpContext`
  nem `HybridCache`.
- Um handler nunca chama outro handler. Lógica comum vai para o domínio.

## O pipeline CQRS

Sem MediatR. `ICommand<TResult>` / `IQuery<TResult>` são interfaces marcadoras, o
`Dispatcher` resolve `ICommandHandler<TCommand, TResult>` pelo container e os
*cross-cutting concerns* são **decorators registrados no DI** (via `Scrutor`). São ~80
linhas de infraestrutura, sem dependência externa de runtime e sem a questão de
licenciamento comercial do MediatR v13+. Ver [ADR-0002](adr/0002-sem-mediatr.md).

```
COMANDO
  Dispatcher
   └─ Metrics            duração + contadores por resultado
       └─ Validation     FluentValidation; falha → 400 sem tocar no banco
           └─ CacheInvalidation   drena a fila DEPOIS do commit
               └─ Transaction     BEGIN … COMMIT/ROLLBACK, retry em 40001/deadlock
                   └─ Idempotency reserva a chave e grava o snapshot da resposta
                       └─ Handler regra de negócio + auditoria

QUERY
  Dispatcher
   └─ Metrics
       └─ Validation
           └─ Handler    AsNoTracking, cache quando aplicável
```

A ordem não é arbitrária; cada posição resolve um problema:

- **Validation antes de Transaction**: input inválido não abre transação no banco.
- **Idempotency dentro de Transaction**: a reserva da chave e o efeito que ela protege
  precisam ser atômicos. Se a transação der rollback, a chave some junto e o cliente
  pode repetir. Ver [idempotency.md](idempotency.md).
- **CacheInvalidation fora de Transaction**: invalidar antes do commit permitiria que uma
  leitura concorrente repopulasse o cache com o valor **antigo**. Os handlers só
  enfileiram as chaves a invalidar (`ICacheInvalidationQueue`, scoped); o decorator drena
  a fila depois do commit bem-sucedido. Ver [caching.md](caching.md).
- **Retry dentro de Transaction, não fora**: só o que é transacional é reexecutado.

Comandos que não são idempotentes por natureza implementam `IIdempotentCommand`
(hoje, apenas `CreateLoanCommand`); o decorator ignora os demais.

### Como um endpoint fica

```csharp
group.MapPost("/loans", async (
        CreateLoanRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        IDispatcher dispatcher,
        CancellationToken ct) =>
    {
        var result = await dispatcher.Send(
            new CreateLoanCommand(request.BookId, request.UserId, idempotencyKey), ct);
        return result.ToHttpResult(loan => Results.Created($"/loans/{loan.Id}", loan));
    })
    .RequireAuthorization()
    .WithName("CreateLoan");
```

O endpoint faz três coisas: traduzir HTTP em comando, despachar e traduzir o resultado
de volta em HTTP. Nada de regra de negócio, nada de `DbContext`, nada de `try/catch`.

Handlers devolvem `Result<T>` (sucesso ou erro de domínio tipado) em vez de lançar
exceção para fluxo esperado. `ResultExtensions` mapeia erro de domínio → Problem Details:
exceção fica reservada para o que é realmente excepcional. Ver
[api-contract.md](api-contract.md).

## Fluxo de um request

```
HTTP ─▶ CorrelationId ─▶ Autenticação ─▶ Autorização ─▶ Endpoint ─▶ Dispatcher
                                                                        │
                        ┌───────────────────────────────────────────────┤
                        ▼                                               ▼
                     QUERY                                          COMANDO
                        │                                               │
              If-None-Match bate? ──sim──▶ 304                  BEGIN TRANSACTION
                        │ não                                          │
                 HybridCache L1 ──▶ L2 (Redis)                  reserva idempotência
                        │ miss                                         │
                        ▼                                      UPDATE condicional
                   PostgreSQL                                          │
                        │                                       INSERT loan + audit
                        ▼                                              │
                 resposta + ETag                                    COMMIT
                                                                       │
                                                        invalidação do cache (pós-commit)
```

- **Leituras de catálogo** passam pelo cache antes do banco e respondem com `ETag`;
  um `If-None-Match` que bate devolve `304` sem tocar em Redis nem PostgreSQL.
- **Empréstimo, devolução e cancelamento** alteram `books.available_copies` e `loans`
  na mesma transação — nunca em duas. Ver [concurrency.md](concurrency.md).

## Limites do sistema

```
   ┌──────────┐   JWT    ┌──────────────────┐
   │ Cliente  │ ───────▶ │ Biblioteca.Api   │  2..11 réplicas, stateless
   └──────────┘          └───┬──────────┬───┘
                             │          │
                 ┌───────────▼──┐   ┌───▼────────────┐
                 │ PostgreSQL   │   │ Redis          │
                 │ fonte da     │   │ dados derivados│
                 │ verdade      │   │ e descartáveis │
                 └──────────────┘   └────────────────┘
```

**PostgreSQL é a única fonte da verdade.** Redis guarda apenas dados derivados:
perder o Redis inteiro degrada latência, nunca corretude. Nenhuma decisão de negócio
— há exemplar disponível? o usuário pode pegar emprestado? — é tomada a partir do
cache; todas acontecem dentro da transação no PostgreSQL.

Note que o CQRS aqui **não** significa bancos separados nem event sourcing: comandos e
queries usam o mesmo PostgreSQL e o mesmo `DbContext`. A separação é de *caminho de
código*, não de armazenamento — e é por isso que não existe atraso de projeção nem
leitura eventualmente consistente fora do cache, cujo TTL é explícito e curto.

A API é **stateless**: sem sessão, sem lock em memória, sem trabalho agendado preso a
um pod. Qualquer réplica atende qualquer request.

Única exceção declarada: o `BackgroundService` que expira chaves de idempotência roda
em todas as réplicas. É seguro porque a operação é um `DELETE` condicional idempotente
(`WHERE expires_at < now()`) — rodar N vezes tem o mesmo efeito de rodar uma vez.

## Versionamento da API

As rotas seguem o contrato sugerido no enunciado, sem prefixo de versão, para manter a
comparação direta com o que foi pedido. Uma mudança incompatível futura entraria em
`/v2/...`; mudanças compatíveis (campo opcional novo, endpoint novo) entram no contrato
atual. Ver [api-contract.md](api-contract.md).

## Bibliotecas auxiliares

| Biblioteca | Problema concreto que resolve |
|---|---|
| `Npgsql.EntityFrameworkCore.PostgreSQL` | Provider obrigatório; dá acesso a `xmin`, `CHECK` constraints e índices parciais |
| `Microsoft.Extensions.Caching.Hybrid` | Cache em dois níveis com proteção contra *stampede* — sem ele, um miss em chave quente vira N queries simultâneas |
| `Scrutor` | Registro dos handlers por scan e, principalmente, `Decorate<,>` para montar o pipeline CQRS |
| `FluentValidation` | Validação declarativa, testável fora do pipeline HTTP |
| `OpenTelemetry.*` | Traces e métricas em padrão aberto, sem acoplar a vendor |
| `Testcontainers` | PostgreSQL e Redis reais nos testes — o teste de concorrência não teria valor contra um banco em memória |
| `Respawn` | Reset rápido do banco entre testes de integração |
| `Scalar.AspNetCore` | UI sobre o OpenAPI nativo do .NET 10 (o Swashbuckle deixou de ser o default) |
