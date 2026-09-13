using Biblioteca.Api.Infrastructure.Caching;

namespace Biblioteca.Api.Infrastructure.Cqrs.Decorators;

/// <summary>
/// Drena <see cref="ICacheInvalidationQueue"/> só depois de <paramref name="inner"/>
/// terminar com sucesso — como este decorator embrulha o TransactionCommandDecorator
/// (está mais "fora" no pipeline), quando chegamos aqui o commit já aconteceu. Invalidar
/// antes do commit deixaria uma leitura concorrente repopular o cache com o valor antigo
/// (ADR-0007).
///
/// A invalidação de fato no Redis/HybridCache chega na fase 5 (docs/caching.md); por ora
/// só drenamos e registramos a intenção, para que os handlers já possam enfileirar chaves
/// desde já sem esperar o cache existir.
/// </summary>
internal sealed class CacheInvalidationCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ICacheInvalidationQueue queue,
    ILogger<CacheInvalidationCommandDecorator<TCommand, TResult>> logger)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var result = await inner.Handle(command, cancellationToken);

        if (result.IsSuccess)
        {
            var keys = queue.DrainPending();

            if (keys.Count > 0)
            {
                logger.LogDebug("Invalidação de cache pendente para as chaves {CacheKeys}", keys);
            }
        }

        return result;
    }
}
