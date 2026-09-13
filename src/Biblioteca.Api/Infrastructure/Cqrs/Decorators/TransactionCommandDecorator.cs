using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Biblioteca.Api.Infrastructure.Cqrs.Decorators;

/// <summary>
/// Abre a transação (READ COMMITTED, o default do PostgreSQL) antes do handler e faz
/// commit só se o resultado for sucesso — falha de negócio (<see cref="Result{T}.IsFailure"/>)
/// causa rollback tanto quanto uma exceção. Retry automático apenas para conflito
/// transitório (40001 serialization failure, 40P01 deadlock), no máximo 3 tentativas,
/// com backoff exponencial curto — nunca para erro de negócio (docs/concurrency.md,
/// docs/data-integrity.md#transações).
/// </summary>
internal sealed class TransactionCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    BibliotecaDbContext dbContext,
    ILogger<TransactionCommandDecorator<TCommand, TResult>> logger)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    private const int MaxAttempts = 3;

    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var result = await inner.Handle(command, cancellationToken);

                if (result.IsSuccess)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return result;
            }
            catch (DbUpdateConcurrencyException)
            {
                // xmin não bateu no UPDATE: alguém editou o recurso entre a leitura do
                // cliente (ETag) e este PATCH. Só usado em edição de catálogo — ver
                // docs/concurrency.md.
                await transaction.RollbackAsync(CancellationToken.None);
                return Result<TResult>.Failure(Error.PreconditionFailed());
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Rede de segurança para a corrida rara entre a checagem prévia do
                // handler (que dá a mensagem específica, ex. "isbn-already-exists") e o
                // commit. Não é a garantia principal — o índice único é.
                await transaction.RollbackAsync(CancellationToken.None);
                return Result<TResult>.Failure(Error.Conflict());
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                await transaction.RollbackAsync(CancellationToken.None);

                var delay = TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt - 1));
                logger.LogWarning(ex,
                    "Tentativa {Attempt}/{MaxAttempts} de {CommandType} falhou por conflito transitório; nova tentativa em {DelayMilliseconds}ms",
                    attempt, MaxAttempts, typeof(TCommand).Name, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is PostgresException { SqlState: "40001" or "40P01" } ||
        ex.InnerException is PostgresException { SqlState: "40001" or "40P01" };

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505" };
}
