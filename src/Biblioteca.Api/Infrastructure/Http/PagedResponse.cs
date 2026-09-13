namespace Biblioteca.Api.Infrastructure.Http;

/// <summary>Página de resultado com paginação keyset — <see cref="NextCursor"/> é opaco.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, string? NextCursor);
