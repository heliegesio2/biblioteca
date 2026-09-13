# Biblioteca — API de Empréstimos Concorrentes e Auditáveis

API REST em **.NET 10 (LTS)** para o catálogo e os empréstimos de uma biblioteca.

O escopo é pequeno de propósito. O que este projeto exercita não é a quantidade de
endpoints, e sim as decisões de um serviço distribuído rodando em múltiplas réplicas:
**integridade dos dados, concorrência, idempotência, auditoria, cache, testes e operação**.

> Estado: **documentação de decisões concluída, implementação em andamento.**
> O plano de execução está em [docs/implementation-plan.md](docs/implementation-plan.md).

---

## 1. Stack

| Camada | Tecnologia |
|---|---|
| Runtime | .NET 10.0 (LTS) — ASP.NET Core Web API (Minimal APIs) |
| Organização | Vertical slices por feature + **CQRS** com handlers próprios (sem MediatR) |
| Banco | PostgreSQL 17 — **única fonte de verdade** |
| Acesso a dados | EF Core 10 + provider Npgsql, migrations versionadas |
| Cache | Redis 7 via `HybridCache` (L1 em memória + L2 distribuído) |
| Observabilidade | OpenTelemetry (traces + métricas), logs estruturados JSON, health checks |
| Testes | xUnit + Testcontainers (PostgreSQL e Redis reais) |
| Empacotamento | Dockerfile multi-stage, Docker Compose, Helm chart |

## 2. Pré-requisitos

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Compose v2) — obrigatório.
- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) — apenas para rodar/testar fora do container.

## 3. Como executar

```bash
# sobe api + postgres + redis; as migrations são aplicadas por um serviço dedicado
docker compose up --build
```

| Recurso | URL |
|---|---|
| API | http://localhost:8080 |
| OpenAPI (JSON) | http://localhost:8080/openapi/v1.json |
| UI de exploração | http://localhost:8080/scalar |
| Liveness | http://localhost:8080/health/live |
| Readiness | http://localhost:8080/health/ready |

Desenvolvimento local, com apenas as dependências em container:

```bash
docker compose up -d postgres redis
dotnet run --project src/Biblioteca.Api
```

## 4. Migrations

As migrations **nunca** são aplicadas automaticamente no start da API: com 2 a 11
réplicas subindo em paralelo, isso é uma corrida entre pods. Elas rodam em um passo
separado — serviço `migrator` no Compose, `Job` com hook `pre-install/pre-upgrade`
no Helm chart. Ver [docs/operations.md](docs/operations.md).

```bash
# aplicar manualmente
dotnet ef database update --project src/Biblioteca.Api

# criar uma nova migration
dotnet ef migrations add <Nome> --project src/Biblioteca.Api \
    --output-dir Infrastructure/Persistence/Migrations
```

## 5. Testes

```bash
dotnet test                                   # tudo (integração exige Docker)
dotnet test tests/Biblioteca.UnitTests        # regras de negócio, sem I/O, rápido
dotnet test tests/Biblioteca.IntegrationTests # Postgres + Redis reais (Testcontainers)
```

Os testes de integração sobem os containers sozinhos; não é preciso `docker compose up`
antes, nem existe banco de teste compartilhado. Cobertura exigida pelo desafio e onde
ela está: [docs/testing.md](docs/testing.md).

## 6. Configuração e variáveis de ambiente

Toda configuração vem de `appsettings.json` → `appsettings.{Environment}.json` →
variáveis de ambiente (a última vence). **Nenhum segredo real é versionado**: o
repositório contém apenas credenciais locais e descartáveis do Compose.

| Variável | Descrição | Default local |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production` | `Development` |
| `ConnectionStrings__Postgres` | Conexão do PostgreSQL | host `postgres`, db `biblioteca` |
| `ConnectionStrings__Redis` | Conexão do Redis | `redis:6379` |
| `Auth__Issuer` / `Auth__Audience` | Emissor/audiência do JWT | `biblioteca` |
| `Auth__SigningKey` | Chave de assinatura do JWT — **injetada por Secret** | somente em `appsettings.Development.json` |
| `Loans__LoanPeriodDays` | Prazo de devolução | `14` |
| `Loans__MaxActiveLoansPerUser` | Limite de empréstimos ativos por usuário | `5` |
| `Cache__BookTtlSeconds` / `Cache__AvailabilityTtlSeconds` | TTLs do cache de leitura | `300` / `30` |
| `Idempotency__RetentionHours` | Retenção das chaves de idempotência | `24` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Coletor OpenTelemetry (opcional) | vazio (exportação desligada) |

No Kubernetes, `ConnectionStrings__*` e `Auth__SigningKey` vêm de um `Secret`
referenciado por nome, sem valores no chart. Ver [docs/operations.md](docs/operations.md).

## 7. Endpoints

Contrato conforme sugerido no enunciado, com dois ajustes documentados em
[docs/api-contract.md](docs/api-contract.md): `DELETE /books/{id}` **desativa** o livro
(nunca apaga) e `PATCH /books/{id}` exige `If-Match`.

```text
POST   /books                  cria livro (ISBN único)
GET    /books                  lista/busca paginada
GET    /books/{id}             detalhe do livro                    [cache + ETag]
PATCH  /books/{id}             atualização parcial                 [exige If-Match]
DELETE /books/{id}             desativa (soft delete), preserva histórico

POST   /users                  cadastra leitor
GET    /users/{id}/loans       histórico de empréstimos do usuário

POST   /loans                  cria empréstimo                     [exige Idempotency-Key]
POST   /loans/{id}/return      devolve
POST   /loans/{id}/cancel      cancela, preservando o registro

GET    /books/{id}/availability   disponibilidade atual            [cache]
GET    /books/{id}/history        histórico de empréstimos do livro
GET    /audit-events              trilha de auditoria de negócio

GET    /health/live            processo vivo
GET    /health/ready           pronto para receber tráfego
POST   /auth/token             emite JWT de desenvolvimento
```

Erros seguem **Problem Details (RFC 9457)**, com `traceId` e `correlationId` em todas
as respostas de erro.

## 8. Decisões de engenharia

Resumo; o detalhe e os trade-offs descartados estão nos documentos linkados e nas
[ADRs](docs/adr/).

### 8.1 Concorrência — o último exemplar

**Estratégia: atualização condicional atômica**, dentro de uma transação
`READ COMMITTED`, em uma única instrução:

```sql
UPDATE books
   SET available_copies = available_copies - 1
 WHERE id = @id AND is_active AND available_copies > 0;
```

Se o comando afetar **0 linhas**, não havia exemplar: a requisição recebe `409 Conflict`
com um erro de negócio explícito (`book-unavailable`) — não um erro genérico. Se afetar
1 linha, o `INSERT` do empréstimo, o evento de auditoria e a finalização da chave de
idempotência acontecem na **mesma transação**. É tudo ou nada.

Por que isso é correto sob concorrência: no PostgreSQL, em `READ COMMITTED`, quando
duas transações tentam atualizar a mesma linha, a segunda bloqueia até o commit da
primeira e então **reavalia a cláusula `WHERE` sobre a versão nova** da linha. Com
`available_copies = 0`, a condição deixa de valer e o comando afeta 0 linhas. Não existe
janela entre "ler" e "decidir": a leitura, a decisão e a escrita são a mesma instrução.

Rede de segurança no banco, para o caso de qualquer caminho futuro errar a conta:
`CHECK (available_copies >= 0 AND available_copies <= total_copies)`.

**Alternativas consideradas e por que não:**

| Alternativa | Por que não foi escolhida |
|---|---|
| `SELECT ... FOR UPDATE` + update | Correto, mas serializa a linha por mais tempo (dura o round-trip inteiro da aplicação, não uma instrução). Fica como plano B se o fluxo passar a exigir várias leituras consistentes antes de decidir. |
| Token de concorrência `xmin` no livro | Gera **falso conflito**: duas retiradas simultâneas com 50 exemplares disponíveis falhariam, embora ambas devessem ter sucesso. Em linha quente, vira retry em cascata. É a ferramenta certa para *edição* do catálogo — e é lá que a usamos. |
| `SERIALIZABLE` | Resolve, mas paga aborts (`40001`) e obriga retry em toda a aplicação por um problema que uma instrução resolve. |
| Lock distribuído no Redis | Introduz dependência de corretude num componente que queremos manter descartável, com todos os problemas de expiração de lock e fencing. Redis aqui só acelera leitura. |
| `rowversion` | Recurso de SQL Server; não existe no PostgreSQL. Explicitamente descartado pelo enunciado. |

Onde **usamos** concorrência otimista: `PATCH /books/{id}`. O `xmin` da linha é exposto
como `ETag`, e o `PATCH` exige `If-Match`. Se o livro mudou desde a leitura, a resposta é
`412 Precondition Failed`. Aqui o conflito é real (dois editores sobrescrevendo o mesmo
campo), e falhar é a resposta certa. Detalhes: [docs/concurrency.md](docs/concurrency.md).

### 8.2 Idempotência

`POST /loans` exige o header `Idempotency-Key` (ausência → `400`). A chave é persistida
no PostgreSQL, **não** no Redis nem em memória: com 2 a 11 réplicas, a garantia precisa
morar no mesmo lugar transacional que o efeito que ela protege.

O registro da chave é inserido **na mesma transação** que cria o empréstimo e é
finalizado com o snapshot da resposta antes do commit. Consequências:

- **Repetição sequencial** → devolve a resposta original armazenada (mesmo status, mesmo
  corpo, mesmo `loanId`), com `Idempotency-Replayed: true`. A disponibilidade não é
  reduzida duas vezes.
- **Repetição concorrente** → a segunda transação bloqueia no índice único da chave até o
  commit da primeira, e então lê a resposta pronta e a devolve. Sem efeito duplicado.
- **Mesma chave com payload diferente** → `409 Conflict` (`idempotency-key-reuse`). A
  requisição é comparada por hash do corpo canonicalizado.
- **Falha no meio** → o rollback remove a chave junto com o empréstimo; o cliente pode
  repetir com a mesma chave e ter sucesso.

Detalhes e diagrama de estados: [docs/idempotency.md](docs/idempotency.md).

### 8.3 Cache

Redis via `HybridCache` em duas leituras: `GET /books/{id}` e `GET /books/{id}/availability`
(esta é a consulta mais chamada do sistema e a mais cara de manter fresca).

- **O cache nunca decide nada.** Disponibilidade exibida vem do cache; disponibilidade
  *decidida* vem do `UPDATE` condicional no PostgreSQL. Um cache velho pode fazer um
  cliente tentar um empréstimo que será rejeitado — nunca pode criar um empréstimo inválido.
- **Invalidação explícita depois do commit**: empréstimo, devolução, cancelamento,
  alteração de quantidade e desativação removem as chaves do livro afetado. Invalidar
  antes do commit deixaria o cache repopular com o valor antigo.
- **TTL curto como rede de segurança** (30s para disponibilidade, 5min para o livro): se a
  invalidação falhar — Redis fora do ar, por exemplo — a janela de inconsistência é
  limitada e observável por métrica.
- **A listagem `GET /books` não é cacheada**: o espaço de chaves é ilimitado (filtro ×
  página × ordenação) e a invalidação seria imprecisa. Cachear ali traria o pior dos dois
  mundos.
- **Redis indisponível degrada, não derruba**: as leituras caem para o banco.

Detalhes: [docs/caching.md](docs/caching.md).

### 8.4 Auditoria

Tabela `audit_events`, escrita **na mesma transação** da mudança que descreve — se o
empréstimo existe, o evento existe; não há trilha perdida por falha de processo.

Cada evento registra `entity_type`, `entity_id`, `action`, `actor`, `occurred_at` (UTC),
`correlation_id` e um `payload` JSONB com o que mudou (antes/depois em alterações de
livro; dados do empréstimo nas transições). Consultável por `GET /audit-events`.

Eventos cobertos: `BookCreated`, `BookUpdated`, `BookDeactivated`, `LoanCreated`,
`LoanReturned`, `LoanCancelled`.

Nada é apagado: `DELETE /books/{id}` desativa o livro, e devolução/cancelamento mudam o
**status** do empréstimo, preservando o registro e as datas. Detalhes:
[docs/auditing.md](docs/auditing.md).

### 8.5 Observabilidade

- `correlationId` aceito via header (`X-Correlation-Id`), gerado quando ausente,
  propagado para logs, auditoria e resposta.
- Logs estruturados em JSON com `correlationId`, `bookId`, `userId`, `loanId`.
- Traces e métricas via OpenTelemetry (OTLP), incluindo instrumentação de ASP.NET Core,
  Npgsql e Redis.
- Métricas de negócio: `biblioteca.loans.created`, `biblioteca.loans.rejected`
  (com `reason=unavailable|limit_exceeded|...`), `biblioteca.idempotency.replayed`,
  `biblioteca.loans.create.duration`, `biblioteca.cache.invalidation.failed`.

Detalhes: [docs/observability.md](docs/observability.md).

### 8.6 Corretude entre 2 e 11 réplicas

| Risco com N réplicas | Como é resolvido |
|---|---|
| Duas réplicas emprestam o último exemplar | A decisão é uma única instrução atômica no PostgreSQL, não um `if` no processo ([8.1](#81-concorrência--o-último-exemplar)) |
| Retry do cliente atinge outra réplica e duplica o empréstimo | Chave de idempotência persistida no banco, com índice único ([8.2](#82-idempotência)) |
| Réplicas com caches divergentes | Cache compartilhado no Redis (L2); o L1 em memória tem TTL curto e nunca é consultado para decidir |
| Migrations concorrentes na subida | Migrations só rodam em `Job`/serviço dedicado, nunca no start da API |
| Estado preso a um pod | API stateless: sem sessão, sem lock em memória, sem agendamento local — qualquer pod atende qualquer request |
| Pod removido no meio de um request | `terminationGracePeriodSeconds` + shutdown gracioso do ASP.NET Core; transações incompletas sofrem rollback e a chave de idempotência permite repetir |

## 9. Limitações conhecidas

- **Sem identity provider real.** `POST /auth/token` emite JWT assinado com chave
  simétrica local para exercitar autoria (`actor`) na auditoria e autorização por papel.
  Em produção, seria um IdP externo com chaves assimétricas e rotação.
- **Exemplares são uma contagem, não entidades.** `total_copies`/`available_copies` bastam
  para o domínio pedido. Rastrear exemplar físico individual (código de tombo, estado de
  conservação) exigiria uma tabela `copies` e mudaria o modelo de concorrência para lock
  de exemplar específico.
- **Sem reservas, multas ou renovação.** Fora do escopo do enunciado.
- **Limpeza de chaves de idempotência** roda como `BackgroundService` simples; em
  produção, seria um `CronJob` fora do processo da API.
- **Busca textual é `ILIKE` com índice trigram**, suficiente para o volume do desafio.
  Acima disso, `tsvector`/busca dedicada.
- **Auditoria não é imutável por construção**: a aplicação só insere, mas um DBA com
  acesso direto pode alterar a tabela. Produção pediria permissões restritas
  (`INSERT`-only para a role da aplicação) ou encadeamento por hash.

## 10. Evolução para produção

1. **Outbox + eventos**: publicar `LoanCreated`/`LoanReturned` transacionalmente para
   integrações (notificações, BI), sem acoplar a API a um broker.
2. **Rate limiting e quotas** por cliente, com o middleware nativo do ASP.NET Core.
3. **Particionamento de `audit_events`** por mês, com política de retenção e arquivamento.
4. **IdP externo** (OIDC) com validação por JWKS.
5. **Réplica de leitura** para consultas de histórico e auditoria.
6. **Testes de carga** no endpoint de empréstimo para calibrar pool de conexões e HPA.
7. **SLOs e alertas** sobre as métricas de negócio (taxa de rejeição, latência p99).

## 11. Documentação

| Documento | Conteúdo |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Organização do código, fluxo de request, limites do sistema |
| [docs/domain-model.md](docs/domain-model.md) | Entidades, invariantes, máquina de estados do empréstimo |
| [docs/api-contract.md](docs/api-contract.md) | Endpoints, payloads, status, erros |
| [docs/data-integrity.md](docs/data-integrity.md) | Schema, constraints, índices, transações |
| [docs/concurrency.md](docs/concurrency.md) | Estratégia de concorrência e trade-offs |
| [docs/idempotency.md](docs/idempotency.md) | Protocolo da `Idempotency-Key` |
| [docs/auditing.md](docs/auditing.md) | Trilha de auditoria de negócio |
| [docs/caching.md](docs/caching.md) | Chaves, TTL, invalidação, falha do Redis |
| [docs/observability.md](docs/observability.md) | Correlação, logs, métricas, traces, health |
| [docs/security.md](docs/security.md) | Autenticação, autorização, segredos |
| [docs/testing.md](docs/testing.md) | Estratégia de testes e rastreabilidade |
| [docs/operations.md](docs/operations.md) | Docker, Helm, migrations, runbook |
| [docs/requirements-traceability.md](docs/requirements-traceability.md) | Cada requisito do enunciado → onde é atendido e testado |
| [docs/implementation-plan.md](docs/implementation-plan.md) | Fases, ordem dos commits, critérios de pronto |
| [docs/adr/](docs/adr/) | Decisões arquiteturais registradas |

## 12. Estrutura do repositório

```
.
├── docs/                            # decisões de engenharia (parte da entrega)
├── src/Biblioteca.Api/              # projeto único, organizado por feature
│   ├── Features/                    # Catalog, Users, Loans, Audit, Auth
│   │   └── <feature>/               # Domain/ + Commands/ + Queries/ + Endpoints
│   └── Infrastructure/              # Cqrs, Persistence, Idempotency, Caching,
│                                    # Observability, Http
├── tests/
│   ├── Biblioteca.UnitTests/        # regras de negócio, sem I/O
│   └── Biblioteca.IntegrationTests/ # Postgres + Redis reais via Testcontainers
├── deploy/helm/biblioteca/          # Deployment, Service, HPA, Job de migration
├── docker-compose.yml
└── Dockerfile
```

O pipeline de comandos (Metrics → Validation → CacheInvalidation → Transaction →
Idempotency → Handler) e o motivo de cada posição estão em
[docs/architecture.md](docs/architecture.md#o-pipeline-cqrs). A ordem não é estética:
idempotência precisa rodar **dentro** da transação e a invalidação de cache **fora** dela.
