using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Biblioteca.Api.Infrastructure.Idempotency;

public enum IdempotencyOutcome
{
    /// <summary>Chave nova — o handler deve prosseguir.</summary>
    Reserved,

    /// <summary>Já <c>Completed</c> com o mesmo hash — devolver a resposta salva.</summary>
    Replayed,

    /// <summary>Outra transação está processando esta chave agora (docs/idempotency.md#2-repetição-concorrente).</summary>
    InProgress,

    /// <summary>Mesma chave, corpo diferente.</summary>
    KeyReuse,
}

public sealed record IdempotencyReservation(IdempotencyOutcome Outcome, IdempotencyKeyEntry Entry);

/// <summary>
/// Reserva/conclui uma chave de idempotência na mesma transação do comando que ela
/// protege (ADR-0005). O <c>INSERT</c> de reserva é flushado imediatamente (não espera
/// o <c>SaveChanges</c> final do TransactionCommandDecorator) para que o índice único da
/// PK sirva de verdade como ponto de serialização entre requisições concorrentes.
/// </summary>
internal sealed class IdempotencyStore(BibliotecaDbContext dbContext, IOptions<IdempotencyOptions> options)
{
    private const string SavepointName = "idempotency_reserve";

    public async Task<IdempotencyReservation> ReserveAsync(
        string key, string endpoint, string requestHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "IdempotencyStore.ReserveAsync exige uma transação ambiente (TransactionCommandDecorator).");

        // Sem savepoint, uma violação de unicidade aqui deixaria a transação inteira
        // "abortada" no protocolo do PostgreSQL — nenhuma consulta subsequente (nem o
        // SELECT da linha existente, logo abaixo) funcionaria até um ROLLBACK.
        await transaction.CreateSavepointAsync(SavepointName, cancellationToken);

        var entry = IdempotencyKeyEntry.CreateInProgress(
            key, endpoint, requestHash, now, TimeSpan.FromHours(options.Value.RetentionHours));

        dbContext.Set<IdempotencyKeyEntry>().Add(entry);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new IdempotencyReservation(IdempotencyOutcome.Reserved, entry);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackToSavepointAsync(SavepointName, cancellationToken);
            dbContext.Entry(entry).State = EntityState.Detached;

            var existing = await dbContext.Set<IdempotencyKeyEntry>().AsNoTracking()
                .FirstAsync(e => e.Key == key, cancellationToken);

            if (existing.RequestHash != requestHash)
            {
                return new IdempotencyReservation(IdempotencyOutcome.KeyReuse, existing);
            }

            return new IdempotencyReservation(
                existing.Status == IdempotencyStatus.Completed ? IdempotencyOutcome.Replayed : IdempotencyOutcome.InProgress,
                existing);
        }
    }

    /// <summary>Marca a chave como concluída — a mutação é flushada no SaveChanges final
    /// do TransactionCommandDecorator, junto com o resto do efeito do comando.</summary>
    public static void Complete(IdempotencyKeyEntry entry, int responseStatus, string responseBody, Guid? resourceId) =>
        entry.Complete(responseStatus, responseBody, resourceId);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505" };
}
