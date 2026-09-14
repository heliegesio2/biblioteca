using Biblioteca.Api.Infrastructure.Caching;
using Biblioteca.Api.Infrastructure.Observability;
using Microsoft.Extensions.Caching.Hybrid;

namespace Biblioteca.Api.Infrastructure.Cqrs.Decorators;

/// <summary>
/// Drena <see cref="ICacheInvalidationQueue"/> só depois de <paramref name="inner"/>
/// terminar com sucesso — como este decorator embrulha o TransactionCommandDecorator
/// (está mais "fora" no pipeline), quando chegamos aqui o commit já aconteceu. Invalidar
/// antes do commit deixaria uma leitura concorrente repopular o cache com o valor antigo
/// (ADR-0007).
/// </summary>
internal sealed class CacheInvalidationCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ICacheInvalidationQueue queue,
    HybridCache cache,
    LoanMetrics metrics,
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

            foreach (var key in keys)
            {
                try
                {
                    // CancellationToken.None: o commit já aconteceu; o cliente desistir
                    // da requisição não pode deixar uma chave velha no cache.
                    await cache.RemoveAsync(key, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Registrada e contada, mas nunca reverte a transação — o efeito já
                    // commitou, e desfazê-lo por causa do cache trocaria um problema de
                    // latência por um de corretude. O TTL curto limita a exposição
                    // (docs/caching.md).
                    metrics.CacheInvalidationFailed(KeyPrefix(key));
                    logger.LogWarning(ex, "Falha ao invalidar a chave de cache {CacheKey}", key);
                }
            }
        }

        return result;
    }

    private static string KeyPrefix(string key)
    {
        var separator = key.IndexOf(':');
        return separator < 0 ? key : key[..separator];
    }
}
