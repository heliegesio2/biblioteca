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

public sealed record CreateLoanCommand(Guid BookId, Guid UserId, string IdempotencyKey)
    : ICommand<LoanResponse>, IIdempotentCommand;

public sealed class CreateLoanValidator : AbstractValidator<CreateLoanCommand>
{
    public CreateLoanValidator()
    {
        RuleFor(c => c.BookId).NotEmpty();
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.IdempotencyKey).NotEmpty();
    }
}

/// <summary>
/// O caso de uso crítico do desafio. A decisão de disponibilidade é o UPDATE condicional
/// — uma única instrução, sem janela entre ler e escrever (ADR-0003, docs/concurrency.md).
/// As validações anteriores (usuário/livro ativos, limite, duplicidade) dão a mensagem
/// certa no caminho comum; não são a garantia de corretude sob concorrência.
/// </summary>
internal sealed class CreateLoanHandler(
    BibliotecaDbContext dbContext,
    ICacheInvalidationQueue cacheInvalidation,
    IAuditWriter auditWriter,
    LoanPolicy loanPolicy,
    TimeProvider timeProvider,
    LoanMetrics metrics)
    : ICommandHandler<CreateLoanCommand, LoanResponse>
{
    public async Task<Result<LoanResponse>> Handle(CreateLoanCommand command, CancellationToken cancellationToken)
    {
        var book = await dbContext.Books.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == command.BookId, cancellationToken);
        if (book is null)
        {
            return new Error("book-not-found", "Livro não encontrado",
                $"Não existe livro com o id '{command.BookId}'.", StatusCodes.Status404NotFound);
        }

        var user = await dbContext.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);
        if (user is null)
        {
            return new Error("user-not-found", "Usuário não encontrado",
                $"Não existe usuário com o id '{command.UserId}'.", StatusCodes.Status404NotFound);
        }

        var userActiveLoanBookIds = await dbContext.Loans.AsNoTracking()
            .Where(l => l.UserId == command.UserId && l.Status == LoanStatus.Active)
            .Select(l => l.BookId)
            .ToListAsync(cancellationToken);

        var eligibility = loanPolicy.EnsureCanBorrow(
            userIsActive: user.IsActive,
            bookIsActive: book.IsActive,
            bookHasAvailableCopy: book.AvailableCopies > 0,
            userActiveLoanCount: userActiveLoanBookIds.Count,
            userHasActiveLoanForBook: userActiveLoanBookIds.Contains(command.BookId));

        if (eligibility != LoanEligibility.Allowed)
        {
            metrics.LoanRejected(MapRejectionReason(eligibility));
            return MapEligibilityError(eligibility, book.Title);
        }

        var now = timeProvider.GetUtcNow();

        // A decisão de verdade: uma única instrução, condição e efeito juntos. Se 0
        // linhas forem afetadas, não havia exemplar — 409 de negócio, não erro genérico.
        // rows_affected no span é o atributo mais útil do sistema: distingue "não havia
        // exemplar" de "algo falhou" sem precisar de log (docs/observability.md#traces).
        using var decrementActivity = BibliotecaActivitySource.Instance.StartActivity("loan.decrement_availability");
        decrementActivity?.SetTag("book.id", command.BookId);

        var affected = await dbContext.Books
            .Where(b => b.Id == command.BookId && b.IsActive && b.AvailableCopies > 0)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.AvailableCopies, b => b.AvailableCopies - 1)
                .SetProperty(b => b.UpdatedAt, now), cancellationToken);

        decrementActivity?.SetTag("rows_affected", affected);

        if (affected == 0)
        {
            metrics.LoanRejected("unavailable");
            return new Error("book-unavailable", "Não há exemplar disponível",
                $"O livro '{book.Title}' não possui exemplares disponíveis no momento.", StatusCodes.Status409Conflict);
        }

        var dueAt = loanPolicy.ComputeDueAt(now);
        var loan = Loan.Create(command.BookId, command.UserId, now, dueAt);

        // ux_loans_active_book_user (índice único parcial) é a rede de segurança contra
        // empréstimo duplicado sob concorrência — a checagem acima dá a mensagem boa.
        dbContext.Loans.Add(loan);

        var availableCopiesAfter = book.AvailableCopies - 1;

        auditWriter.Record("Loan", loan.Id, AuditActions.LoanCreated, new
        {
            bookId = command.BookId,
            userId = command.UserId,
            dueAt,
            availableCopiesAfter,
        });

        cacheInvalidation.Enqueue(BookCache.BookKey(book.Id));
        cacheInvalidation.Enqueue(BookCache.AvailabilityKey(book.Id));

        metrics.LoanCreated(book.Id);
        return Result<LoanResponse>.Success(ToResponse(loan));
    }

    private static Error MapEligibilityError(LoanEligibility eligibility, string bookTitle) => eligibility switch
    {
        LoanEligibility.UserInactive => new Error("user-inactive", "Usuário inativo",
            "O usuário está inativo e não pode criar novos empréstimos.", StatusCodes.Status422UnprocessableEntity),
        LoanEligibility.BookInactive => new Error("book-inactive", "Livro inativo",
            $"O livro '{bookTitle}' está inativo.", StatusCodes.Status422UnprocessableEntity),
        LoanEligibility.UserLoanLimitReached => new Error("user-loan-limit-reached", "Limite de empréstimos atingido",
            "O usuário já atingiu o limite de empréstimos ativos.", StatusCodes.Status422UnprocessableEntity),
        LoanEligibility.DuplicateActiveLoan => new Error("duplicate-active-loan", "Empréstimo duplicado",
            $"O usuário já tem um empréstimo ativo do livro '{bookTitle}'.", StatusCodes.Status409Conflict),
        LoanEligibility.BookUnavailable => new Error("book-unavailable", "Não há exemplar disponível",
            $"O livro '{bookTitle}' não possui exemplares disponíveis no momento.", StatusCodes.Status409Conflict),
        _ => throw new ArgumentOutOfRangeException(nameof(eligibility), eligibility, null),
    };

    // Tags de docs/observability.md#métricas (`reason`), independentes do código de erro
    // HTTP (que é contrato de API e não pode mudar de formato por conveniência de métrica).
    private static string MapRejectionReason(LoanEligibility eligibility) => eligibility switch
    {
        LoanEligibility.UserInactive => "user_inactive",
        LoanEligibility.BookInactive => "book_inactive",
        LoanEligibility.UserLoanLimitReached => "limit_exceeded",
        LoanEligibility.DuplicateActiveLoan => "duplicate_active_loan",
        LoanEligibility.BookUnavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(eligibility), eligibility, null),
    };

    internal static LoanResponse ToResponse(Loan loan) => new(
        loan.Id, loan.BookId, loan.UserId, loan.Status.ToString(),
        loan.BorrowedAt, loan.DueAt, loan.ReturnedAt, loan.CancelledAt);
}
