namespace Biblioteca.Api.Features.Users.Contracts;

public sealed record UserResponse(Guid Id, string Name, string Email, bool IsActive, DateTimeOffset CreatedAt);
