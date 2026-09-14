using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Infrastructure.Idempotency;

/// <summary>
/// Remove chaves expiradas periodicamente. É seguro rodar em todas as réplicas: o
/// DELETE condicional (<c>WHERE expires_at &lt; now()</c>) é idempotente — rodar N vezes
/// tem o mesmo efeito de rodar uma (docs/idempotency.md#retenção-e-limpeza). Em produção
/// isso seria um CronJob fora do processo da API — limitação conhecida (README §9).
/// </summary>
public sealed class IdempotencyCleanupService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<IdempotencyCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<BibliotecaDbContext>();
                var now = timeProvider.GetUtcNow();

                var removed = await dbContext.Set<IdempotencyKeyEntry>()
                    .Where(e => e.ExpiresAt < now)
                    .ExecuteDeleteAsync(stoppingToken);

                if (removed > 0)
                {
                    logger.LogInformation("Removidas {Count} chaves de idempotência expiradas", removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Falha ao limpar chaves de idempotência expiradas; tentando de novo no próximo ciclo");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
