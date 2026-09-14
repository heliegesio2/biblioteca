using System.Text.Json;

namespace Biblioteca.Api.Features.Audit.Contracts;

/// <summary><see cref="Payload"/> é o JSON já estruturado (não uma string escapada) —
/// ver docs/api-contract.md#auditoria.</summary>
public sealed record AuditEventResponse(
    long Id,
    string EntityType,
    Guid EntityId,
    string Action,
    string Actor,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    JsonElement Payload);
