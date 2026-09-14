namespace Biblioteca.Api.Features.Auth.Contracts;

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, string TokenType);
