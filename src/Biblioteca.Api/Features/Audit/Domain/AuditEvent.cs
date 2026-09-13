namespace Biblioteca.Api.Features.Audit.Domain;

/// <summary>
/// Trilha de negócio, append-only (docs/auditing.md) — não é log técnico. Gravada na
/// mesma transação da mudança que descreve: se a entidade existe, o evento existe.
/// </summary>
public sealed class AuditEvent
{
    private AuditEvent()
    {
    }

    public long Id { get; private set; }
    public string EntityType { get; private set; } = string.Empty;
    public Guid EntityId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string Actor { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>JSON já serializado — o suficiente para entender o que mudou.</summary>
    public string Payload { get; private set; } = string.Empty;

    public static AuditEvent Create(
        string entityType,
        Guid entityId,
        string action,
        string actor,
        DateTimeOffset occurredAt,
        string correlationId,
        string payloadJson) =>
        new()
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Actor = actor,
            OccurredAt = occurredAt,
            CorrelationId = correlationId,
            Payload = payloadJson,
        };
}
