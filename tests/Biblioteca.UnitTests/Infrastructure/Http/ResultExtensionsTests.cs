using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Biblioteca.UnitTests.Infrastructure.Http;

public sealed class ResultExtensionsTests
{
    [Fact]
    public void ToHttpResult_Sucesso_ChamaOnSuccessComOValor()
    {
        var result = Result<string>.Success("ok");

        var httpResult = result.ToHttpResult(value => TypedResults.Ok(value));

        var ok = Assert.IsType<Ok<string>>(httpResult);
        Assert.Equal("ok", ok.Value);
    }

    [Fact]
    public void ToHttpResult_Falha_RetornaProblemComCodeETitulo()
    {
        var error = new Error("book-unavailable", "Não há exemplar disponível", "detalhe", StatusCodes.Status409Conflict);
        Result<string> result = error;

        var httpResult = result.ToHttpResult(value => TypedResults.Ok(value));

        var problem = Assert.IsType<ProblemHttpResult>(httpResult);
        Assert.Equal(409, problem.ProblemDetails.Status);
        Assert.Equal("Não há exemplar disponível", problem.ProblemDetails.Title);
        Assert.Equal("https://biblioteca.dev/errors/book-unavailable", problem.ProblemDetails.Type);
        Assert.Equal("book-unavailable", problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public void ToProblemResult_ErroDeValidacao_RetornaValidationProblemComOsErrosDeCampo()
    {
        var errors = new Dictionary<string, string[]> { ["title"] = ["obrigatório"] };
        var error = Error.Validation(errors);

        var httpResult = error.ToProblemResult();

        var validationProblem = Assert.IsType<ValidationProblem>(httpResult);
        Assert.Equal(400, validationProblem.ProblemDetails.Status);
        Assert.Equal(errors["title"], validationProblem.ProblemDetails.Errors["title"]);
    }
}
