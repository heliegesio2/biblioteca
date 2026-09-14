using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Biblioteca.Api.Infrastructure.Observability.HealthChecks;

/// <summary>
/// Tag "degraded" (docs/observability.md#health-checks): Redis fora do ar nunca reprova
/// `/health/ready` — devolve <see cref="HealthStatus.Degraded"/>, não Unhealthy. Leituras
/// caem para o banco e escritas nem tocam no Redis (docs/caching.md), então perder o
/// Redis não tira a réplica do balanceador.
/// </summary>
internal sealed class RedisHealthCheck(IDistributedCache cache) : IHealthCheck
{
    private const string PingKey = "health:ping";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.GetAsync(PingKey, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Timeout ao conectar; leituras servidas pelo banco", ex);
        }
    }
}
