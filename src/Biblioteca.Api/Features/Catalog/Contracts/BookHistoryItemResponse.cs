namespace Biblioteca.Api.Features.Catalog.Contracts;

public sealed record BookHistoryItemResponse(
    Guid Id,
    Guid UserId,
    string Status,
    DateTimeOffset BorrowedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? ReturnedAt,
    DateTimeOffset? CancelledAt);
