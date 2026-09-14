namespace Biblioteca.Api.Infrastructure.Idempotency;

public enum IdempotencyStatus
{
    InProgress,
    Completed,
}

/// <summary>
/// Uma linha de <c>idempotency_keys</c> (docs/data-integrity.md). A chave é a PK — é o
/// índice único dela que serializa duas requisições concorrentes com a mesma
/// Idempotency-Key (docs/idempotency.md).
/// </summary>
public sealed class IdempotencyKeyEntry
{
    private IdempotencyKeyEntry()
    {
    }

    public string Key { get; private set; } = string.Empty;
    public string Endpoint { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public IdempotencyStatus Status { get; private set; }
    public int? ResponseStatus { get; private set; }
    public string? ResponseBody { get; private set; }
    public Guid? ResourceId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public static IdempotencyKeyEntry CreateInProgress(
        string key, string endpoint, string requestHash, DateTimeOffset now, TimeSpan retention) =>
        new()
        {
            Key = key,
            Endpoint = endpoint,
            RequestHash = requestHash,
            Status = IdempotencyStatus.InProgress,
            CreatedAt = now,
            ExpiresAt = now + retention,
        };

    public void Complete(int responseStatus, string responseBody, Guid? resourceId)
    {
        Status = IdempotencyStatus.Completed;
        ResponseStatus = responseStatus;
        ResponseBody = responseBody;
        ResourceId = resourceId;
    }
}
