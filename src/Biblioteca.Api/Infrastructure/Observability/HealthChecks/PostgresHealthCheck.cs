using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Biblioteca.Api.Infrastructure.Observability.HealthChecks;

/// <summary>
/// Tag "critical" (docs/observability.md#health-checks): PostgreSQL fora do ar reprova
/// `/health/ready` — a API não serve nada sem o banco.
/// </summary>
internal sealed class PostgresHealthCheck(BibliotecaDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Não foi possível conectar ao PostgreSQL.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Não foi possível conectar ao PostgreSQL.", ex);
        }
    }
}
