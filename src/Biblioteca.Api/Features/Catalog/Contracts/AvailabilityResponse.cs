namespace Biblioteca.Api.Features.Catalog.Contracts;

/// <summary><see cref="AsOf"/> é o instante em que o valor foi lido do banco — informativo,
/// nunca a base da decisão de emprestar (docs/concurrency.md).</summary>
public sealed record AvailabilityResponse(
    Guid BookId,
    string Title,
    bool IsActive,
    int TotalCopies,
    int AvailableCopies,
    int ActiveLoans,
    DateTimeOffset AsOf);
