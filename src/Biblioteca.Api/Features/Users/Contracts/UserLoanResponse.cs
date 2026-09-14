namespace Biblioteca.Api.Features.Users.Contracts;

public sealed record UserLoanResponse(
    Guid Id,
    Guid BookId,
    string Status,
    DateTimeOffset BorrowedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? ReturnedAt,
    DateTimeOffset? CancelledAt);
