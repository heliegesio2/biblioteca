namespace Biblioteca.Api.Infrastructure.Idempotency;

/// <summary>
/// Sinaliza ao endpoint que a resposta veio de um replay (docs/idempotency.md), sem que
/// o IdempotencyCommandDecorator precise conhecer HttpContext — mesmo padrão de
/// ICorrelationIdAccessor.
/// </summary>
public interface IIdempotencyReplayAccessor
{
    bool WasReplayed { get; set; }
}

internal sealed class IdempotencyReplayAccessor : IIdempotencyReplayAccessor
{
    public bool WasReplayed { get; set; }
}
