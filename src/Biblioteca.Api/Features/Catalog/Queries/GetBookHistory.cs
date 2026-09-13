using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Catalog.Queries;

/// <param name="Status">Filtro opcional; texto de <see cref="LoanStatus"/> (Active/Returned/Cancelled).</param>
public sealed record GetBookHistoryQuery(Guid BookId, string? Status, string? Cursor, int Limit)
    : IQuery<PagedResponse<BookHistoryItemResponse>>;

public sealed class GetBookHistoryValidator : AbstractValidator<GetBookHistoryQuery>
{
    public GetBookHistoryValidator()
    {
        RuleFor(q => q.Limit).InclusiveBetween(1, 100);
        RuleFor(q => q.Status)
            .Must(status => status is null || Enum.TryParse<LoanStatus>(status, out _))
            .WithMessage("status deve ser Active, Returned ou Cancelled.");
    }
}

/// <summary>
/// Consulta em Catalog que lê Loans diretamente — exceção declarada em
/// docs/architecture.md#organização, é a mesma natureza de projeção de outras queries,
/// não acoplamento de regra de negócio entre os dois slices.
/// </summary>
internal sealed class GetBookHistoryHandler(BibliotecaDbContext dbContext)
    : IQueryHandler<GetBookHistoryQuery, PagedResponse<BookHistoryItemResponse>>
{
    public async Task<Result<PagedResponse<BookHistoryItemResponse>>> Handle(
        GetBookHistoryQuery query, CancellationToken cancellationToken)
    {
        var bookExists = await dbContext.Books.AnyAsync(b => b.Id == query.BookId, cancellationToken);
        if (!bookExists)
        {
            return new Error("book-not-found", "Livro não encontrado",
                $"Não existe livro com o id '{query.BookId}'.", StatusCodes.Status404NotFound);
        }

        var loans = dbContext.Set<Loan>().AsNoTracking().Where(l => l.BookId == query.BookId);

        if (query.Status is not null && Enum.TryParse<LoanStatus>(query.Status, out var status))
        {
            loans = loans.Where(l => l.Status == status);
        }

        if (Guid.TryParse(query.Cursor, out var cursorId))
        {
            // Mais recente primeiro (id desc, Guid v7 é ordenável no tempo) -> a próxima
            // página continua abaixo do último id devolvido.
            loans = loans.Where(l => l.Id.CompareTo(cursorId) < 0);
        }

        var rows = await loans
            .OrderByDescending(l => l.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > query.Limit;
        var pageItems = hasMore ? rows.Take(query.Limit).ToList() : rows;
        var nextCursor = hasMore ? pageItems[^1].Id.ToString() : null;

        var items = pageItems
            .Select(l => new BookHistoryItemResponse(
                l.Id, l.UserId, l.Status.ToString(), l.BorrowedAt, l.DueAt, l.ReturnedAt, l.CancelledAt))
            .ToList();

        return Result<PagedResponse<BookHistoryItemResponse>>.Success(new PagedResponse<BookHistoryItemResponse>(items, nextCursor));
    }
}
