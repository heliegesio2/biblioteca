using FluentValidation;

namespace Biblioteca.Api.Infrastructure.Cqrs.Decorators;

/// <summary>
/// Lógica de validação compartilhada entre <see cref="ValidationCommandDecorator{TCommand,TResult}"/>
/// e <see cref="ValidationQueryDecorator{TQuery,TResult}"/> — os dois fazem exatamente a
/// mesma coisa contra interfaces de handler diferentes.
/// </summary>
internal static class ValidationSupport
{
    public static async Task<IReadOnlyDictionary<string, string[]>?> ValidateAsync<T>(
        IEnumerable<IValidator<T>> validators, T instance, CancellationToken cancellationToken)
    {
        var validatorArray = validators as IValidator<T>[] ?? validators.ToArray();

        if (validatorArray.Length == 0)
        {
            return null;
        }

        var context = new ValidationContext<T>(instance);
        var results = await Task.WhenAll(
            validatorArray.Select(validator => validator.ValidateAsync(context, cancellationToken)));

        var failures = results.SelectMany(r => r.Errors).ToList();

        if (failures.Count == 0)
        {
            return null;
        }

        return failures
            .GroupBy(f => f.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray());
    }
}
