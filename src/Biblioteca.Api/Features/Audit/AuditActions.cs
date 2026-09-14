namespace Biblioteca.Api.Features.Audit;

/// <summary>
/// Vocabulário fechado de <c>AuditEvent.Action</c> (docs/auditing.md) — nunca strings
/// soltas nos handlers.
/// </summary>
public static class AuditActions
{
    public const string BookCreated = nameof(BookCreated);
    public const string BookUpdated = nameof(BookUpdated);
    public const string BookDeactivated = nameof(BookDeactivated);
    public const string LoanCreated = nameof(LoanCreated);
    public const string LoanReturned = nameof(LoanReturned);
    public const string LoanCancelled = nameof(LoanCancelled);
}
