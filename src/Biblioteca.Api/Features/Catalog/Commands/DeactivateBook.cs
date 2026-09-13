using Biblioteca.Api.Features.Audit;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Infrastructure.Caching;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Catalog.Commands;

public sealed record DeactivateBookCommand(Guid Id) : ICommand<Unit>;

public sealed class DeactivateBookValidator : AbstractValidator<DeactivateBookCommand>
{
    public DeactivateBookValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

internal sealed class DeactivateBookHandler(
    BibliotecaDbContext dbContext,
    ICacheInvalidationQueue cacheInvalidation,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
    : ICommandHandler<DeactivateBookCommand, Unit>
{
    public async Task<Result<Unit>> Handle(DeactivateBookCommand command, CancellationToken cancellationToken)
    {
        var book = await dbContext.Books.FirstOrDefaultAsync(b => b.Id == command.Id, cancellationToken);
        if (book is null)
        {
            return new Error("book-not-found", "Livro não encontrado",
                $"Não existe livro com o id '{command.Id}'.", StatusCodes.Status404NotFound);
        }

        // Idempotente por natureza (docs/api-contract.md): já desativado -> 204 de novo,
        // sem novo evento de auditoria nem nova invalidação de cache.
        if (!book.IsActive)
        {
            return Result<Unit>.Success(Unit.Value);
        }

        // Leitura direta em Loans (exceção declarada em docs/architecture.md#organização):
        // Catalog precisa saber se há empréstimo ativo para recusar a desativação.
        var activeLoans = await dbContext.Set<Loan>()
            .CountAsync(l => l.BookId == book.Id && l.Status == LoanStatus.Active, cancellationToken);

        if (activeLoans > 0)
        {
            return new Error("book-has-active-loans", "Livro com empréstimo ativo",
                $"O livro possui {activeLoans} empréstimo(s) ativo(s) e não pode ser desativado.",
                StatusCodes.Status409Conflict);
        }

        var now = timeProvider.GetUtcNow();
        book.Deactivate(now);

        auditWriter.Record("Book", book.Id, AuditActions.BookDeactivated, new { activeLoansAtDeactivation = 0 });

        cacheInvalidation.Enqueue(BookCache.BookKey(book.Id));
        cacheInvalidation.Enqueue(BookCache.AvailabilityKey(book.Id));

        return Result<Unit>.Success(Unit.Value);
    }
}
