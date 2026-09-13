namespace Biblioteca.Api.Infrastructure.Caching;

/// <summary>
/// Fila scoped de chaves de cache a invalidar. Handlers só enfileiram — nunca invalidam
/// diretamente (CLAUDE.md regra 2: "o caminho de escrita não lê cache", e o mesmo vale
/// para escrever nele fora deste contrato). O
/// <c>CacheInvalidationCommandDecorator</c> drena a fila depois do commit
/// (docs/caching.md, docs/architecture.md#o-pipeline-cqrs).
/// </summary>
public interface ICacheInvalidationQueue
{
    void Enqueue(string cacheKey);

    IReadOnlyCollection<string> DrainPending();
}

internal sealed class CacheInvalidationQueue : ICacheInvalidationQueue
{
    private readonly List<string> _pendingKeys = [];

    public void Enqueue(string cacheKey) => _pendingKeys.Add(cacheKey);

    public IReadOnlyCollection<string> DrainPending()
    {
        if (_pendingKeys.Count == 0)
        {
            return [];
        }

        var drained = _pendingKeys.ToArray();
        _pendingKeys.Clear();
        return drained;
    }
}
