namespace Biblioteca.Api.Infrastructure.Idempotency;

/// <summary>Seção <c>Idempotency</c> de configuração (README §6).</summary>
public sealed class IdempotencyOptions
{
    public int RetentionHours { get; set; } = 24;
}
