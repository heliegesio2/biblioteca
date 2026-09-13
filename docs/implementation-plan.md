# Plano de implementação

Ordem de execução, com critério de pronto por fase. Cada fase é um conjunto de commits
pequenos e coerentes — o enunciado avalia o **histórico**, não só o estado final.

Princípio de sequenciamento: **o que é difícil e arriscado vem primeiro**. O empréstimo
concorrente é o coração do desafio; se algo precisar ser cortado por tempo, o corte
acontece na periferia (busca avançada, filtros extras), nunca no núcleo.

## Fase 0 — Esqueleto

- `Biblioteca.slnx`, `src/Biblioteca.Api`, `tests/Biblioteca.UnitTests`,
  `tests/Biblioteca.IntegrationTests`.
- `global.json` fixando o SDK 10, `Directory.Build.props` (nullable, warnings as errors,
  `TreatWarningsAsErrors`), `Directory.Packages.props` (versões centralizadas).
- `.editorconfig`, `.gitignore`, `.dockerignore`.
- `docker-compose.yml` com `postgres` e `redis` (a API entra na fase 5).

**Pronto quando:** `dotnet build` limpo e `docker compose up postgres redis` saudável.

## Fase 1 — Domínio e persistência

- `Book`, `User`, `Loan`, `LoanPolicy`, `Isbn` — tipos puros, sem EF.
- `BibliotecaDbContext` + `IEntityTypeConfiguration` por entidade, com **todas** as
  constraints de [data-integrity.md](data-integrity.md): `CHECK`, índice único parcial,
  `citext`, `pg_trgm`, `UseXminAsConcurrencyToken`.
- Primeira migration.
- Testes unitários de `LoanPolicy`, `Book`, `Loan` e `Isbn`.

**Pronto quando:** migration aplica em banco limpo e os testes unitários passam. Este é
o ponto em que as invariantes ficam garantidas pelo banco — tudo depois se apoia nisso.

## Fase 2 — Infraestrutura CQRS

- `ICommand`/`IQuery`, handlers, `Dispatcher`, registro por scan (`Scrutor`).
- Decorators na ordem definida em
  [architecture.md](architecture.md#o-pipeline-cqrs): Metrics → Validation →
  CacheInvalidation → Transaction → Idempotency.
- `Result<T>` + mapeamento para Problem Details (RFC 9457).
- `CorrelationIdMiddleware`.

**Pronto quando:** um comando trivial atravessa o pipeline inteiro com transação,
validação e Problem Details funcionando.

## Fase 3 — Catálogo

- `CreateBook`, `UpdateBook` (com `If-Match`/`xmin`), `DeactivateBook`.
- `GetBookById` (com `ETag`), `SearchBooks` (keyset), `GetAvailability`, `GetBookHistory`.
- `AuditWriter` + eventos de livro.
- Testes de integração: CRUD, ISBN duplicado, `412`/`428`, soft delete com empréstimo
  ativo.

**Pronto quando:** `CatalogTests`, `UpdateBookConcurrencyTests` e `DeactivateBookTests`
passam contra PostgreSQL real.

## Fase 4 — Empréstimos (o núcleo)

1. `CreateUser`, `GetUserLoans`.
2. `CreateLoan` com o `UPDATE` condicional atômico + auditoria na mesma transação.
3. **`LastCopyConcurrencyTests`** — 20 requisições paralelas. Este teste precisa passar
   antes de qualquer outra coisa ser acrescentada.
4. `IdempotencyStore` + `IdempotencyDecorator`; `IdempotencyTests` (5 cenários).
5. `ReturnLoan`, `CancelLoan` com transição condicional; `LoanHistoryTests`.

**Pronto quando:** os três testes exigidos pelo enunciado (concorrência, idempotência,
preservação de histórico) passam. Aqui o desafio está essencialmente resolvido.

## Fase 5 — Cache

- `HybridCache` + Redis, chaves e TTLs de [caching.md](caching.md).
- `ICacheInvalidationQueue` drenada pós-commit.
- `CacheTests`, incluindo o cenário com Redis parado.

**Pronto quando:** empréstimo e devolução refletem imediatamente em
`GET /availability`, e a API continua respondendo com o Redis fora do ar.

## Fase 6 — Auditoria e consulta

- `GET /audit-events` com filtros e paginação keyset.
- `AuditTests`: um evento por mudança, `from`/`to` em `BookUpdated`, UTC, correlação.

**Pronto quando:** a trilha de qualquer livro reconstrói a história do seu estoque.

## Fase 7 — Segurança

- JWT Bearer, `POST /auth/token` (não mapeado em `Production`), papéis.
- `SameUserOrLibrarian` como `IAuthorizationHandler`.
- `actor` real chegando à auditoria.
- `AuthorizationTests`.

**Pronto quando:** um `member` não alcança recurso de terceiro nem `/audit-events`.

## Fase 8 — Observabilidade

- OpenTelemetry (traces + métricas), instrumentação de ASP.NET Core, Npgsql e Redis.
- `LoanMetrics` com os quatro instrumentos exigidos.
- Logs JSON estruturados; health checks `live`/`ready` com a semântica de
  [observability.md](observability.md#health-checks).
- `HealthTests`, `MetricsTests`.

**Pronto quando:** `/health/live` responde com o Postgres parado e `/health/ready` não
reprova com o Redis parado.

## Fase 9 — Empacotamento e operação

- `Dockerfile` multi-stage, serviço `api` e `migrator` no Compose.
- Helm chart completo (`Deployment`, `Service`, `ConfigMap`, referência a `Secret`,
  `Job` de migration, HPA, PDB), `helm lint` limpo.
- `README` revisado contra a
  [rastreabilidade](requirements-traceability.md) e ADRs fechadas.

**Pronto quando:** `docker compose up --build` sobe tudo do zero em uma máquina limpa e
`helm template` renderiza sem erro.

## Fase 10 — Revisão final

- Rodar a suíte inteira em máquina limpa.
- Conferir cada linha de [requirements-traceability.md](requirements-traceability.md).
- Revisar o histórico de commits e a ausência de segredos versionados
  (`git log -p | grep -i` por padrões de credencial).
- Atualizar limitações conhecidas com o que efetivamente ficou de fora.

## Ordem dos commits

Um commit por unidade coerente, com mensagem explicando **por quê** quando a decisão não
é óbvia. Exemplos:

```
feat(catalog): adiciona Book com invariantes de exemplares
feat(db): CHECK e índice único parcial para garantir estoque não-negativo
feat(loans): empréstimo com UPDATE condicional atômico
test(loans): 20 requisições paralelas no último exemplar
feat(loans): idempotência de POST /loans na mesma transação do empréstimo
fix(cache): invalida apenas após o commit para não repopular valor antigo
docs(readme): estratégia de concorrência e trade-offs descartados
```

## Riscos e planos de contenção

| Risco | Contenção |
|---|---|
| Teste de concorrência instável (*flaky*) | `Barrier` para disparo simultâneo, 20 usuários distintos, asserções no banco e não só nas respostas HTTP |
| Testcontainers lento ou indisponível em CI | Containers compartilhados por collection; suíte unitária roda isolada sem Docker |
| `HybridCache` + invalidação L1 entre réplicas | TTL local curto e explícito (5s), documentado como janela conhecida |
| Escopo inflando (renovação, multas, reservas) | Fora do escopo por decisão registrada; `LoanPolicy` é o ponto de extensão |
| Divergência entre docs e código | [requirements-traceability.md](requirements-traceability.md) revisado na fase 10 |
