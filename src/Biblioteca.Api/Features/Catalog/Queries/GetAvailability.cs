using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Infrastructure.Caching;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Biblioteca.Api.Features.Catalog.Queries;

public sealed record GetAvailabilityQuery(Guid BookId) : IQuery<AvailabilityResponse>;

internal sealed class GetAvailabilityHandler(
    BibliotecaDbContext dbContext,
    TimeProvider timeProvider,
    HybridCache cache,
    IOptions<CacheOptions> cacheOptions,
    ILogger<GetAvailabilityHandler> logger)
    : IQueryHandler<GetAvailabilityQuery, AvailabilityResponse>
{
    public async Task<Result<AvailabilityResponse>> Handle(GetAvailabilityQuery query, CancellationToken cancellationToken)
    {
        var options = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromSeconds(cacheOptions.Value.AvailabilityTtlSeconds),
            LocalCacheExpiration = TimeSpan.FromSeconds(5),
        };

        AvailabilityResponse? result;

        try
        {
            result = await cache.GetOrCreateAsync(
                BookCache.AvailabilityKey(query.BookId),
                (dbContext, timeProvider, query.BookId),
                static (state, ct) => FetchAsync(state.dbContext, state.timeProvider, state.BookId, ct),
                options,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Redis fora do ar degrada para o banco — nunca derruba a leitura
            // (docs/caching.md#redis-fora-do-ar).
            logger.LogWarning(ex, "Falha ao acessar o cache de disponibilidade de {BookId}; lendo direto do banco", query.BookId);
            result = await FetchAsync(dbContext, timeProvider, query.BookId, cancellationToken);
        }

        return result is null
            ? new Error("book-not-found", "Livro não encontrado",
                $"Não existe livro com o id '{query.BookId}'.", StatusCodes.Status404NotFound)
            : Result<AvailabilityResponse>.Success(result);
    }

    // Leitura direta em Loans (exceção declarada em docs/architecture.md#organização).
    private static async ValueTask<AvailabilityResponse?> FetchAsync(
        BibliotecaDbContext dbContext, TimeProvider timeProvider, Guid bookId, CancellationToken ct)
    {
        var book = await dbContext.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookId, ct);
        if (book is null)
        {
            return null;
        }

        var activeLoans = await dbContext.Set<Loan>()
            .CountAsync(l => l.BookId == bookId && l.Status == LoanStatus.Active, ct);

        return new AvailabilityResponse(
            book.Id, book.Title, book.IsActive, book.TotalCopies, book.AvailableCopies, activeLoans, timeProvider.GetUtcNow());
    }
}
