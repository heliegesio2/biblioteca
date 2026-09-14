namespace Biblioteca.Api.Features.Auth.Contracts;

public sealed record IssueTokenRequest(Guid UserId, string Role);
