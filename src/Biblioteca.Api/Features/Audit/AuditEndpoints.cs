using Biblioteca.Api.Features.Audit.Queries;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;

namespace Biblioteca.Api.Features.Audit;

/// <summary>Exige o papel `librarian` (docs/security.md#autorização).</summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/audit-events", async (
                string? entityType, Guid? entityId, string? action, string? actor,
                DateTimeOffset? from, DateTimeOffset? to, string? cursor, int? limit,
                IDispatcher dispatcher, CancellationToken ct) =>
            {
                var query = new SearchAuditEventsQuery(entityType, entityId, action, actor, from, to, cursor, limit ?? 20);
                var result = await dispatcher.Send(query, ct);
                return result.ToHttpResult(Results.Ok);
            })
            .WithTags("Audit")
            .RequireAuthorization("Librarian")
            .WithName("SearchAuditEvents");

        return app;
    }
}
