using Biblioteca.Api.Features.Catalog.Commands;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Catalog.Queries;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;

namespace Biblioteca.Api.Features.Catalog;

/// <summary>
/// Só tradução HTTP → comando/consulta → HTTP (docs/architecture.md#como-um-endpoint-fica).
/// Papéis (`librarian`) chegam na fase 7 — sem autorização por enquanto, qualquer chamada
/// passa.
/// </summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/books").WithTags("Catalog");

        group.MapPost("/", async (CreateBookRequest request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var command = new CreateBookCommand(request.Isbn, request.Title, request.Author, request.TotalCopies);
            var result = await dispatcher.Send(command, ct);
            return result.ToHttpResult(book => Results.Created($"/books/{book.Id}", book));
        }).WithName("CreateBook");

        group.MapGet("/", async (
                IDispatcher dispatcher, CancellationToken ct,
                string? search, bool includeInactive = false, bool availableOnly = false, string? cursor = null, int? limit = null) =>
            {
                var query = new SearchBooksQuery(search, includeInactive, availableOnly, cursor, limit ?? 20);
                var result = await dispatcher.Send(query, ct);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("SearchBooks");

        group.MapGet("/{id:guid}", async (Guid id, HttpContext httpContext, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send(new GetBookByIdQuery(id), ct);

            if (result.IsFailure)
            {
                return result.Error!.ToProblemResult();
            }

            httpContext.Response.Headers["ETag"] = result.Value.ETag;

            var ifNoneMatch = httpContext.Request.Headers["If-None-Match"].ToString();
            return !string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == result.Value.ETag
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Ok(result.Value.Book);
        }).WithName("GetBookById");

        group.MapPatch("/{id:guid}", async (
            Guid id, UpdateBookRequest request, HttpContext httpContext, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var ifMatch = httpContext.Request.Headers["If-Match"].ToString();

            if (string.IsNullOrEmpty(ifMatch) || !ETag.TryParse(ifMatch, out var expectedXmin))
            {
                return Error.PreconditionRequired().ToProblemResult();
            }

            var command = new UpdateBookCommand(id, request.Title, request.Author, request.TotalCopies, expectedXmin);
            var result = await dispatcher.Send(command, ct);

            if (result.IsFailure)
            {
                return result.Error!.ToProblemResult();
            }

            httpContext.Response.Headers["ETag"] = result.Value.ETag;
            return Results.Ok(result.Value.Book);
        }).WithName("UpdateBook");

        group.MapDelete("/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send(new DeactivateBookCommand(id), ct);
            return result.ToHttpResult(Results.NoContent);
        }).WithName("DeactivateBook");

        group.MapGet("/{id:guid}/availability", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send(new GetAvailabilityQuery(id), ct);
            return result.ToHttpResult(Results.Ok);
        }).WithName("GetBookAvailability");

        group.MapGet("/{id:guid}/history", async (
                Guid id, string? status, string? cursor, int? limit, IDispatcher dispatcher, CancellationToken ct) =>
            {
                var query = new GetBookHistoryQuery(id, status, cursor, limit ?? 20);
                var result = await dispatcher.Send(query, ct);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetBookHistory");

        return app;
    }
}
