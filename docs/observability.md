# Observabilidade

Requisitos do enunciado: `GET /health/live` e `GET /health/ready` com semânticas
distintas, logs estruturados com `correlationId` e identificadores de negócio, métricas
de empréstimos criados / rejeições por indisponibilidade / repetições idempotentes /
latência do endpoint de empréstimo, e instrumentação compatível com OpenTelemetry.

## Correlação

`CorrelationIdMiddleware`, o primeiro do pipeline:

1. Lê `X-Correlation-Id` do request; se ausente ou malformado, gera um `Guid`.
2. Publica o valor em um `ICorrelationContext` *scoped*.
3. Abre um escopo de log (`BeginScope`) — todo log daquela requisição carrega o campo.
4. Anexa `correlation.id` ao `Activity` corrente (trace).
5. **Sempre** devolve `X-Correlation-Id` na resposta, inclusive em erro.

O mesmo valor é gravado em `audit_events.correlation_id`
([auditing.md](auditing.md#relação-com-correlationid)) e aparece no corpo de todo
Problem Details. De um `409` reclamado pelo cliente se chega ao log, ao trace e à linha
da trilha de auditoria — sem adivinhação.

`traceId` (W3C, propagado por `traceparent`) coexiste com `correlationId`: o primeiro
identifica a operação distribuída, o segundo é o identificador que o cliente enxerga e
consegue repetir ao abrir um chamado.

## Logs

`Microsoft.Extensions.Logging` com formatter JSON nativo (`AddJsonConsole`), stdout —
sem arquivo, sem rotação, sem agente dentro do container: em Kubernetes, o coletor do
cluster lê stdout.

```json
{ "Timestamp":"2026-09-12T14:03:11.482Z", "Level":"Information",
  "Message":"Loan created",
  "correlationId":"0f9b2c1e-...", "traceId":"4bf92f3577b34da6a3ce929d0e0e4736",
  "loanId":"01923c...", "bookId":"01923f...", "userId":"01923a...",
  "availableCopiesAfter":0, "actor":"ana@example.com" }
```

Regras aplicadas:

- **Sempre com template e parâmetros nomeados** (`"Loan created for {BookId}"`), nunca
  interpolação — é o que produz campos consultáveis em vez de texto.
- **Níveis**: `Information` para transições de negócio (empréstimo criado, devolvido,
  cancelado, livro desativado); `Warning` para rejeições de negócio; `Error` apenas para
  falha inesperada. Rejeição de regra **não é erro** — poluir o nível `Error` com
  `book-unavailable` faz o alerta de erro perder o significado.
- **Nada de dado sensível**: sem token, sem senha, sem corpo bruto de requisição.
  E-mail aparece apenas como `actor`, por ser exigência do requisito de auditoria.
- **`EventId` estável** por tipo de evento, para permitir alerta por identificador em
  vez de por substring da mensagem.

## Métricas

`Meter` chamado `Biblioteca.Api`, exportado por OpenTelemetry. As quatro primeiras são
exatamente as exigidas pelo enunciado:

| Instrumento | Tipo | Tags | Pergunta que responde |
|---|---|---|---|
| `biblioteca.loans.created` | Counter | `book_id` | Quantos empréstimos foram criados |
| `biblioteca.loans.rejected` | Counter | `reason` (`unavailable`, `limit_exceeded`, `book_inactive`, `user_inactive`, `duplicate_active_loan`) | Quantas rejeições, e por quê |
| `biblioteca.idempotency.replayed` | Counter | `endpoint` | Quantas repetições idempotentes — subida repentina indica timeouts na malha |
| `biblioteca.loans.create.duration` | Histogram (ms) | `outcome` (`created`, `rejected`, `replayed`) | Latência do endpoint de empréstimo, separada por desfecho |
| `biblioteca.cache.invalidation.failed` | Counter | `key_prefix` | Invalidação que falhou → janela de dado velho |
| `biblioteca.loans.returned` / `.cancelled` | Counter | — | Volume das transições finais |

`outcome` no histograma existe porque misturar os desfechos esconde o que importa: um
`replayed` responde em milissegundos e puxaria o p99 para baixo, mascarando lentidão nos
`created` reais.

Além dessas, vêm de graça pela instrumentação: `http.server.request.duration`
(ASP.NET Core), métricas do pool de conexões do Npgsql, do runtime (GC, thread pool) e
do cliente Redis.

## Traces

OpenTelemetry com instrumentação automática de ASP.NET Core, `HttpClient`, **Npgsql** e
StackExchange.Redis. Exportação OTLP, configurada por `OTEL_EXPORTER_OTLP_ENDPOINT`;
**vazio significa desligado**, que é o default local — não é preciso ter coletor,
Jaeger ou conta de vendor para rodar o projeto.

Spans próprios são criados onde a leitura do trace precisa de mais do que o SQL mostra:

```
POST /loans                                          ← span do ASP.NET Core
├── CreateLoanCommand                                ← span do dispatcher (decorator)
│   ├── idempotency.reserve                          ← INSERT da chave
│   ├── loan.decrement_availability                  ← o UPDATE condicional
│   │     atributos: book.id, rows_affected          ← 0 ou 1 conta a história toda
│   ├── loan.insert
│   └── audit.write
└── cache.invalidate                                 ← pós-commit, fora da transação
```

`rows_affected` no span do `UPDATE` é o atributo mais útil do sistema: distingue, em um
trace, "o livro não estava disponível" de "algo falhou", sem precisar de log.

Atributos padronizados em todos os spans relevantes: `correlation.id`, `book.id`,
`user.id`, `loan.id`, `actor`.

## Health checks

| Endpoint | O que verifica | Quem usa |
|---|---|---|
| `GET /health/live` | **Nada externo.** Responde `200` se o processo está vivo e o pipeline responde | `livenessProbe` |
| `GET /health/ready` | PostgreSQL (`SELECT 1`, tag `critical`). Redis é verificado e **reportado**, com tag `degraded` | `readinessProbe` |

Duas decisões importantes, ambas exigidas ou sugeridas pelo enunciado:

**`live` não consulta dependências.** Se consultasse, uma indisponibilidade do
PostgreSQL faria o kubelet **reiniciar todos os pods** — em loop, sem resolver nada,
enquanto o banco volta. Liveness responde "este processo precisa ser morto?", não "o
sistema está saudável?".

**`ready` não reprova por causa do Redis.** A API funciona sem cache: leituras caem
para o banco e escritas nem tocam no Redis ([caching.md](caching.md)). Reprovar a
readiness tiraria **todas** as réplicas do balanceador e transformaria "está mais
lento" em "está fora do ar". O estado do Redis aparece no corpo da resposta, para quem
estiver diagnosticando:

```json
{ "status": "Healthy",
  "checks": [
    { "name": "postgres", "status": "Healthy", "duration": 3.1 },
    { "name": "redis",    "status": "Degraded", "duration": 2001.4,
      "description": "Timeout ao conectar; leituras servidas pelo banco" } ] }
```

Ambos os endpoints são anônimos (um probe não carrega JWT), respondem rápido e não
fazem trabalho pesado. `ready` tem timeout curto: um health check que demora mais que o
`periodSeconds` do probe é pior do que não ter health check.

## Como isso se traduz em operação

| Sintoma | Onde olhar |
|---|---|
| Clientes reclamando de empréstimos negados | `biblioteca.loans.rejected` por `reason` — separa "acabou o estoque" de "bug de limite" |
| Endpoint lento | `biblioteca.loans.create.duration{outcome=created}` p99 + spans `loan.decrement_availability` (contenção na linha) |
| Suspeita de empréstimo duplicado | `biblioteca.idempotency.replayed` e `GET /audit-events?entityType=Loan&entityId=…` |
| Disponibilidade exibida errada | `biblioteca.cache.invalidation.failed` e o estado do Redis em `/health/ready` |
| Erro relatado por um cliente | `correlationId` → logs, trace e `audit_events` |

Runbook completo em [operations.md](operations.md).

## O que ficou de fora

- **Sem agente ou SDK de vendor** (DataDog, Application Insights). OTLP é o padrão
  aberto e o enunciado dispensa conta em qualquer serviço. Apontar
  `OTEL_EXPORTER_OTLP_ENDPOINT` para um coletor é tudo o que um ambiente real exigiria.
- **Logs não são exportados por OTLP**, vão para stdout. É o que Kubernetes espera, e
  evita perder log quando o coletor está fora.
- **Sem SLO nem alerta definidos** — as métricas existem, os limiares dependem de dados
  de produção que este projeto não tem. Listado na evolução no
  [README](../README.md#10-evolução-para-produção).
