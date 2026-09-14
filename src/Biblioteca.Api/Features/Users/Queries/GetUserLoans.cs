using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Features.Users.Contracts;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Users.Queries;

/// <param name="Status">Filtro opcional; texto de <see cref="LoanStatus"/> (Active/Returned/Cancelled).</param>
public sealed record GetUserLoansQuery(Guid UserId, string? Status, string? Cursor, int Limit)
    : IQuery<PagedResponse<UserLoanResponse>>;

public sealed class GetUserLoansValidator : AbstractValidator<GetUserLoansQuery>
{
    public GetUserLoansValidator()
    {
        RuleFor(q => q.Limit).InclusiveBetween(1, 100);
        RuleFor(q => q.Status)
            .Must(status => status is null || Enum.TryParse<LoanStatus>(status, out _))
            .WithMessage("status deve ser Active, Returned ou Cancelled.");
    }
}

/// <summary>
/// Consulta em Users que lê Loans diretamente — exceção declarada em
/// docs/architecture.md#organização.
/// </summary>
internal sealed class GetUserLoansHandler(BibliotecaDbContext dbContext)
    : IQueryHandler<GetUserLoansQuery, PagedResponse<UserLoanResponse>>
{
    public async Task<Result<PagedResponse<UserLoanResponse>>> Handle(
        GetUserLoansQuery query, CancellationToken cancellationToken)
    {
        var userExists = await dbContext.Users.AnyAsync(u => u.Id == query.UserId, cancellationToken);
        if (!userExists)
        {
            return new Error("user-not-found", "Usuário não encontrado",
                $"Não existe usuário com o id '{query.UserId}'.", StatusCodes.Status404NotFound);
        }

        var loans = dbContext.Set<Loan>().AsNoTracking().Where(l => l.UserId == query.UserId);

        if (query.Status is not null && Enum.TryParse<LoanStatus>(query.Status, out var status))
        {
            loans = loans.Where(l => l.Status == status);
        }

        if (Guid.TryParse(query.Cursor, out var cursorId))
        {
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
            .Select(l => new UserLoanResponse(
                l.Id, l.BookId, l.Status.ToString(), l.BorrowedAt, l.DueAt, l.ReturnedAt, l.CancelledAt))
            .ToList();

        return Result<PagedResponse<UserLoanResponse>>.Success(new PagedResponse<UserLoanResponse>(items, nextCursor));
    }
}
