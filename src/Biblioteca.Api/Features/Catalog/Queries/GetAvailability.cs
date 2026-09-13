using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Catalog.Queries;

public sealed record GetAvailabilityQuery(Guid BookId) : IQuery<AvailabilityResponse>;

internal sealed class GetAvailabilityHandler(BibliotecaDbContext dbContext, TimeProvider timeProvider)
    : IQueryHandler<GetAvailabilityQuery, AvailabilityResponse>
{
    public async Task<Result<AvailabilityResponse>> Handle(GetAvailabilityQuery query, CancellationToken cancellationToken)
    {
        var book = await dbContext.Books.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == query.BookId, cancellationToken);

        if (book is null)
        {
            return new Error("book-not-found", "Livro não encontrado",
                $"Não existe livro com o id '{query.BookId}'.", StatusCodes.Status404NotFound);
        }

        // Leitura direta em Loans (exceção declarada em docs/architecture.md#organização).
        var activeLoans = await dbContext.Set<Loan>()
            .CountAsync(l => l.BookId == book.Id && l.Status == LoanStatus.Active, cancellationToken);

        return Result<AvailabilityResponse>.Success(new AvailabilityResponse(
            book.Id, book.Title, book.IsActive, book.TotalCopies, book.AvailableCopies,
            activeLoans, timeProvider.GetUtcNow()));
    }
}
