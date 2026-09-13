namespace Biblioteca.Api.Features.Catalog.Contracts;

/// <summary>Campos ausentes (<see langword="null"/>) não são alterados — nunca usado para apagar valor.</summary>
public sealed record UpdateBookRequest(string? Title, string? Author, int? TotalCopies);
