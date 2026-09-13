using Biblioteca.Api.Features.Audit;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Catalog.Domain;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Catalog.Commands;

public sealed record CreateBookCommand(string Isbn, string Title, string Author, int TotalCopies)
    : ICommand<BookResponse>;

public sealed class CreateBookValidator : AbstractValidator<CreateBookCommand>
{
    public CreateBookValidator()
    {
        RuleFor(c => c.Isbn)
            .NotEmpty()
            .Must(isbn => Isbn.TryParse(isbn, out _))
            .WithMessage("ISBN inválido (dígito verificador não confere).");

        RuleFor(c => c.Title).NotEmpty().MaximumLength(300);
        RuleFor(c => c.Author).NotEmpty().MaximumLength(200);
        RuleFor(c => c.TotalCopies).GreaterThanOrEqualTo(0);
    }
}

internal sealed class CreateBookHandler(
    BibliotecaDbContext dbContext,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
    : ICommandHandler<CreateBookCommand, BookResponse>
{
    public async Task<Result<BookResponse>> Handle(CreateBookCommand command, CancellationToken cancellationToken)
    {
        // Já validado pelo ValidationCommandDecorator; Parse não deveria falhar aqui.
        var isbn = Isbn.Parse(command.Isbn);

        // Mensagem específica no caminho comum; o índice único (ux_books_isbn) e o
        // catch de violação no TransactionCommandDecorator cobrem a corrida rara.
        var isbnTaken = await dbContext.Books.AnyAsync(b => b.Isbn == isbn, cancellationToken);
        if (isbnTaken)
        {
            return new Error("isbn-already-exists", "ISBN já cadastrado",
                $"Já existe um livro cadastrado com o ISBN '{isbn.Value}'.", StatusCodes.Status409Conflict);
        }

        var now = timeProvider.GetUtcNow();
        var book = Book.Create(isbn, command.Title, command.Author, command.TotalCopies, now);
        dbContext.Books.Add(book);

        auditWriter.Record("Book", book.Id, AuditActions.BookCreated, new
        {
            isbn = isbn.Value,
            title = command.Title,
            author = command.Author,
            totalCopies = command.TotalCopies,
        });

        // POST /books não invalida cache: não existe entrada anterior para o id novo
        // (docs/caching.md#quem-invalida-o-quê).

        return Result<BookResponse>.Success(ToResponse(book));
    }

    internal static BookResponse ToResponse(Book book) => new(
        book.Id, book.Isbn.Value, book.Title, book.Author,
        book.TotalCopies, book.AvailableCopies, book.IsActive,
        book.CreatedAt, book.UpdatedAt);
}
