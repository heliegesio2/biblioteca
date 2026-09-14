using Biblioteca.Api.Features.Users.Commands;
using Biblioteca.Api.Features.Users.Contracts;
using Biblioteca.Api.Features.Users.Queries;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;

namespace Biblioteca.Api.Features.Users;

/// <summary>
/// Autorização (`member` só vê os próprios empréstimos) chega na fase 7 — sem
/// restrição por enquanto.
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
        }).WithName("CreateUser");

        group.MapGet("/{id:guid}/loans", async (
                Guid id, string? status, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                var query = new GetUserLoansQuery(id, status, cursor, limit ?? 20);
                var result = await dispatcher.Send(query, ct);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetUserLoans");

        return app;
    }
}
