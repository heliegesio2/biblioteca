namespace Biblioteca.Api.Features.Audit;

/// <summary>
/// Vocabulário fechado de <c>AuditEvent.Action</c> (docs/auditing.md) — nunca strings
/// soltas nos handlers. Ações de empréstimo entram na fase 4.
/// </summary>
public static class AuditActions
{
    public const string BookCreated = nameof(BookCreated);
    public const string BookUpdated = nameof(BookUpdated);
    public const string BookDeactivated = nameof(BookDeactivated);
}
