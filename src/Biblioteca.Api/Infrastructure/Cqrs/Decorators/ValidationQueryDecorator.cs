using FluentValidation;

namespace Biblioteca.Api.Infrastructure.Cqrs.Decorators;

internal sealed class ValidationQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    IEnumerable<IValidator<TQuery>> validators)
    : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    public async Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken)
    {
        var errors = await ValidationSupport.ValidateAsync(validators, query, cancellationToken);

        return errors is null
            ? await inner.Handle(query, cancellationToken)
            : Error.Validation(errors);
    }
}
