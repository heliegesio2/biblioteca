using System.Text.Json;
using Biblioteca.Api.Features.Audit.Domain;
using Biblioteca.Api.Infrastructure.Observability;
using Biblioteca.Api.Infrastructure.Persistence;

namespace Biblioteca.Api.Features.Audit;

/// <summary>
/// Grava o evento no mesmo <see cref="BibliotecaDbContext"/> do comando corrente — sem
/// <c>SaveChanges</c> próprio. O <c>TransactionCommandDecorator</c> comita tudo junto:
/// se o efeito existe, o evento existe (docs/auditing.md#gravação-transacional).
/// </summary>
public interface IAuditWriter
{
    void Record(string entityType, Guid entityId, string action, object payload);
}

internal sealed class AuditWriter(
    BibliotecaDbContext dbContext,
    ICorrelationIdAccessor correlationIdAccessor,
    TimeProvider timeProvider)
    : IAuditWriter
{
    // Substituído por CurrentActor (JWT -> ator) na fase 7.
    private const string SystemActor = "system";

    public void Record(string entityType, Guid entityId, string action, object payload)
    {
        var auditEvent = AuditEvent.Create(
            entityType,
            entityId,
            action,
            SystemActor,
            timeProvider.GetUtcNow(),
            correlationIdAccessor.CorrelationId,
            JsonSerializer.Serialize(payload));

        dbContext.Set<AuditEvent>().Add(auditEvent);
    }
}
