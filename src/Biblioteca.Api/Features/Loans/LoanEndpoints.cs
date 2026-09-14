using Biblioteca.Api.Features.Loans.Commands;
using Biblioteca.Api.Features.Loans.Contracts;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Idempotency;
using Microsoft.AspNetCore.Mvc;

namespace Biblioteca.Api.Features.Loans;

/// <summary>
/// Papel (`librarian` ou o próprio `member`) chega na fase 7 — sem autorização por
/// enquanto.
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
            HttpContext httpContext,
            CancellationToken ct) =>
        {
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

        group.MapPost("/{id:guid}/return", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send(new ReturnLoanCommand(id), ct);
            return result.ToHttpResult(Results.Ok);
        }).WithName("ReturnLoan");

        group.MapPost("/{id:guid}/cancel", async (
            Guid id, CancelLoanRequest? request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send(new CancelLoanCommand(id, request?.Reason), ct);
            return result.ToHttpResult(Results.Ok);
        }).WithName("CancelLoan");

        return app;
    }
}
