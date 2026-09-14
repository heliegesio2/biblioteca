using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Loans.Queries;

/// <summary>
/// Só para a checagem de autorização de ReturnLoan (docs/security.md#autorização) — o
/// próprio comando já recusa com 404 se o empréstimo não existir, então o valor
/// <see langword="null"/> aqui não precisa de tratamento de erro à parte.
/// </summary>
public sealed record GetLoanOwnerQuery(Guid LoanId) : IQuery<Guid?>;

internal sealed class GetLoanOwnerHandler(BibliotecaDbContext dbContext) : IQueryHandler<GetLoanOwnerQuery, Guid?>
{
    public async Task<Result<Guid?>> Handle(GetLoanOwnerQuery query, CancellationToken cancellationToken)
    {
        var userId = await dbContext.Loans.AsNoTracking()
            .Where(l => l.Id == query.LoanId)
            .Select(l => (Guid?)l.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        return Result<Guid?>.Success(userId);
    }
}
