using Biblioteca.Api.Features.Auth.Commands;
using Biblioteca.Api.Features.Auth.Contracts;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;

namespace Biblioteca.Api.Features.Auth;

/// <summary>Mapeado só fora de Production (Program.cs) — ver docs/security.md#post-authtoken.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/token", async (IssueTokenRequest request, IDispatcher dispatcher, CancellationToken ct) =>
            {
                var result = await dispatcher.Send(new IssueTokenCommand(request.UserId, request.Role), ct);
                return result.ToHttpResult(Results.Ok);
            })
            .WithTags("Auth")
            .AllowAnonymous()
            .WithName("IssueToken");

        return app;
    }
}
