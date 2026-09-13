using Biblioteca.Api.Features.Catalog.Commands;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Catalog.Queries;

public sealed record GetBookByIdQuery(Guid Id) : IQuery<BookQueryResult>;

/// <summary>Livro + <see cref="ETag"/> já formatado a partir do xmin — cacheada na fase 5.</summary>
public sealed record BookQueryResult(BookResponse Book, string ETag);

internal sealed class GetBookByIdHandler(BibliotecaDbContext dbContext) : IQueryHandler<GetBookByIdQuery, BookQueryResult>
{
    public async Task<Result<BookQueryResult>> Handle(GetBookByIdQuery query, CancellationToken cancellationToken)
    {
        var row = await dbContext.Books.AsNoTracking()
            .Where(b => b.Id == query.Id)
            .Select(b => new { Book = b, Xmin = EF.Property<uint>(b, "xmin") })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return new Error("book-not-found", "Livro não encontrado",
                $"Não existe livro com o id '{query.Id}'.", StatusCodes.Status404NotFound);
        }

        return Result<BookQueryResult>.Success(new BookQueryResult(CreateBookHandler.ToResponse(row.Book), ETag.From(row.Xmin)));
    }
}
