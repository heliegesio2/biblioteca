namespace Biblioteca.Api.Features.Catalog.Contracts;

public sealed record BookResponse(
    Guid Id,
    string Isbn,
    string Title,
    string Author,
    int TotalCopies,
    int AvailableCopies,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
