# Testes

Testes mínimos exigidos pelo enunciado: unitários das regras de negócio, integração com
PostgreSQL real ou containerizado, teste concorrente do último exemplar, teste de
idempotência e teste que comprove a preservação do histórico após devolução/cancelamento.
Todos existem, e a rastreabilidade está na [tabela final](#rastreabilidade).

## Estratégia

Dois projetos, com propósitos que não se misturam:

| Projeto | O que testa | Dependências | Tempo alvo |
|---|---|---|---|
| `Biblioteca.UnitTests` | Regras de domínio puras | Nenhuma — sem I/O, sem Docker | < 1s |
| `Biblioteca.IntegrationTests` | API de ponta a ponta | PostgreSQL e Redis reais (Testcontainers) | ~40s incluindo subida dos containers |

Não existe camada intermediária de "testes de serviço com mocks de repositório". Ela
testaria, principalmente, se os mocks foram configurados corretamente. O que decide a
corretude deste sistema — `UPDATE` condicional, índice único parcial, `CHECK` constraint,
comportamento de `READ COMMITTED` — **só existe no banco**, e um duplo não reproduz nada
disso. Ver [ADR-0006](adr/0006-testes-com-testcontainers.md).

Pela mesma razão, o provider InMemory do EF Core não é usado em lugar nenhum: ele não
tem transações reais, não valida constraints e não reproduz concorrência. Um teste de
"último exemplar" contra ele passaria sempre — inclusive com o código errado.

## Testes unitários

Alvo: os tipos de `Features/*/Domain/` e os validators.

```
LoanPolicyTests
  ├── DueDate_Is_BorrowedAt_Plus_ConfiguredPeriod
  ├── Rejects_When_User_Has_Max_Active_Loans
  ├── Rejects_When_User_Is_Inactive
  ├── Rejects_When_Book_Is_Inactive
  └── Allows_When_Under_Limit
LoanStateTests
  ├── Return_From_Active_Sets_ReturnedAt_And_Status
  ├── Return_From_Returned_Fails_With_LoanNotActive
  ├── Cancel_From_Returned_Fails
  └── Cancel_From_Active_Sets_CancelledAt
BookTests
  ├── Borrow_Decrements_Available
  ├── Borrow_With_Zero_Available_Fails
  ├── IncreaseTotalCopies_Increases_Available_By_Delta
  ├── DecreaseTotalCopies_Below_ActiveLoans_Fails
  └── Deactivate_With_Active_Loans_Fails
IsbnTests
  ├── Normalizes_Hyphens_And_Case
  ├── Accepts_Valid_Isbn10_And_Isbn13
  └── Rejects_Invalid_CheckDigit
EtagTests / ValidatorTests
```

O tempo é controlado por `TimeProvider` injetado (`FakeTimeProvider`), o que permite
testar vencimento e `daysLate` sem esperar 14 dias nem depender do relógio da máquina de
CI.

## Testes de integração

`WebApplicationFactory<Program>` com o host real — mesmo pipeline, mesmos decorators,
mesma configuração — apontando para containers efêmeros:

```csharp
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        // migrations aplicadas uma vez, como em produção: fora do start da API
        await MigrateAsync(_postgres.GetConnectionString());
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString())
             .UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString()));
    }
}
```

Os containers sobem uma vez por *collection* (não por teste) e o estado é limpo entre
testes com **Respawn**, que trunca as tabelas de dados preservando o schema. Recriar o
banco a cada teste multiplicaria o tempo por dez sem ganho de isolamento.

Testes que exigem paralelismo real (concorrência, idempotência) rodam em uma collection
própria, sem compartilhamento de dados com os demais.

### O teste de concorrência

O teste que dá sentido a toda a escolha de arquitetura:

```csharp
[Fact]
public async Task LastCopy_TwentyConcurrentRequests_CreatesExactlyOneLoan()
{
    var bookId = await CreateBook(totalCopies: 1);
    var users  = await CreateUsers(20);          // usuários distintos:
                                                 // o que rejeita tem de ser o estoque,
                                                 // não o índice único parcial
    using var barrier = new Barrier(20);
    var responses = await Task.WhenAll(users.Select(async u =>
    {
        barrier.SignalAndWait();                 // dispara todas no mesmo instante
        return await Client.PostLoan(bookId, u, idempotencyKey: Guid.NewGuid());
    }));

    responses.Count(r => r.StatusCode is HttpStatusCode.Created).Should().Be(1);
    responses.Count(r => r.StatusCode is HttpStatusCode.Conflict).Should().Be(19);
    responses.Where(r => r.IsConflict)
             .Should().OnlyContain(r => r.Problem.Code == "book-unavailable");

    (await GetAvailability(bookId)).AvailableCopies.Should().Be(0);
    (await CountLoansInDb(bookId, LoanStatus.Active)).Should().Be(1);
    (await CountAuditEvents(bookId, "LoanCreated")).Should().Be(1);
}
```

As cinco asserções cobrem exatamente o que o enunciado pede: **exatamente um** sucesso,
rejeição **clara de regra de negócio** (o `code`, não só o status), **nenhuma quantidade
negativa**, **nenhum empréstimo duplicado** e — extra — trilha de auditoria coerente com
o resultado.

### Demais testes de integração

| Arquivo | Cobre |
|---|---|
| `CatalogTests` | CRUD, ISBN único, normalização de ISBN, busca, paginação keyset |
| `UpdateBookConcurrencyTests` | `If-Match` correto → `200`; desatualizado → `412`; ausente → `428` |
| `DeactivateBookTests` | Com empréstimo ativo → `409`; sem → `204` e sai da listagem; já desativado → `204` |
| `CreateLoanTests` | Caminho feliz, `dueAt` correto, limite por usuário, livro/usuário inativo, empréstimo duplicado do mesmo livro |
| `LastCopyConcurrencyTests` | O teste acima + devolução concorrente |
| `IdempotencyTests` | Os cinco cenários de [idempotency.md](idempotency.md#como-isso-é-testado) |
| `LoanHistoryTests` | Preservação do histórico após devolução e cancelamento |
| `AuditTests` | Um evento por mudança, `actor`/`correlationId`/UTC corretos, `from`/`to` do `BookUpdated`, filtros de consulta |
| `CacheTests` | Hit/miss, invalidação pós-commit, Redis fora do ar, `304` com `If-None-Match` |
| `HealthTests` | `live` responde com o Postgres parado; `ready` não reprova com o Redis parado, mas reprova com o Postgres parado |
| `AuthorizationTests` | `member` não acessa empréstimo de terceiro (`403`), não acessa `/audit-events`, não escreve no catálogo |
| `ProblemDetailsTests` | Formato RFC 9457, `correlationId` presente, `X-Correlation-Id` ecoado |
| `ArchitectureTests` | Domínio não referencia EF/HTTP/cache; um slice não referencia outro |

### Teste de preservação do histórico

```csharp
[Fact]
public async Task History_IsPreserved_After_Return_And_Cancel()
{
    var (bookId, userA, userB) = await SeedBookWithTwoCopies();
    var loanA = await Client.CreateLoan(bookId, userA);
    var loanB = await Client.CreateLoan(bookId, userB);

    await Client.ReturnLoan(loanA);
    await Client.CancelLoan(loanB, reason: "erro de registro");

    var history = await Client.GetBookHistory(bookId);
    history.Items.Should().HaveCount(2);                       // nada sumiu
    history.Single(l => l.Id == loanA).Status.Should().Be("Returned");
    history.Single(l => l.Id == loanA).BorrowedAt.Should().NotBe(default);
    history.Single(l => l.Id == loanB).Status.Should().Be("Cancelled");

    (await GetAvailability(bookId)).AvailableCopies.Should().Be(2); // ambos voltaram

    var audit = await Client.GetAuditEvents(entityType: "Loan");
    audit.Actions.Should().Contain(["LoanCreated", "LoanReturned", "LoanCancelled"]);
}
```

## Execução

```bash
dotnet test                                    # tudo; integração exige Docker
dotnet test tests/Biblioteca.UnitTests         # rápido, sem Docker
dotnet test --filter Category=Concurrency      # só os de concorrência
```

Os testes de integração **não** dependem de `docker compose up` prévio nem de banco
compartilhado: cada execução sobe e destrói os próprios containers, o que os torna
reproduzíveis na máquina de qualquer pessoa e em CI sem serviço externo.

## Rastreabilidade

| Exigência do enunciado | Onde |
|---|---|
| Testes unitários das regras de negócio | `Biblioteca.UnitTests` — `LoanPolicyTests`, `LoanStateTests`, `BookTests`, `IsbnTests` |
| Integração com PostgreSQL real/containerizado | `Biblioteca.IntegrationTests` inteiro (Testcontainers, `postgres:17-alpine`) |
| Teste concorrente do último exemplar | `LastCopyConcurrencyTests.LastCopy_TwentyConcurrentRequests_CreatesExactlyOneLoan` |
| Teste de idempotência | `IdempotencyTests` (5 cenários, incluindo 10 requisições paralelas) |
| Preservação do histórico após devolução/cancelamento | `LoanHistoryTests.History_IsPreserved_After_Return_And_Cancel` |

Mapa completo requisito → implementação → teste:
[requirements-traceability.md](requirements-traceability.md).

## O que não é testado, e por quê

- **Cobertura de linha não é meta.** Nenhum portão de percentual: um número alto obtido
  testando mapeamento de DTO não diz nada sobre a corretude do empréstimo concorrente.
- **Carga e desempenho**: exigiriam ambiente dedicado e baseline; estão na
  [evolução](../README.md#10-evolução-para-produção).
- **O Helm chart não é validado por teste automatizado** além de `helm lint` —
  renderizar e aplicar em cluster efêmero (kind) seria o passo seguinte em CI.
