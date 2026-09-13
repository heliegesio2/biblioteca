using Biblioteca.Api.Infrastructure.Cqrs;

namespace Biblioteca.UnitTests.Infrastructure.Cqrs;

public sealed class ResultTests
{
    [Fact]
    public void Success_ExpoeValorEIsSuccessTrue()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(42, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_IsSuccessFalseEValueLanca()
    {
        var error = new Error("book-unavailable", "Sem exemplar", "detalhe", 409);
        var result = Result<int>.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ConversaoImplicitaDeError_CriaFailure()
    {
        var error = new Error("book-unavailable", "Sem exemplar", "detalhe", 409);

        Result<int> result = error;

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }
}

public sealed class ErrorTests
{
    [Fact]
    public void Validation_DefineCodeFixoE400()
    {
        var errors = new Dictionary<string, string[]> { ["title"] = ["obrigatório"] };

        var error = Error.Validation(errors);

        Assert.Equal("validation-failed", error.Code);
        Assert.Equal(400, error.HttpStatus);
        Assert.Same(errors, error.ValidationErrors);
    }
}
