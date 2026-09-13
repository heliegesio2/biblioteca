namespace Biblioteca.Api.Infrastructure.Cqrs;

/// <summary>
/// Erro de negócio tipado. <see cref="Code"/> é o vocabulário fechado de
/// docs/api-contract.md#códigos-de-erro-de-negócio (ex.: "book-unavailable"),
/// <see cref="HttpStatus"/> é o status HTTP correspondente. <see cref="ResultExtensions"/>
/// (Infrastructure/Http) traduz isto para Problem Details (RFC 9457) — nunca uma exceção
/// para o que é rejeição de negócio esperada.
/// </summary>
public sealed record Error(string Code, string Title, string Detail, int HttpStatus)
{
    /// <summary>Erros de campo, por nome — presente só quando <see cref="Code"/> é "validation-failed".</summary>
    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; init; }

    public static Error Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new("validation-failed", "Erro de validação", "Um ou mais campos são inválidos.",
            StatusCodes.Status400BadRequest)
        {
            ValidationErrors = errors,
        };
}
