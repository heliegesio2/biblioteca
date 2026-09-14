using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Cqrs.SelfTest;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Biblioteca.IntegrationTests.Infrastructure;

/// <summary>
/// PostgreSQL e Redis reais via Testcontainers (ADR-0006 — sem EF InMemory),
/// compartilhados por toda a <see cref="PostgresCollection"/>. Aplica as migrations reais
/// na subida e expõe <see cref="ResetAsync"/> (Respawn) para isolar os testes entre si sem
/// recriar os containers a cada um.
///
/// Também injeta, só para teste, uma rota que expõe <c>PingCommand</c>
/// (Infrastructure/Cqrs/SelfTest) via HTTP — prova que Problem Details/correlationId
/// funcionam de ponta a ponta sem adicionar essa rota à API real.
/// </summary>
public sealed class PostgresApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("biblioteca")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    private Respawner? _respawner;

    async Task IAsyncLifetime.InitializeAsync()
    {
        await Task.WhenAll(_container.StartAsync(), _redis.StartAsync());

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BibliotecaDbContext>();
        await dbContext.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();

        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
        });
    }

    /// <summary>Simula Redis fora do ar (docs/caching.md#redis-fora-do-ar) — parar/religar
    /// o mesmo container preserva o mapeamento de porta, então a connection string
    /// configurada no host continua válida depois de <see cref="StartRedisAsync"/>.</summary>
    public Task StopRedisAsync() => _redis.StopAsync();

    public Task StartRedisAsync() => _redis.StartAsync();

    public async Task ResetAsync()
    {
        if (_respawner is null)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _container.GetConnectionString(),
                ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
            }));

        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, PingEndpointStartupFilter>());
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await Task.WhenAll(_container.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
        await base.DisposeAsync();
    }

    private sealed class PingEndpointStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);

            app.UseEndpoints(endpoints => endpoints.MapPost(
                    "/__selftest/ping",
                    async (PingCommand command, IDispatcher dispatcher, CancellationToken ct) =>
                    {
                        var result = await dispatcher.Send(command, ct);
                        return result.ToHttpResult(value => Results.Ok(value));
                    })
                .AllowAnonymous()); // fase 7: sem isto, a fallback policy (autenticado por padrão) bloquearia
        };
    }
}
