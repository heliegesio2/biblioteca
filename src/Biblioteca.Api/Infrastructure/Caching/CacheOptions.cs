namespace Biblioteca.Api.Infrastructure.Caching;

/// <summary>Seção <c>Cache</c> de configuração (README §6, docs/caching.md).</summary>
public sealed class CacheOptions
{
    public int BookTtlSeconds { get; set; } = 300;
    public int AvailabilityTtlSeconds { get; set; } = 30;
}
