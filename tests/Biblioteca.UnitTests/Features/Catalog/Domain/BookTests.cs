using Biblioteca.Api.Features.Catalog.Domain;

namespace Biblioteca.UnitTests.Features.Catalog.Domain;

public sealed class BookTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Isbn SampleIsbn = Isbn.Parse("9780306406157");

    private static Book CreateBook(int totalCopies) =>
        Book.Create(SampleIsbn, "Dom Casmurro", "Machado de Assis", totalCopies, Now);

    [Fact]
    public void Create_DefineAvailableCopiesIgualATotalCopies()
    {
        var book = CreateBook(totalCopies: 3);

        Assert.Equal(3, book.TotalCopies);
        Assert.Equal(3, book.AvailableCopies);
        Assert.True(book.IsActive);
    }

    [Fact]
    public void Create_TotalCopiesNegativo_Lanca()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Book.Create(SampleIsbn, "Título", "Autor", totalCopies: -1, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_TituloVazio_Lanca(string title)
    {
        Assert.Throws<ArgumentException>(() => Book.Create(SampleIsbn, title, "Autor", 1, Now));
    }

    [Fact]
    public void Borrow_ComExemplarDisponivel_DecrementaERetornaTrue()
    {
        var book = CreateBook(totalCopies: 2);

        var result = book.Borrow(Now);

        Assert.True(result);
        Assert.Equal(1, book.AvailableCopies);
    }

    [Fact]
    public void Borrow_SemExemplarDisponivel_RetornaFalseENaoAltera()
    {
        var book = CreateBook(totalCopies: 1);
        book.Borrow(Now);

        var result = book.Borrow(Now);

        Assert.False(result);
        Assert.Equal(0, book.AvailableCopies);
    }

    [Fact]
    public void Borrow_LivroInativo_RetornaFalse()
    {
        var book = CreateBook(totalCopies: 5);
        book.Deactivate(Now);

        var result = book.Borrow(Now);

        Assert.False(result);
        Assert.Equal(5, book.AvailableCopies);
    }

    [Fact]
    public void Return_ComExemplarEmprestado_IncrementaERetornaTrue()
    {
        var book = CreateBook(totalCopies: 2);
        book.Borrow(Now);

        var result = book.Return(Now);

        Assert.True(result);
        Assert.Equal(2, book.AvailableCopies);
    }

    [Fact]
    public void Return_SemNadaEmprestado_RetornaFalse()
    {
        var book = CreateBook(totalCopies: 2);

        var result = book.Return(Now);

        Assert.False(result);
        Assert.Equal(2, book.AvailableCopies);
    }

    [Fact]
    public void ChangeTotalCopies_Aumentando_SomaODeltaAosDisponiveis()
    {
        var book = CreateBook(totalCopies: 3);
        book.Borrow(Now); // 3 total, 2 disponíveis

        var result = book.ChangeTotalCopies(5, Now);

        Assert.Equal(ChangeTotalCopiesResult.Success, result);
        Assert.Equal(5, book.TotalCopies);
        Assert.Equal(4, book.AvailableCopies);
    }

    [Fact]
    public void ChangeTotalCopies_DiminuindoDentroDoLimite_Sucede()
    {
        var book = CreateBook(totalCopies: 5);
        book.Borrow(Now); // 5 total, 4 disponíveis (1 emprestado)

        var result = book.ChangeTotalCopies(2, Now);

        Assert.Equal(ChangeTotalCopiesResult.Success, result);
        Assert.Equal(2, book.TotalCopies);
        Assert.Equal(1, book.AvailableCopies);
    }

    [Fact]
    public void ChangeTotalCopies_AbaixoDosExemplaresEmprestados_Recusa()
    {
        var book = CreateBook(totalCopies: 5);
        book.Borrow(Now);
        book.Borrow(Now);
        book.Borrow(Now); // 5 total, 2 disponíveis, 3 emprestados

        var result = book.ChangeTotalCopies(2, Now);

        Assert.Equal(ChangeTotalCopiesResult.CopiesBelowActiveLoans, result);
        // nada muda quando a operação é recusada
        Assert.Equal(5, book.TotalCopies);
        Assert.Equal(2, book.AvailableCopies);
    }

    [Fact]
    public void ChangeTotalCopies_Negativo_Lanca()
    {
        var book = CreateBook(totalCopies: 3);

        Assert.Throws<ArgumentOutOfRangeException>(() => book.ChangeTotalCopies(-1, Now));
    }

    [Fact]
    public void Deactivate_MarcaIsActiveComoFalse()
    {
        var book = CreateBook(totalCopies: 1);

        book.Deactivate(Now);

        Assert.False(book.IsActive);
    }
}
