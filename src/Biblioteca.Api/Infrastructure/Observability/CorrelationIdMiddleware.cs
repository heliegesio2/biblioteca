namespace Biblioteca.Api.Infrastructure.Observability;

/// <summary>
/// Aceita <c>X-Correlation-Id</c> do cliente, gera um novo (<see cref="Guid.CreateVersion7"/>)
/// quando ausente, e devolve o valor em toda resposta — inclusive de erro
/// (docs/api-contract.md#cabeçalhos). Disponível ao resto do pipeline via
/// <see cref="HttpContextCorrelationIdExtensions.GetCorrelationId"/>.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var existing) && existing.Count > 0
            ? existing.ToString()
            : Guid.CreateVersion7().ToString();

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        await next(context);
    }
}

public static class HttpContextCorrelationIdExtensions
{
    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var value) && value is string correlationId
            ? correlationId
            : context.TraceIdentifier;
}
