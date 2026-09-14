namespace Biblioteca.Api.Features.Loans.Contracts;

public sealed record CreateLoanRequest(Guid BookId, Guid UserId);
