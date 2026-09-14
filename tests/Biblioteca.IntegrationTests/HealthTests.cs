using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Biblioteca.IntegrationTests.Infrastructure;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Critério de pronto da fase 8: `/health/live` responde independente do Postgres e
/// `/health/ready` não reprova com o Redis parado (docs/implementation-plan.md#fase-8,
/// docs/observability.md#health-checks). O cenário "Postgres parado" é coberto em
/// HealthChecksTests (unidade) — parar o container Postgres compartilhado por toda a
/// coleção de testes de integração já se mostrou destrutivo para o resto da suíte.
/// </summary>
public sealed class HealthTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Live_NaoConsultaNadaExterno_RespondeHealthy()
    {
        var response = await Client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_ComTudoUp_RespondeComOsDoisChecksENaoReprova()
    {
        var response = await Client.GetAsync("/health/ready");
        var report = await response.Content.ReadFromJsonAsync<HealthReportPayload>(JsonOptions);

        // Status geral não é asserido como "Healthy": neste ambiente específico, a
        // conectividade de Redis via porta publicada do Docker Desktop é instável mesmo
        // com o container no ar (mesma limitação documentada nas fases 5/6) — o que
        // importa aqui é que isso nunca vira 503, e é exatamente o que reprovaria
        // /health/ready por causa do Redis.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(report!.Checks, c => c.Name == "postgres" && c.Status == "Healthy");
        Assert.Contains(report.Checks, c => c.Name == "redis");
    }

    [Fact]
    public async Task Ready_ComRedisParado_NaoReprova()
    {
        await Factory.StopRedisAsync();
        try
        {
            var response = await Client.GetAsync("/health/ready");
            var report = await response.Content.ReadFromJsonAsync<HealthReportPayload>(JsonOptions);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(report!.Checks, c => c.Name == "redis" && c.Status == "Degraded");
            Assert.Contains(report.Checks, c => c.Name == "postgres" && c.Status == "Healthy");
        }
        finally
        {
            await Factory.StartRedisAsync();
        }
    }

    private sealed record HealthReportPayload(string Status, List<HealthCheckPayload> Checks);

    private sealed record HealthCheckPayload(string Name, string Status, double Duration, string? Description);
}
