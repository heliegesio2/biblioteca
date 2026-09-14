using System.Security.Claims;
using Biblioteca.Api.Features.Loans.Commands;
using Biblioteca.Api.Features.Loans.Contracts;
using Biblioteca.Api.Features.Loans.Queries;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Idempotency;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Biblioteca.Api.Features.Loans;

/// <summary>
/// `POST /loans` e `/return`: `librarian` (qualquer usuário) ou `member` (só para/de si
/// mesmo) — a regra transversal de docs/security.md#autorização. `/cancel` exige
/// `librarian`.
/// </summary>
public static class LoanEndpoints
{
    public static IEndpointRouteBuilder MapLoanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/loans").WithTags("Loans");

        group.MapPost("/", async (
            CreateLoanRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            IDispatcher dispatcher,
            IIdempotencyReplayAccessor replayAccessor,
            IAuthorizationService authorizationService,
            ClaimsPrincipal user,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var authResult = await authorizationService.AuthorizeAsync(user, request.UserId, "SameUserOrLibrarian");
            if (!authResult.Succeeded)
            {
                return Error.Forbidden().ToProblemResult();
            }

            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Error.IdempotencyKeyMissing().ToProblemResult();
            }

            var command = new CreateLoanCommand(request.BookId, request.UserId, idempotencyKey);
            var result = await dispatcher.Send(command, ct);

            if (result.IsFailure && result.Error!.Code == "idempotency-in-progress")
            {
                httpContext.Response.Headers["Retry-After"] = "1";
            }

            if (replayAccessor.WasReplayed)
            {
                httpContext.Response.Headers["Idempotency-Replayed"] = "true";
            }

            return result.ToHttpResult(loan => Results.Created($"/loans/{loan.Id}", loan));
        }).WithName("CreateLoan");

        group.MapPost("/{id:guid}/return", async (
            Guid id, IDispatcher dispatcher, IAuthorizationService authorizationService, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var ownerResult = await dispatcher.Send(new GetLoanOwnerQuery(id), ct);
            if (ownerResult.IsSuccess && ownerResult.Value is { } ownerId)
            {
                var authResult = await authorizationService.AuthorizeAsync(user, ownerId, "SameUserOrLibrarian");
                if (!authResult.Succeeded)
                {
                    return Error.Forbidden().ToProblemResult();
                }
            }
            // Empréstimo inexistente: sem checar posse, deixa o próprio comando
            // responder 404 — 403 para um recurso que não existe só confundiria.

            var result = await dispatcher.Send(new ReturnLoanCommand(id), ct);
            return result.ToHttpResult(Results.Ok);
        }).WithName("ReturnLoan");

        group.MapPost("/{id:guid}/cancel", async (
            Guid id, CancelLoanRequest? request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send(new CancelLoanCommand(id, request?.Reason), ct);
            return result.ToHttpResult(Results.Ok);
        }).RequireAuthorization("Librarian").WithName("CancelLoan");

        return app;
    }
}
