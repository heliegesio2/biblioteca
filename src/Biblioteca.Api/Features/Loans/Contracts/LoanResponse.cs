namespace Biblioteca.Api.Features.Loans.Contracts;

public sealed record LoanResponse(
    Guid Id,
    Guid BookId,
    Guid UserId,
    string Status,
    DateTimeOffset BorrowedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? ReturnedAt,
    DateTimeOffset? CancelledAt);
