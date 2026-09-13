using Biblioteca.Api.Infrastructure.Cqrs;

namespace Biblioteca.Api.Infrastructure.Http;

/// <summary>
/// Traduz <see cref="Result{T}"/> devolvido pelo handler em <see cref="IResult"/> HTTP.
/// O endpoint só chama isto — nenhuma regra de negócio, nenhum <c>try/catch</c>
/// (docs/architecture.md#como-um-endpoint-fica).
/// </summary>
public static class ResultExtensions
{
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error!.ToProblemResult();

    /// <summary>Para comandos cujo sucesso não carrega valor de resposta (<see cref="Unit"/>).</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<IResult> onSuccess) =>
        result.IsSuccess ? onSuccess() : result.Error!.ToProblemResult();

    public static IResult ToProblemResult(this Error error) =>
        error.ValidationErrors is { } validationErrors
            ? TypedResults.ValidationProblem(
                validationErrors,
                title: error.Title,
                detail: error.Detail,
                type: $"https://biblioteca.dev/errors/{error.Code}",
                extensions: new Dictionary<string, object?> { ["code"] = error.Code })
            : TypedResults.Problem(
                title: error.Title,
                detail: error.Detail,
                statusCode: error.HttpStatus,
                type: $"https://biblioteca.dev/errors/{error.Code}",
                extensions: new Dictionary<string, object?> { ["code"] = error.Code });
}
