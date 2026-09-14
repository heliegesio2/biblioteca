using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Biblioteca.Api.Infrastructure.Observability;
using Biblioteca.IntegrationTests.Infrastructure;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Os quatro instrumentos exigidos pelo enunciado (docs/observability.md#métricas), sob o
/// `Meter` "Biblioteca.Api": um <see cref="MeterListener"/> observa o mesmo processo do
/// próprio pipeline em vez de depender de um coletor OTLP externo.
/// </summary>
public sealed class MetricsTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    private sealed record Measurement(string Instrument, object Value, KeyValuePair<string, object?>[] Tags);

    private static MeterListener StartListener(ConcurrentBag<Measurement> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LoanMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, tags.ToArray())));
        listener.Start();
        return listener;
    }

    [Fact]
    public async Task CreateLoan_Sucesso_IncrementaLoansCreatedComBookId()
    {
        var measurements = new ConcurrentBag<Measurement>();
        using var listener = StartListener(measurements);

        var bookId = await CreateBookAsync();
        var userId = await CreateUserAsync();

        var response = await PostLoanAsync(bookId, userId);
        response.EnsureSuccessStatusCode();

        var created = Assert.Single(measurements, m => m.Instrument == "biblioteca.loans.created");
        Assert.Equal(bookId.ToString(), created.Tags.Single(t => t.Key == "book_id").Value);
        Assert.Contains(measurements, m => m.Instrument == "biblioteca.loans.create.duration"
            && m.Tags.Any(t => t.Key == "outcome" && (string?)t.Value == "created"));
    }

    [Fact]
    public async Task CreateLoan_LivroIndisponivel_IncrementaLoansRejectedComReasonUnavailable()
    {
        var bookId = await CreateBookAsync(totalCopies: 1);
        var firstUserId = await CreateUserAsync();
        var secondUserId = await CreateUserAsync();

        (await PostLoanAsync(bookId, firstUserId)).EnsureSuccessStatusCode();

        var measurements = new ConcurrentBag<Measurement>();
        using var listener = StartListener(measurements);

        await PostLoanAsync(bookId, secondUserId);

        var rejected = Assert.Single(measurements, m => m.Instrument == "biblioteca.loans.rejected");
        Assert.Equal("unavailable", rejected.Tags.Single(t => t.Key == "reason").Value);
        Assert.Contains(measurements, m => m.Instrument == "biblioteca.loans.create.duration"
            && m.Tags.Any(t => t.Key == "outcome" && (string?)t.Value == "rejected"));
    }

    [Fact]
    public async Task CreateLoan_ChaveRepetida_IncrementaIdempotencyReplayed()
    {
        var bookId = await CreateBookAsync();
        var userId = await CreateUserAsync();
        var idempotencyKey = Guid.NewGuid().ToString();

        (await PostLoanAsync(bookId, userId, idempotencyKey)).EnsureSuccessStatusCode();

        var measurements = new ConcurrentBag<Measurement>();
        using var listener = StartListener(measurements);

        (await PostLoanAsync(bookId, userId, idempotencyKey)).EnsureSuccessStatusCode();

        Assert.Single(measurements, m => m.Instrument == "biblioteca.idempotency.replayed");
        Assert.Contains(measurements, m => m.Instrument == "biblioteca.loans.create.duration"
            && m.Tags.Any(t => t.Key == "outcome" && (string?)t.Value == "replayed"));
    }
}
