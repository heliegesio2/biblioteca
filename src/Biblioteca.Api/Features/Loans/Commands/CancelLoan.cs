using Biblioteca.Api.Features.Audit;
using Biblioteca.Api.Features.Catalog;
using Biblioteca.Api.Features.Loans.Contracts;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Infrastructure.Caching;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Observability;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Loans.Commands;

public sealed record CancelLoanCommand(Guid LoanId, string? Reason) : ICommand<LoanResponse>;

public sealed class CancelLoanValidator : AbstractValidator<CancelLoanCommand>
{
    public CancelLoanValidator() => RuleFor(c => c.LoanId).NotEmpty();
}

/// <summary>
/// Diferente de devolver só no significado (docs/domain-model.md#diferença-entre-devolver-e-cancelar):
/// cancelar diz que o empréstimo não deveria ter existido. Mesma transição condicional
/// atômica de ReturnLoan.
/// </summary>
internal sealed class CancelLoanHandler(
    BibliotecaDbContext dbContext,
    ICacheInvalidationQueue cacheInvalidation,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    LoanMetrics metrics)
    : ICommandHandler<CancelLoanCommand, LoanResponse>
{
    public async Task<Result<LoanResponse>> Handle(CancelLoanCommand command, CancellationToken cancellationToken)
    {
        var loan = await dbContext.Loans.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == command.LoanId, cancellationToken);
        if (loan is null)
        {
            return new Error("loan-not-found", "Empréstimo não encontrado",
                $"Não existe empréstimo com o id '{command.LoanId}'.", StatusCodes.Status404NotFound);
        }

        var now = timeProvider.GetUtcNow();

        var loanAffected = await dbContext.Loans
            .Where(l => l.Id == command.LoanId && l.Status == LoanStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, LoanStatus.Cancelled)
                .SetProperty(l => l.CancelledAt, now)
                .SetProperty(l => l.UpdatedAt, now), cancellationToken);

        if (loanAffected == 0)
        {
            return new Error("loan-not-active", "Empréstimo não está ativo",
                $"O empréstimo '{command.LoanId}' já foi finalizado.", StatusCodes.Status409Conflict);
        }

        await dbContext.Books
            .Where(b => b.Id == loan.BookId && b.AvailableCopies < b.TotalCopies)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.AvailableCopies, b => b.AvailableCopies + 1)
                .SetProperty(b => b.UpdatedAt, now), cancellationToken);

        var availableCopiesAfter = await dbContext.Books.AsNoTracking()
            .Where(b => b.Id == loan.BookId)
            .Select(b => b.AvailableCopies)
            .FirstAsync(cancellationToken);

        auditWriter.Record("Loan", loan.Id, AuditActions.LoanCancelled, new
        {
            reason = command.Reason,
            availableCopiesAfter,
        });

        cacheInvalidation.Enqueue(BookCache.BookKey(loan.BookId));
        cacheInvalidation.Enqueue(BookCache.AvailabilityKey(loan.BookId));

        metrics.LoanCancelled();
        return Result<LoanResponse>.Success(new LoanResponse(
            loan.Id, loan.BookId, loan.UserId, nameof(LoanStatus.Cancelled), loan.BorrowedAt, loan.DueAt, null, now));
    }
}
