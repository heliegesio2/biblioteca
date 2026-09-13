namespace Biblioteca.Api.Features.Catalog;

/// <summary>
/// Nomes de chave de cache do livro (docs/caching.md). O "v1" é a versão do formato
/// serializado — sobe se o DTO mudar, para não colidir com entradas antigas durante um
/// deploy com réplicas convivendo. A leitura via HybridCache e os TTLs chegam na fase 5;
/// por ora os comandos de escrita já enfileiram estas chaves para invalidação
/// (ICacheInvalidationQueue), para que nada precise mudar aqui quando o cache existir.
/// </summary>
public static class BookCache
{
    public static string BookKey(Guid bookId) => $"book:v1:{bookId}";

    public static string AvailabilityKey(Guid bookId) => $"book:v1:{bookId}:availability";

    public static IReadOnlyCollection<string> KeysFor(Guid bookId) => [BookKey(bookId), AvailabilityKey(bookId)];
}
