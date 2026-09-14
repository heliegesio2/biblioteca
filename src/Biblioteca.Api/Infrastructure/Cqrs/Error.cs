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

    /// <summary>
    /// Usado pelo TransactionCommandDecorator quando um UPDATE otimista (xmin) afeta 0
    /// linhas — o recurso mudou desde que o cliente leu o ETag (docs/concurrency.md).
    /// </summary>
    public static Error PreconditionFailed() => new("precondition-failed", "Precondição falhou",
        "O recurso foi alterado por outra requisição desde a última leitura (ETag desatualizado).",
        StatusCodes.Status412PreconditionFailed);

    /// <summary>PATCH sem If-Match — checado no endpoint, antes de despachar qualquer comando.</summary>
    public static Error PreconditionRequired() => new("precondition-required", "If-Match obrigatório",
        "Esta operação exige o cabeçalho If-Match com o ETag atual do recurso.",
        StatusCodes.Status428PreconditionRequired);

    /// <summary>
    /// Rede de segurança genérica do TransactionCommandDecorator para violação de índice
    /// único não prevista por uma checagem prévia no handler (a checagem prévia dá a
    /// mensagem específica; isto cobre só a corrida rara entre a checagem e o commit).
    /// </summary>
    public static Error Conflict() => new("conflict", "Conflito de dados",
        "A operação conflita com um registro já existente.", StatusCodes.Status409Conflict);

    /// <summary>POST /loans sem Idempotency-Key — checado no endpoint (docs/idempotency.md).</summary>
    public static Error IdempotencyKeyMissing() => new("idempotency-key-missing", "Idempotency-Key obrigatória",
        "Esta operação exige o cabeçalho Idempotency-Key.", StatusCodes.Status400BadRequest);

    /// <summary>Mesma chave de idempotência usada com um corpo diferente.</summary>
    public static Error IdempotencyKeyReuse() => new("idempotency-key-reuse", "Idempotency-Key reutilizada",
        "A mesma chave de idempotência já foi usada com um corpo diferente.", StatusCodes.Status409Conflict);

    /// <summary>Outra requisição com a mesma chave ainda está em processamento.</summary>
    public static Error IdempotencyInProgress() => new("idempotency-in-progress", "Requisição em andamento",
        "Uma requisição com esta chave de idempotência já está em processamento.", StatusCodes.Status409Conflict);

    /// <summary>
    /// Acesso a recurso de terceiro (docs/security.md#autorização) — nunca 404: o recurso
    /// existe, e mascarar isso não protege nada num sistema em que os identificadores já
    /// são conhecidos de quem os criou.
    /// </summary>
    public static Error Forbidden() => new("forbidden", "Acesso negado",
        "Você não tem permissão para acessar ou modificar este recurso.", StatusCodes.Status403Forbidden);
}
