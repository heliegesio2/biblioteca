using Biblioteca.Api.Infrastructure.Observability.HealthChecks;
using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Biblioteca.UnitTests.Infrastructure.Observability;

/// <summary>
/// Cenários de falha de docs/observability.md#health-checks isolados do container Postgres
/// compartilhado pela suíte de integração — parar aquele container se mostrou destrutivo
/// para o resto dos testes (efeito colateral em toda a coleção). Aqui, um endpoint
/// inalcançável simula a mesma falha sem tocar em infraestrutura compartilhada.
/// </summary>
public sealed class HealthChecksTests
{
    [Fact]
    public async Task PostgresHealthCheck_ComBancoInalcancavel_RetornaUnhealthy()
    {
        var options = new DbContextOptionsBuilder<BibliotecaDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Timeout=1;Pooling=false")
            .Options;
        using var dbContext = new BibliotecaDbContext(options);
        var check = new PostgresHealthCheck(dbContext);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task RedisHealthCheck_ComRedisInalcancavel_RetornaDegraded()
    {
        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(options =>
        {
            options.ConfigurationOptions = new StackExchange.Redis.ConfigurationOptions
            {
                EndPoints = { "127.0.0.1:1" },
                ConnectTimeout = 300,
                SyncTimeout = 300,
                AsyncTimeout = 300,
                ConnectRetry = 1,
                AbortOnConnectFail = false,
            };
        });
        using var provider = services.BuildServiceProvider();
        var check = new RedisHealthCheck(provider.GetRequiredService<IDistributedCache>());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        // Nunca Unhealthy: Redis fora do ar não pode reprovar /health/ready
        // (docs/observability.md#health-checks).
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task Live_ComPredicadoFalso_NaoExecutaNenhumCheckMesmoComUmCriticoFalhando()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks()
            .AddCheck("postgres", () => HealthCheckResult.Unhealthy(), tags: ["critical"]);
        using var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetRequiredService<HealthCheckService>();

        // A mesma semântica de /health/live em Program.cs: Predicate = _ => false.
        var report = await healthCheckService.CheckHealthAsync(_ => false);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Empty(report.Entries);
    }
}
