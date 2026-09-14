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

public sealed record ReturnLoanCommand(Guid LoanId) : ICommand<LoanResponse>;

public sealed class ReturnLoanValidator : AbstractValidator<ReturnLoanCommand>
{
    public ReturnLoanValidator() => RuleFor(c => c.LoanId).NotEmpty();
}

/// <summary>
/// Mesma técnica de CreateLoan, condição diferente: a transição de status é que é
/// atômica (docs/concurrency.md#devolução-e-cancelamento). Duas devoluções simultâneas
/// do mesmo empréstimo — uma vence, a outra recebe 409, e o exemplar volta uma vez só.
/// </summary>
internal sealed class ReturnLoanHandler(
    BibliotecaDbContext dbContext,
    ICacheInvalidationQueue cacheInvalidation,
    IAuditWriter auditWriter,
    TimeProvider timeProvider,
    LoanMetrics metrics)
    : ICommandHandler<ReturnLoanCommand, LoanResponse>
{
    public async Task<Result<LoanResponse>> Handle(ReturnLoanCommand command, CancellationToken cancellationToken)
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
                .SetProperty(l => l.Status, LoanStatus.Returned)
                .SetProperty(l => l.ReturnedAt, now)
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

        var daysLate = Math.Max(0, (now - loan.DueAt).Days);

        auditWriter.Record("Loan", loan.Id, AuditActions.LoanReturned, new
        {
            returnedAt = now,
            dueAt = loan.DueAt,
            daysLate,
            availableCopiesAfter,
        });

        cacheInvalidation.Enqueue(BookCache.BookKey(loan.BookId));
        cacheInvalidation.Enqueue(BookCache.AvailabilityKey(loan.BookId));

        metrics.LoanReturned();
        return Result<LoanResponse>.Success(new LoanResponse(
            loan.Id, loan.BookId, loan.UserId, nameof(LoanStatus.Returned), loan.BorrowedAt, loan.DueAt, now, null));
    }
}
