namespace Biblioteca.Api.Features.Loans.Contracts;

/// <summary>Corpo opcional — <see cref="Reason"/>, quando informado, vai para o payload de auditoria.</summary>
public sealed record CancelLoanRequest(string? Reason);
