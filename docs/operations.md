# Operação

## Imagem

`Dockerfile` multi-stage, construído a partir da raiz do repositório:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# restore separado do build: a camada de pacotes só invalida quando os .csproj mudam
COPY *.slnx Directory.*.props ./
COPY src/Biblioteca.Api/*.csproj src/Biblioteca.Api/
RUN dotnet restore src/Biblioteca.Api/Biblioteca.Api.csproj
COPY . .
RUN dotnet publish src/Biblioteca.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID                       # não-root, já definido na imagem base
EXPOSE 8080
ENTRYPOINT ["dotnet", "Biblioteca.Api.dll"]
```

Decisões: runtime `aspnet` (não `sdk`) na imagem final, usuário não-root, porta 8080 (sem
capability privilegiada), `.dockerignore` excluindo `bin/`, `obj/`, `.git/` e `tests/`.

Sem AOT nem `PublishTrimmed`: o EF Core com Npgsql ainda exige cuidado com trimming, e o
ganho de alguns megabytes não compensa o risco de falha em runtime por reflexão removida.

## Docker Compose

```
services:
  postgres   postgres:17-alpine   healthcheck: pg_isready
  redis      redis:7-alpine       healthcheck: redis-cli ping
  migrator   imagem da API        entrypoint: dotnet ef database update
                                  depends_on: postgres (service_healthy)
                                  restart: "no"
  api        imagem da API        depends_on: migrator (service_completed_successfully)
                                             postgres, redis (service_healthy)
                                  ports: 8080:8080
```

O serviço `migrator` existe para que o Compose reproduza o **mesmo** modelo de produção:
migration é um passo separado, que termina, antes de a API subir. Se ele falhar, a API
não sobe — falha visível é melhor do que API rodando contra schema errado.

Credenciais do Compose são locais e descartáveis (`postgres`/`postgres`), o que é
explícito no [security.md](security.md#segredos). Nenhum segredo real é versionado.

```bash
docker compose up --build      # sobe tudo
docker compose logs -f api     # logs estruturados em JSON
docker compose down -v         # derruba e apaga os volumes
```

## Migrations

**Nunca** no `Program.cs`. Com 2 a 11 réplicas subindo em paralelo, `Database.Migrate()`
no start é uma corrida entre pods: várias tentam aplicar a mesma migration ao mesmo
tempo, e o resultado vai de erro de lock a schema parcialmente aplicado.

| Ambiente | Como roda |
|---|---|
| Local | `dotnet ef database update` ou o serviço `migrator` do Compose |
| Kubernetes | `Job` com hooks `pre-install` e `pre-upgrade` do Helm, `backoffLimit: 2` |

O `Job` usa a **mesma imagem** da API com outro entrypoint (`dotnet ef ...` via bundle de
migrations publicado junto). Como é hook `pre-upgrade`, o Helm espera o `Job` terminar
com sucesso antes de atualizar o `Deployment` — se a migration falhar, os pods novos não
sobem e a versão anterior continua servindo.

Regra de compatibilidade, para que isso funcione com *rolling update* (pods antigos e
novos convivendo por alguns segundos): **toda migration precisa ser compatível com a
versão anterior da aplicação**. Adicionar coluna `NOT NULL` sem default, renomear ou
remover coluna em uso quebra os pods antigos durante a janela. O padrão é expandir →
migrar dados → contrair em *releases* separados.

## Kubernetes / Helm

```
deploy/helm/biblioteca/
├── Chart.yaml
├── values.yaml
└── templates/
    ├── deployment.yaml
    ├── service.yaml
    ├── configmap.yaml          # configuração não-sensível
    ├── secret-ref.yaml         # REFERÊNCIA a um Secret existente, sem valores
    ├── migration-job.yaml      # hook pre-install / pre-upgrade
    ├── hpa.yaml
    ├── pdb.yaml
    └── serviceaccount.yaml
```

Conteúdo mínimo exigido pelo enunciado e como está atendido:

| Exigência | Onde |
|---|---|
| `Deployment` | `templates/deployment.yaml`, `replicas` de `values.yaml` (default 2) |
| `Service` | `templates/service.yaml`, `ClusterIP` na 8080 |
| Referências a configuração e segredos **sem valores reais** | `configMapRef` + `secretKeyRef` apontando para `existingSecret`; o chart não carrega nenhuma credencial |
| Requests e limits de CPU/memória | `resources` no container |
| Liveness e readiness probes | `/health/live` e `/health/ready` |

```yaml
resources:
  requests: { cpu: 100m, memory: 128Mi }
  limits:   { cpu: 500m, memory: 512Mi }

livenessProbe:
  httpGet:  { path: /health/live,  port: 8080 }
  initialDelaySeconds: 10
  periodSeconds: 10
  failureThreshold: 3

readinessProbe:
  httpGet:  { path: /health/ready, port: 8080 }
  initialDelaySeconds: 5
  periodSeconds: 5
  failureThreshold: 2

securityContext:
  runAsNonRoot: true
  allowPrivilegeEscalation: false
  readOnlyRootFilesystem: true
  capabilities: { drop: ["ALL"] }
```

Sem limite de CPU agressivo demais: *throttling* em container .NET aumenta latência de
cauda de forma difícil de diagnosticar. O limite existe para conter vazamento, não para
dimensionar.

```bash
helm lint deploy/helm/biblioteca
helm template biblioteca deploy/helm/biblioteca          # renderiza sem cluster
helm upgrade --install biblioteca deploy/helm/biblioteca \
     --set image.tag=1.4.0 --set existingSecret=biblioteca-secrets
```

Não é preciso cluster para avaliar: `helm template` renderiza os manifests localmente.

## De 2 a 11 réplicas

Por que a aplicação continua correta ao escalar — o requisito explícito do enunciado:

| Risco | Mitigação |
|---|---|
| Duas réplicas emprestam o último exemplar | A decisão é uma instrução atômica no PostgreSQL, não um `if` no processo ([concurrency.md](concurrency.md)) |
| Retry cai em outra réplica e duplica o empréstimo | Chave de idempotência no PostgreSQL, com índice único ([idempotency.md](idempotency.md)) |
| Caches divergentes entre réplicas | L2 compartilhado no Redis; L1 local com TTL curto e explícito, nunca usado para decidir ([caching.md](caching.md)) |
| Migrations concorrentes na subida | `Job` `pre-upgrade`, nunca no start da API |
| Estado preso a um pod | API stateless: sem sessão, sem lock em memória, sem agendamento local |
| Rotina de limpeza duplicada | O `DELETE` de chaves expiradas é idempotente; rodar em N réplicas tem o mesmo efeito de rodar em uma |
| Pod removido no meio de um request | `terminationGracePeriodSeconds: 30` + shutdown gracioso; transações incompletas sofrem rollback e o cliente repete com a mesma `Idempotency-Key` |
| Exaustão de conexões do PostgreSQL | `Maximum Pool Size` por réplica × réplicas ≤ `max_connections`; com 11 réplicas e pool 20, são 220 conexões — acima do default 100, então o pool é dimensionado por `values.yaml` (ou entra PgBouncer) |

A última linha é o limite real de escala aqui, e é por isso que está explícita: o
gargalo de 11 réplicas não é CPU, é conexão de banco.

```yaml
autoscaling:                    # hpa.yaml
  enabled: true
  minReplicas: 2
  maxReplicas: 11
  targetCPUUtilizationPercentage: 70
podDisruptionBudget:
  minAvailable: 1               # deploy/drain nunca derruba a última réplica
```

## Configuração

Precedência: `appsettings.json` → `appsettings.{Environment}.json` → variáveis de
ambiente. Em Kubernetes, o `ConfigMap` cobre o não-sensível e o `Secret` cobre
`ConnectionStrings__*` e `Auth__SigningKey`. Tabela completa de variáveis no
[README](../README.md#6-configuração-e-variáveis-de-ambiente).

Validação na subida (`ValidateOnStart`): connection strings presentes, `Auth:SigningKey`
com pelo menos 32 bytes e diferente da chave de desenvolvimento quando
`ASPNETCORE_ENVIRONMENT=Production`. A aplicação **recusa subir** com configuração
inválida, em vez de descobrir o problema no primeiro request.

## Shutdown gracioso

`terminationGracePeriodSeconds: 30` no pod e `ShutdownTimeout` de 25s no host. Na ordem:
o pod sai dos endpoints do `Service`, o Kestrel para de aceitar conexões novas, as
requisições em andamento terminam, as transações abertas commitam ou revertem. O que não
terminar é revertido pelo banco — e, no caso do empréstimo, o cliente repete com a mesma
`Idempotency-Key` sem risco de duplicar.

## Runbook

| Sintoma | Diagnóstico | Ação |
|---|---|---|
| `/health/ready` reprovando | Corpo da resposta indica qual check falhou | Se for `postgres`, o problema é o banco (conexões, disco, rede) — a API está correta ao recusar tráfego |
| Redis marcado `Degraded` | `biblioteca.cache.invalidation.failed` subindo | A API funciona degradada; restaurar o Redis. Nenhuma ação de dado é necessária: o cache é descartável |
| Latência alta em `POST /loans` | `biblioteca.loans.create.duration{outcome=created}` p99; spans `loan.decrement_availability` | Contenção na linha de um título muito disputado ou pool de conexões saturado |
| Muitas rejeições | `biblioteca.loans.rejected` por `reason` | `unavailable` é comportamento correto; `limit_exceeded` em massa sugere revisar `Loans:MaxActiveLoansPerUser` |
| `idempotency.replayed` disparando | Clientes repetindo | Procurar timeout na malha — costuma indicar latência antes da API, não nela |
| Reclamação sobre um empréstimo específico | `correlationId` do cliente | Correlacionar log + trace + `GET /audit-events?entityId={loanId}` |
| Pods reiniciando em loop | `kubectl describe pod` | Se a liveness estiver falhando, **não** é por dependência: `/health/live` não consulta Postgres nem Redis ([observability.md](observability.md#health-checks)) |

## Backup e recuperação

Fora do escopo do desafio (não há banco gerenciado provisionado), mas a decisão de
desenho que importa está tomada: **todo o estado durável está no PostgreSQL**. Backup do
PostgreSQL é backup do sistema inteiro. O Redis pode ser recriado vazio a qualquer
momento, sem perda de dado nem passo de reconstrução.
