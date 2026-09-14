# Rastreabilidade dos requisitos

Cada exigência do enunciado → onde é atendida → como é verificada. Serve de checklist de
entrega e de roteiro de revisão.

Legenda: ✅ documentado e planejado · 🔨 em implementação · ✔ concluído e testado.

## 1. Catálogo

| Requisito | Implementação | Teste | Status |
|---|---|---|---|
| Criar, consultar, atualizar e listar livros | `POST/GET/PATCH /books`, `GET /books/{id}` | `CatalogTests` | ✔ |
| Identificar por título, ISBN, autor e quantidade de exemplares | `Book` ([domain-model.md](domain-model.md#book)) | `BookTests` | ✔ |
| Unicidade de ISBN | `UNIQUE INDEX ux_books_isbn` + normalização em `Isbn` | `CatalogTests.CreateBook_IsbnDuplicado_Retorna409`, `IsbnTests` | ✔ |
| Desativar ou rejeitar remoção de livro com histórico; histórico não pode ser apagado silenciosamente | `DELETE` = soft delete; `409` com empréstimo ativo; FK `ON DELETE RESTRICT` | `DeactivateBookTests` | ✔ |

## 2. Usuários e empréstimos

| Requisito | Implementação | Teste | Status |
|---|---|---|---|
| Cadastrar usuário/leitor | `POST /users` | `UserTests` | ✔ |
| Emprestar exemplar disponível | `POST /loans` → `UPDATE` condicional | `CreateLoanTests` | ✔ |
| Registrar data do empréstimo e prevista de devolução | `borrowed_at`, `due_at` (`Loans:LoanPeriodDays`) | `LoanPolicyTests.ComputeDueAt_SomaOPrazoConfigurado`, `CreateLoanTests.CaminhoFeliz_Retorna201ComDueAtCorreto` | ✔ |
| Devolver empréstimo | `POST /loans/{id}/return` | `LoanHistoryTests`, `IdempotencyTests` | ✔ |
| Cancelar preservando a informação de que existiu | `POST /loans/{id}/cancel` → status `Cancelled`, linha preservada | `LoanHistoryTests` | ✔ |
| Consultar disponibilidade atual | `GET /books/{id}/availability` (cacheado, TTL 30s) | `LoanHistoryTests`, `CacheTests` | ✔ |
| Histórico por livro e por usuário | `GET /books/{id}/history`, `GET /users/{id}/loans` | `LoanHistoryTests` | ✔ |

## 3. Concorrência e consistência

| Requisito | Implementação | Teste | Status |
|---|---|---|---|
| Exatamente um empréstimo bem-sucedido no último exemplar | `UPDATE … WHERE available_copies > 0` em `READ COMMITTED` | `LastCopyConcurrencyTests` | ✔ |
| Rejeição **clara de regra de negócio** para a outra tentativa | `409 book-unavailable` em Problem Details | idem (asserção sobre o `code`) | ✔ |
| Nenhuma quantidade negativa, empréstimo duplicado ou estado inconsistente | `CHECK` + índice único parcial + transação única | idem + `CreateLoanTests.EmprestimoDuplicadoDoMesmoLivro_Retorna409` | ✔ |
| Cenário automatizado em teste de integração | `LastCopyConcurrencyTests` com 20 requisições paralelas | — | ✔ |
| Estratégia e trade-offs documentados no README | [README §8.1](../README.md#81-concorrência--o-último-exemplar) + [concurrency.md](concurrency.md) | — | ✔ |
| **Não** usar `rowversion` | `xmin` (nativo do PostgreSQL) na edição de catálogo; `rowversion` descartado explicitamente | `UpdateBookConcurrencyTests` | ✔ |

## 4. Idempotência

| Requisito | Implementação | Teste | Status |
|---|---|---|---|
| `POST /loans` aceita `Idempotency-Key` | Obrigatório; ausente → `400` ([desvio documentado](api-contract.md#desvios-do-contrato-sugerido)) | `IdempotencyTests.MissingKey_Retorna400` | ✔ |
| Repetir não cria dois empréstimos | `idempotency_keys` com PK, na mesma transação | `IdempotencyTests.SameKey_SameBody_Sequential`, `SameKey_Concurrent_TenRequests` | ✔ |
| Repetir não reduz disponibilidade duas vezes | idem — o handler não roda no replay | idem (asserção sobre `availableCopies`) | ✔ |
| Devolver a resposta anterior ou equivalente | Snapshot de status + corpo, com `Idempotency-Replayed: true` | `IdempotencyTests.SameKey_SameBody_Sequential` | ✔ |

## 5. Auditoria de domínio

| Requisito | Implementação | Teste | Status |
|---|---|---|---|
| Criação e alteração de livro | `BookCreated`, `BookUpdated` (com `from`/`to`) | `AuditTests` | ✔ |
| Desativação de livro | `BookDeactivated` | `AuditTests` | ✔ |
| Criação de empréstimo | `LoanCreated` | `AuditTests`, `LastCopyConcurrencyTests` | ✔ |
| Devolução de empréstimo | `LoanReturned` | `AuditTests`, `LoanHistoryTests` | ✔ |
| Cancelamento de empréstimo | `LoanCancelled` (com `reason`) | `LoanHistoryTests` | ✔ |
| Entidade, id, ação, ator, timestamp UTC, correlação e informação suficiente | Esquema de `audit_events` ([auditing.md](auditing.md#esquema-do-evento)) | `AuditTests` | ✔ |
| Trilha de negócio ≠ log técnico | Tabela no PostgreSQL, gravada **na transação** | `AuditTests` (evento existe ⟺ mudança existe) | ✔ |
| Consulta com filtros e paginação | `GET /audit-events` | `AuditTests` | ✔ |

## 6. Cache

| Requisito | Implementação | Teste | Status |
|---|---|---|---|
| Redis em ao menos uma consulta de leitura | `GET /books/{id}` e `GET /books/{id}/availability` via `HybridCache` | `CacheTests` | ✔ |
| Invalidar/atualizar coerentemente em empréstimo, devolução e alteração de quantidade | Invalidação explícita **pós-commit** por `CacheInvalidationCommandDecorator` | `CacheTests.Loan_InvalidatesAvailability`, `Return_InvalidatesAvailability`, `PatchBook_InvalidatesBook` | ✔ |
| PostgreSQL como fonte de verdade para decisão | O caminho de escrita nunca lê o cache | `LastCopyConcurrencyTests` (correto mesmo com cache quente) | ✔ |
| Redis fora do ar degrada, não derruba | Leitura embrulhada em try/catch, cai para o banco | `CacheTests.RedisDown_ReadsStillWork` | ✔ |

## 7. Segurança

| Requisito | Implementação | Teste | Status |
|---|---|---|---|
| Autenticação com ator para a auditoria | JWT Bearer (`sub`/`email`/`role`); `POST /auth/token` fora de Production ([security.md](security.md#autenticação)) | `AuthorizationTests`, `AuditTests` | ✔ |
| Papéis `librarian`/`member` | Policy `Librarian` por papel; fallback exige usuário autenticado por padrão | `AuthorizationTests` | ✔ |
| Acesso a recurso de terceiro (`member` só o próprio) | `SameUserOrLibrarianRequirement`, autorização por recurso nos endpoints | `AuthorizationTests.Member_NaoConsegueVerEmprestimosDeOutroUsuario` e afins | ✔ |
| Ator real (não `"system"`) chegando à auditoria | `ICurrentActor` a partir do claim `email` | `AuditTests.CreateBook_GeraEventoBookCreated_ComCorrelationIdEUtc` | ✔ |
| Recusa subir em Production com chave fraca ou de desenvolvimento | Checagem em `Program.cs` (< 32 bytes ou igual à chave dev) | — (exigiria rodar de fato com `ASPNETCORE_ENVIRONMENT=Production`) | ✔ |
| Nenhum segredo real versionado | Só a chave de desenvolvimento em `appsettings.Development.json`, marcada como tal | — | ✔ |

## 8. API e configuração

| Requisito | Implementação | Status |
|---|---|---|
| Operações assíncronas com `CancellationToken` propagado | Toda a cadeia endpoint → dispatcher → handler → EF/Redis | ✅ |
| Status HTTP adequados e erros consistentes (Problem Details) | RFC 9457 com `code`, `correlationId`, `traceId` ([api-contract.md](api-contract.md#erros--rfc-9457-problem-details)) | ✅ |
| Configuração por `appsettings.*.json` e variáveis de ambiente | Precedência padrão + `ValidateOnStart` | ✅ |
| Não versionar segredos | Só credenciais locais do Compose; `Secret` referenciado por nome no Helm ([security.md](security.md#segredos)) | ✅ |
| Migrations versionadas | `Infrastructure/Persistence/Migrations/` no Git | ✅ |

## 9. Health checks e telemetria

| Requisito | Implementação | Teste | Status |
|---|---|---|---|
| `GET /health/live` que não consulta nada externo | `Predicate = _ => false`, nenhum check executado | `HealthTests.Live_NaoConsultaNadaExterno_RespondeHealthy` | ✔ |
| `GET /health/ready` com Postgres crítico e Redis degradado (nunca reprova) | `PostgresHealthCheck` (tag `critical`), `RedisHealthCheck` (tag `degraded`, nunca `Unhealthy`) | `HealthTests.Ready_ComTudoUp_RespondeComOsDoisChecksENaoReprova`, `HealthTests.Ready_ComRedisParado_NaoReprova`; casos de falha de Postgres isolados (sem tocar o container compartilhado) em `HealthChecksTests` (unidade) | ✔ |
| Logs estruturados com `correlationId` e ids de negócio | `AddJsonConsole`, `Activity.SetTag("correlation.id", ...)` no `CorrelationIdMiddleware` | inspeção manual (sem teste automatizado dedicado) | ✔ |
| Métrica de empréstimos criados | `biblioteca.loans.created{book_id}` | `MetricsTests.CreateLoan_Sucesso_IncrementaLoansCreatedComBookId` | ✔ |
| Métrica de rejeições por indisponibilidade | `biblioteca.loans.rejected{reason}` | `MetricsTests.CreateLoan_LivroIndisponivel_IncrementaLoansRejectedComReasonUnavailable` | ✔ |
| Métrica de operações idempotentes repetidas | `biblioteca.idempotency.replayed{endpoint}` | `MetricsTests.CreateLoan_ChaveRepetida_IncrementaIdempotencyReplayed` | ✔ |
| Métrica de latência do endpoint de empréstimo | `biblioteca.loans.create.duration{outcome}` | Coberta nos três testes de `MetricsTests` acima (asserção do histograma por desfecho) | ✔ |
| Traces e métricas compatíveis com OpenTelemetry | OTLP (condicional a `OTEL_EXPORTER_OTLP_ENDPOINT`) + instrumentação de ASP.NET Core, HttpClient e Npgsql | — (sem coletor no ambiente local; verificado por inspeção de código) | ✔ |

Simplificação assumida: sem instrumentação de trace dedicada para Redis — o pacote
`OpenTelemetry.Instrumentation.StackExchangeRedis` da comunidade só existe em
pré-lançamento hoje (busca via `dotnet package search` sem correspondência estável).

## 10. Kubernetes

| Requisito | Implementação | Status |
|---|---|---|
| `Deployment` | `deploy/helm/biblioteca/templates/deployment.yaml` | ✅ |
| `Service` | `templates/service.yaml` | ✅ |
| Referências a configuração e segredos **sem valores reais** | `ConfigMap` + `secretKeyRef` para `existingSecret` | ✅ |
| Requests e limits de CPU/memória | `resources` no container | ✅ |
| Liveness e readiness probes | `/health/live`, `/health/ready` | ✅ |
| README explica corretude entre 2 e 11 réplicas | [README §8.6](../README.md#86-corretude-entre-2-e-11-réplicas) + [operations.md](operations.md#de-2-a-11-réplicas) | ✅ |

## 11. Endpoints mínimos

| Endpoint sugerido | Situação |
|---|---|
| `POST /books`, `GET /books`, `GET /books/{id}`, `PATCH /books/{id}`, `DELETE /books/{id}` | ✔ (`PATCH` exige `If-Match`; `DELETE` desativa — [desvios](api-contract.md#desvios-do-contrato-sugerido)) |
| `POST /users`, `GET /users/{id}/loans` | ✔ |
| `POST /loans` (com `Idempotency-Key`), `POST /loans/{id}/return`, `POST /loans/{id}/cancel` | ✔ |
| `GET /books/{id}/availability`, `GET /books/{id}/history` | ✔ |
| `GET /audit-events` | ✔ |
| `GET /health/live`, `GET /health/ready` | ✅ |
| **Acréscimo**: `POST /auth/token` | ✔ — necessário para o `actor` da auditoria ([security.md](security.md)), fora de Production |

## 12. Testes mínimos

| Requisito | Teste | Status |
|---|---|---|
| Unitários das regras de negócio | `Biblioteca.UnitTests` | ✔ |
| Integração com PostgreSQL real/containerizado | `Biblioteca.IntegrationTests` (Testcontainers) | ✔ |
| Concorrente para o último exemplar | `LastCopyConcurrencyTests` | ✔ |
| Idempotência | `IdempotencyTests` | ✔ |
| Preservação de histórico após devolução/cancelamento | `LoanHistoryTests` | ✔ |

## 13. README esperado

| Item | Seção |
|---|---|
| Pré-requisitos e comandos de execução | [§2](../README.md#2-pré-requisitos), [§3](../README.md#3-como-executar) |
| Como executar migrations e testes | [§4](../README.md#4-migrations), [§5](../README.md#5-testes) |
| Variáveis de ambiente, sem segredos | [§6](../README.md#6-configuração-e-variáveis-de-ambiente) |
| Decisões de concorrência, idempotência, cache e auditoria | [§8.1](../README.md#81-concorrência--o-último-exemplar) a [§8.4](../README.md#84-auditoria) |
| Limitações conhecidas e evolução para produção | [§9](../README.md#9-limitações-conhecidas), [§10](../README.md#10-evolução-para-produção) |
| Git com histórico de commits | [implementation-plan.md](implementation-plan.md) define a sequência |
