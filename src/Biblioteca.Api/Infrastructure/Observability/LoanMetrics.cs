using System.Diagnostics.Metrics;

namespace Biblioteca.Api.Infrastructure.Observability;

/// <summary>
/// Os instrumentos de docs/observability.md#métricas, sob o `Meter` "Biblioteca.Api"
/// exportado por OpenTelemetry. Uma única instância singleton — o `Meter` vive tanto
/// quanto o processo.
/// </summary>
public sealed class LoanMetrics : IDisposable
{
    public const string MeterName = "Biblioteca.Api";

    private readonly Meter meter;
    private readonly Counter<long> loansCreated;
    private readonly Counter<long> loansRejected;
    private readonly Counter<long> idempotencyReplayed;
    private readonly Histogram<double> loanCreateDuration;
    private readonly Counter<long> cacheInvalidationFailed;
    private readonly Counter<long> loansReturned;
    private readonly Counter<long> loansCancelled;

    public LoanMetrics()
    {
        meter = new Meter(MeterName);
        loansCreated = meter.CreateCounter<long>("biblioteca.loans.created");
        loansRejected = meter.CreateCounter<long>("biblioteca.loans.rejected");
        idempotencyReplayed = meter.CreateCounter<long>("biblioteca.idempotency.replayed");
        loanCreateDuration = meter.CreateHistogram<double>("biblioteca.loans.create.duration", unit: "ms");
        cacheInvalidationFailed = meter.CreateCounter<long>("biblioteca.cache.invalidation.failed");
        loansReturned = meter.CreateCounter<long>("biblioteca.loans.returned");
        loansCancelled = meter.CreateCounter<long>("biblioteca.loans.cancelled");
    }

    public void LoanCreated(Guid bookId) =>
        loansCreated.Add(1, new KeyValuePair<string, object?>("book_id", bookId.ToString()));

    public void LoanRejected(string reason) =>
        loansRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void IdempotencyReplayed(string endpoint) =>
        idempotencyReplayed.Add(1, new KeyValuePair<string, object?>("endpoint", endpoint));

    public void RecordCreateDuration(double elapsedMilliseconds, string outcome) =>
        loanCreateDuration.Record(elapsedMilliseconds, new KeyValuePair<string, object?>("outcome", outcome));

    public void CacheInvalidationFailed(string keyPrefix) =>
        cacheInvalidationFailed.Add(1, new KeyValuePair<string, object?>("key_prefix", keyPrefix));

    public void LoanReturned() => loansReturned.Add(1);

    public void LoanCancelled() => loansCancelled.Add(1);

    public void Dispose() => meter.Dispose();
}
