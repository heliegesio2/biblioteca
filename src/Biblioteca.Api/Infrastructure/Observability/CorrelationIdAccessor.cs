namespace Biblioteca.Api.Infrastructure.Observability;

/// <summary>
/// Torna o correlationId da requisição atual disponível fora da camada HTTP (ex.:
/// <c>AuditWriter</c>), sem que código de aplicação precise referenciar
/// <see cref="HttpContext"/> diretamente. Populado por <see cref="CorrelationIdMiddleware"/>
/// no início de cada requisição.
/// </summary>
public interface ICorrelationIdAccessor
{
    string CorrelationId { get; set; }
}

internal sealed class CorrelationIdAccessor : ICorrelationIdAccessor
{
    public string CorrelationId { get; set; } = string.Empty;
}
