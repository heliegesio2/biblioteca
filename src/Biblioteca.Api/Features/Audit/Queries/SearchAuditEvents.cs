using System.Text.Json;
using Biblioteca.Api.Features.Audit.Contracts;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Audit.Queries;

public sealed record SearchAuditEventsQuery(
    string? EntityType,
    Guid? EntityId,
    string? Action,
    string? Actor,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Cursor,
    int Limit)
    : IQuery<PagedResponse<AuditEventResponse>>;

public sealed class SearchAuditEventsValidator : AbstractValidator<SearchAuditEventsQuery>
{
    public SearchAuditEventsValidator()
    {
        RuleFor(q => q.Limit).InclusiveBetween(1, 100);
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From!.Value)
            .When(q => q.From is not null && q.To is not null)
            .WithMessage("'to' deve ser maior ou igual a 'from'.");
    }
}

/// <summary>
/// Paginação keyset por id decrescente (a sequência já dá ordem total de escrita —
/// docs/data-integrity.md#audit_events). Os índices que sustentam os filtros estão na
/// migration da fase 3 (ix_audit_entity, ix_audit_occurred_at, ix_audit_action).
/// </summary>
internal sealed class SearchAuditEventsHandler(BibliotecaDbContext dbContext)
    : IQueryHandler<SearchAuditEventsQuery, PagedResponse<AuditEventResponse>>
{
    public async Task<Result<PagedResponse<AuditEventResponse>>> Handle(
        SearchAuditEventsQuery query, CancellationToken cancellationToken)
    {
        var events = dbContext.AuditEvents.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            events = events.Where(e => e.EntityType == query.EntityType);
        }

        if (query.EntityId is not null)
        {
            events = events.Where(e => e.EntityId == query.EntityId);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            events = events.Where(e => e.Action == query.Action);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            events = events.Where(e => e.Actor == query.Actor);
        }

        if (query.From is not null)
        {
            events = events.Where(e => e.OccurredAt >= query.From);
        }

        if (query.To is not null)
        {
            events = events.Where(e => e.OccurredAt <= query.To);
        }

        if (long.TryParse(query.Cursor, out var cursorId))
        {
            events = events.Where(e => e.Id < cursorId);
        }

        var rows = await events
            .OrderByDescending(e => e.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > query.Limit;
        var pageItems = hasMore ? rows.Take(query.Limit).ToList() : rows;
        var nextCursor = hasMore ? pageItems[^1].Id.ToString() : null;

        var items = pageItems
            .Select(e => new AuditEventResponse(
                e.Id, e.EntityType, e.EntityId, e.Action, e.Actor, e.OccurredAt, e.CorrelationId,
                JsonSerializer.Deserialize<JsonElement>(e.Payload)))
            .ToList();

        return Result<PagedResponse<AuditEventResponse>>.Success(new PagedResponse<AuditEventResponse>(items, nextCursor));
    }
}
