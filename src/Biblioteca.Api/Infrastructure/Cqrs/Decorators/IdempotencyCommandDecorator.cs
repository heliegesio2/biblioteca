using System.Diagnostics;
using System.Text.Json;
using Biblioteca.Api.Infrastructure.Idempotency;
using Biblioteca.Api.Infrastructure.Observability;

namespace Biblioteca.Api.Infrastructure.Cqrs.Decorators;

/// <summary>
/// Camada mais interna do pipeline, dentro da transação (a reserva da chave e o efeito
/// que ela protege precisam ser atômicos — docs/architecture.md#o-pipeline-cqrs).
///
/// Comandos que não implementam <see cref="IIdempotentCommand"/> passam direto — hoje só
/// <c>CreateLoanCommand</c> implementa. Ver docs/idempotency.md para o protocolo completo.
///
/// Também é onde `biblioteca.idempotency.replayed` e `biblioteca.loans.create.duration`
/// são medidos (docs/observability.md#métricas): como só CreateLoanCommand é idempotente
/// hoje, este decorator genérico é, na prática, o ponto de medição do endpoint de
/// empréstimo inteiro — se um segundo comando idempotente aparecer, a métrica de duração
/// precisa se tornar específica de Loans em vez de viver aqui.
/// </summary>
internal sealed class IdempotencyCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IdempotencyStore store,
    TimeProvider timeProvider,
    IIdempotencyReplayAccessor replayAccessor,
    LoanMetrics metrics)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    // Hoje o único comando idempotente responde 201 no sucesso (CreateLoanCommand). Se um
    // dia houver mais de um com status de sucesso diferente, isto precisa vir de
    // IIdempotentCommand em vez de fixo aqui.
    private const int SuccessStatus = StatusCodes.Status201Created;

    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        if (command is not IIdempotentCommand idempotentCommand)
        {
            return await inner.Handle(command, cancellationToken);
        }

        var requestHash = ComputeRequestHash(command);
        var now = timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();

        var reservation = await store.ReserveAsync(
            idempotentCommand.IdempotencyKey, typeof(TCommand).Name, requestHash, now, cancellationToken);

        switch (reservation.Outcome)
        {
            case IdempotencyOutcome.KeyReuse:
                return Result<TResult>.Failure(Error.IdempotencyKeyReuse());

            case IdempotencyOutcome.InProgress:
                return Result<TResult>.Failure(Error.IdempotencyInProgress());

            case IdempotencyOutcome.Replayed:
                replayAccessor.WasReplayed = true;
                metrics.IdempotencyReplayed(typeof(TCommand).Name);
                metrics.RecordCreateDuration(stopwatch.Elapsed.TotalMilliseconds, "replayed");
                return Result<TResult>.Success(JsonSerializer.Deserialize<TResult>(reservation.Entry.ResponseBody!)!);

            case IdempotencyOutcome.Reserved:
            default:
                var result = await inner.Handle(command, cancellationToken);

                if (result.IsSuccess)
                {
                    IdempotencyStore.Complete(
                        reservation.Entry,
                        SuccessStatus,
                        JsonSerializer.Serialize(result.Value),
                        TryExtractResourceId(result.Value));
                }

                // Falha de negócio: nada a fazer aqui — o TransactionCommandDecorator dá
                // rollback na transação inteira, e a chave InProgress some junto
                // (docs/idempotency.md#interação-com-rejeições-de-negócio).
                metrics.RecordCreateDuration(stopwatch.Elapsed.TotalMilliseconds, result.IsSuccess ? "created" : "rejected");
                return result;
        }
    }

    private static string ComputeRequestHash(TCommand command) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(command)));

    /// <summary>
    /// Convenção, não contrato: se o resultado tiver uma propriedade pública "Id" do tipo
    /// Guid, ela vira <c>resource_id</c> — só para consulta/depuração, o protocolo de
    /// replay não depende disto.
    /// </summary>
    private static Guid? TryExtractResourceId(TResult value)
    {
        var property = typeof(TResult).GetProperty("Id");
        return property?.PropertyType == typeof(Guid) ? (Guid?)property.GetValue(value) : null;
    }
}
