namespace Biblioteca.Api.Infrastructure.Cqrs;

/// <summary>
/// Sucesso (<see cref="Value"/>) ou falha de negócio tipada (<see cref="Error"/>) — nunca
/// os dois. Handlers devolvem isto em vez de lançar exceção para rejeição esperada
/// (CLAUDE.md: "Rejeição de negócio não é exceção").
/// </summary>
public sealed class Result<T>
{
    private readonly T? _value;

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error? Error { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Não é possível acessar {nameof(Value)} de um resultado com falha ({Error!.Code}).");

    private Result(T value)
    {
        IsSuccess = true;
        _value = value;
    }

    private Result(Error error)
    {
        IsSuccess = false;
        Error = error;
    }

    public static Result<T> Success(T value) => new(value);
    public static Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
