using Biblioteca.Api.Features.Audit;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Catalog.Domain;
using Biblioteca.Api.Infrastructure.Caching;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Catalog.Commands;

/// <summary><paramref name="ExpectedXmin"/> vem do <c>If-Match</c> — obrigatório, resolvido no endpoint.</summary>
public sealed record UpdateBookCommand(Guid Id, string? Title, string? Author, int? TotalCopies, uint ExpectedXmin)
    : ICommand<UpdateBookResult>;

/// <summary><see cref="ETag"/> já formatado (com aspas), calculado depois do SaveChanges interno do handler.</summary>
public sealed record UpdateBookResult(BookResponse Book, string ETag);

public sealed class UpdateBookValidator : AbstractValidator<UpdateBookCommand>
{
    public UpdateBookValidator()
    {
        RuleFor(c => c.Title).MaximumLength(300).When(c => c.Title is not null);
        RuleFor(c => c.Author).MaximumLength(200).When(c => c.Author is not null);
        RuleFor(c => c.TotalCopies).GreaterThanOrEqualTo(0).When(c => c.TotalCopies is not null);
    }
}

internal sealed class UpdateBookHandler(
    BibliotecaDbContext dbContext,
    ICacheInvalidationQueue cacheInvalidation,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
    : ICommandHandler<UpdateBookCommand, UpdateBookResult>
{
    public async Task<Result<UpdateBookResult>> Handle(UpdateBookCommand command, CancellationToken cancellationToken)
    {
        var book = await dbContext.Books.FirstOrDefaultAsync(b => b.Id == command.Id, cancellationToken);
        if (book is null)
        {
            return new Error("book-not-found", "Livro não encontrado",
                $"Não existe livro com o id '{command.Id}'.", StatusCodes.Status404NotFound);
        }

        // Concorrência otimista: força o UPDATE a checar xmin = ExpectedXmin. Se a linha
        // mudou desde a leitura do cliente, 0 linhas afetadas -> DbUpdateConcurrencyException,
        // traduzida pelo TransactionCommandDecorator em 412 (docs/concurrency.md).
        dbContext.Entry(book).Property("xmin").OriginalValue = command.ExpectedXmin;

        var now = timeProvider.GetUtcNow();
        var changes = new Dictionary<string, object>();

        if (command.Title is not null && command.Title != book.Title)
        {
            changes["title"] = new { from = book.Title, to = command.Title };
        }

        if (command.Author is not null && command.Author != book.Author)
        {
            changes["author"] = new { from = book.Author, to = command.Author };
        }

        if (command.Title is not null || command.Author is not null)
        {
            book.Rename(command.Title ?? book.Title, command.Author ?? book.Author, now);
        }

        if (command.TotalCopies is not null && command.TotalCopies != book.TotalCopies)
        {
            var fromTotal = book.TotalCopies;
            var result = book.ChangeTotalCopies(command.TotalCopies.Value, now);

            if (result == ChangeTotalCopiesResult.CopiesBelowActiveLoans)
            {
                var borrowed = book.TotalCopies - book.AvailableCopies;
                return new Error("copies-below-active-loans", "Quantidade abaixo dos exemplares emprestados",
                    $"Há {borrowed} exemplar(es) emprestado(s); não é possível reduzir para {command.TotalCopies}.",
                    StatusCodes.Status422UnprocessableEntity);
            }

            changes["totalCopies"] = new { from = fromTotal, to = command.TotalCopies };
        }

        if (changes.Count > 0)
        {
            auditWriter.Record("Book", book.Id, AuditActions.BookUpdated, changes);
        }

        // Flush explícito para obter o xmin novo (pós-UPDATE) antes de montar a resposta;
        // é seguro chamar de novo no TransactionCommandDecorator — não há mais nada
        // pendente e a chamada extra é um no-op.
        await dbContext.SaveChangesAsync(cancellationToken);

        cacheInvalidation.Enqueue(BookCache.BookKey(book.Id));
        cacheInvalidation.Enqueue(BookCache.AvailabilityKey(book.Id));

        var newXmin = (uint)dbContext.Entry(book).Property("xmin").CurrentValue!;
        return Result<UpdateBookResult>.Success(new UpdateBookResult(CreateBookHandler.ToResponse(book), ETag.From(newXmin)));
    }
}
