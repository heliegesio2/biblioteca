using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Cqrs.SelfTest;
using Biblioteca.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Critério de pronto da fase 2 (docs/implementation-plan.md#fase-2--infraestrutura-cqrs):
/// "um comando trivial atravessa o pipeline inteiro com transação, validação e Problem
/// Details funcionando" — contra PostgreSQL real via Testcontainers, não em memória
/// (ADR-0006).
/// </summary>
public sealed class CqrsPipelineTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Send_PingCommandValido_AtravessaOPipelineInteiroEComitaATransacao()
    {
        using var scope = Factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Send(new PingCommand("hello"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("pong: hello", result.Value);
    }

    [Fact]
    public async Task Send_PingCommandInvalido_ValidationDecoratorRecusaAntesDoHandler()
    {
        using var scope = Factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Send(new PingCommand(""), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("validation-failed", result.Error!.Code);
    }

    [Fact]
    public async Task PostPing_ComandoValido_Retorna200ComOCorpoEOCorrelationIdDevolvido()
    {
        Client.DefaultRequestHeaders.Add("X-Correlation-Id", "test-correlation-id");

        var response = await Client.PostAsJsonAsync("/__selftest/ping", new PingCommand("hello"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("test-correlation-id", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("\"pong: hello\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostPing_ComandoInvalido_Retorna400ComProblemDetailsECorrelationId()
    {
        var response = await Client.PostAsJsonAsync("/__selftest/ping", new PingCommand(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.Contains("Message", problem.Errors.Keys);
        Assert.True(response.Headers.Contains("X-Correlation-Id"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"validation-failed\"", body);
        Assert.Contains("\"correlationId\"", body);
        Assert.Contains("\"traceId\"", body);
    }
}
