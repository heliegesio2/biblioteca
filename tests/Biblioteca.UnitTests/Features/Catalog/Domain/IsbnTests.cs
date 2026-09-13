using Biblioteca.Api.Features.Catalog.Domain;

namespace Biblioteca.UnitTests.Features.Catalog.Domain;

public sealed class IsbnTests
{
    [Theory]
    [InlineData("9780306406157")]
    [InlineData("978-0-306-40615-7")]
    [InlineData("0306406152")]
    [InlineData("0-306-40615-2")]
    public void TryParse_EntradaValida_RetornaTrue(string input)
    {
        var result = Isbn.TryParse(input, out _);

        Assert.True(result);
    }

    [Fact]
    public void TryParse_ComHifensEEspacos_NormalizaParaOMesmoValor()
    {
        Isbn.TryParse("978-0-306-40615-7", out var comHifen);
        Isbn.TryParse("9780306406157", out var semHifen);

        Assert.Equal(semHifen, comHifen);
        Assert.Equal("9780306406157", comHifen.Value);
    }

    [Fact]
    public void TryParse_Isbn10ComXMinusculo_NormalizaParaMaiusculo()
    {
        // 020163371x é um ISBN-10 válido (dígito verificador X)
        var result = Isbn.TryParse("020163371x", out var isbn);

        Assert.True(result);
        Assert.Equal("020163371X", isbn.Value);
    }

    [Theory]
    [InlineData("9780306406158")] // dígito verificador ISBN-13 errado
    [InlineData("0306406153")]    // dígito verificador ISBN-10 errado
    [InlineData("12345")]         // tamanho inválido
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_EntradaInvalida_RetornaFalse(string? input)
    {
        var result = Isbn.TryParse(input, out _);

        Assert.False(result);
    }

    [Fact]
    public void Parse_EntradaInvalida_LancaFormatException()
    {
        Assert.Throws<FormatException>(() => Isbn.Parse("invalido"));
    }

    [Fact]
    public void Equals_MesmoValorNormalizado_SaoIguais()
    {
        var a = Isbn.Parse("978-0-306-40615-7");
        var b = Isbn.Parse("9780306406157");

        Assert.Equal(a, b);
        Assert.True(a == b);
    }
}
