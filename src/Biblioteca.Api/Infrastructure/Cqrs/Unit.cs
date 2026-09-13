namespace Biblioteca.Api.Infrastructure.Cqrs;

/// <summary>
/// Resultado de um comando que não produz valor além de "aconteceu com sucesso"
/// (ex.: DeactivateBook). Evita um <see cref="Result{T}"/> de <see cref="object"/>.
/// </summary>
public readonly record struct Unit
{
    public static readonly Unit Value;
}
