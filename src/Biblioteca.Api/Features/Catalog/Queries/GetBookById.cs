using Biblioteca.Api.Features.Catalog.Commands;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Biblioteca.Api.Features.Catalog.Queries;

public sealed record GetBookByIdQuery(Guid Id) : IQuery<BookQueryResult>;

/// <summary>Livro + <see cref="ETag"/> já formatado a partir do xmin — o que é cacheado
/// (docs/caching.md), para que um hit resolva o ETag sem tocar o banco.</summary>
public sealed record BookQueryResult(BookResponse Book, string ETag);

internal sealed class GetBookByIdHandler(
    BibliotecaDbContext dbContext,
    HybridCache cache,
    ILogger<GetBookByIdHandler> logger)
    : IQueryHandler<GetBookByIdQuery, BookQueryResult>
{
    public async Task<Result<BookQueryResult>> Handle(GetBookByIdQuery query, CancellationToken cancellationToken)
    {
        BookQueryResult? result;

        try
        {
            result = await cache.GetOrCreateAsync(
                BookCache.BookKey(query.Id),
                (dbContext, query.Id),
                static (state, ct) => FetchAsync(state.dbContext, state.Id, ct),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Redis fora do ar (ou qualquer outra falha de cache) degrada para o banco —
            // nunca derruba a leitura (docs/caching.md#redis-fora-do-ar).
            logger.LogWarning(ex, "Falha ao acessar o cache do livro {BookId}; lendo direto do banco", query.Id);
            result = await FetchAsync(dbContext, query.Id, cancellationToken);
        }

        return result is null
            ? new Error("book-not-found", "Livro não encontrado",
                $"Não existe livro com o id '{query.Id}'.", StatusCodes.Status404NotFound)
            : Result<BookQueryResult>.Success(result);
    }

    private static async ValueTask<BookQueryResult?> FetchAsync(BibliotecaDbContext dbContext, Guid id, CancellationToken ct)
    {
        var row = await dbContext.Books.AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new { Book = b, Xmin = EF.Property<uint>(b, "xmin") })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : new BookQueryResult(CreateBookHandler.ToResponse(row.Book), ETag.From(row.Xmin));
    }
}
