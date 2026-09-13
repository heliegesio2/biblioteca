using FluentValidation;

namespace Biblioteca.Api.Infrastructure.Cqrs.Decorators;

/// <summary>Input inválido não abre transação no banco — roda antes de Transaction.</summary>
internal sealed class ValidationCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var errors = await ValidationSupport.ValidateAsync(validators, command, cancellationToken);

        return errors is null
            ? await inner.Handle(command, cancellationToken)
            : Error.Validation(errors);
    }
}
