using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Biblioteca.Api.Features.Auth.Contracts;
using Biblioteca.Api.Infrastructure.Auth;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Biblioteca.Api.Features.Auth.Commands;

public sealed record IssueTokenCommand(Guid UserId, string Role) : ICommand<TokenResponse>;

public sealed class IssueTokenValidator : AbstractValidator<IssueTokenCommand>
{
    public IssueTokenValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.Role)
            .Must(role => role is "librarian" or "member")
            .WithMessage("role deve ser 'librarian' ou 'member'.");
    }
}

/// <summary>
/// Substituto declarado de um identity provider real (docs/security.md#post-authtoken):
/// emite um JWT para um usuário existente, sem cadastro de credenciais nem senha. Em
/// produção seria OIDC com chaves assimétricas — nada no domínio depende de como o token
/// foi emitido, só do ClaimsPrincipal resultante.
/// </summary>
internal sealed class IssueTokenHandler(
    BibliotecaDbContext dbContext,
    IOptions<AuthOptions> authOptions,
    TimeProvider timeProvider)
    : ICommandHandler<IssueTokenCommand, TokenResponse>
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(4);

    public async Task<Result<TokenResponse>> Handle(IssueTokenCommand command, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);

        if (user is null)
        {
            return new Error("user-not-found", "Usuário não encontrado",
                $"Não existe usuário com o id '{command.UserId}'.", StatusCodes.Status404NotFound);
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(TokenLifetime);

        var claims = new[]
        {
            new Claim("sub", user.Id.ToString()),
            new Claim("email", user.Email),
            new Claim("role", command.Role),
        };

        var options = authOptions.Value;
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        return Result<TokenResponse>.Success(new TokenResponse(accessToken, expiresAt, "Bearer"));
    }
}
