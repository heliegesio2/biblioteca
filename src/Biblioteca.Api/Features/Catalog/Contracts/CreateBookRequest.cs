namespace Biblioteca.Api.Features.Catalog.Contracts;

public sealed record CreateBookRequest(string Isbn, string Title, string Author, int TotalCopies);
