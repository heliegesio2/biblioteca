using System.Security.Claims;
using Biblioteca.Api.Features.Users.Commands;
using Biblioteca.Api.Features.Users.Contracts;
using Biblioteca.Api.Features.Users.Queries;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Microsoft.AspNetCore.Authorization;

namespace Biblioteca.Api.Features.Users;

/// <summary>
/// `POST /users` exige `librarian`. `GET /{id}/loans` é a regra transversal
/// (docs/security.md#autorização): `librarian` vê qualquer um, `member` só o próprio —
/// checado como autorização baseada em recurso, não como `if` dentro do handler.
/// </summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users").WithTags("Users");

        group.MapPost("/", async (CreateUserRequest request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var command = new CreateUserCommand(request.Name, request.Email);
            var result = await dispatcher.Send(command, ct);
            return result.ToHttpResult(user => Results.Created($"/users/{user.Id}", user));
        }).RequireAuthorization("Librarian").WithName("CreateUser");

        group.MapGet("/{id:guid}/loans", async (
                Guid id, string? status, string? cursor, int? limit,
                IDispatcher dispatcher, IAuthorizationService authorizationService, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var authResult = await authorizationService.AuthorizeAsync(user, id, "SameUserOrLibrarian");
                if (!authResult.Succeeded)
                {
                    return Error.Forbidden().ToProblemResult();
                }

                var query = new GetUserLoansQuery(id, status, cursor, limit ?? 20);
                var result = await dispatcher.Send(query, ct);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetUserLoans");

        return app;
    }
}
