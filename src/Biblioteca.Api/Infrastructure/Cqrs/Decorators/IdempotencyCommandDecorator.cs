namespace Biblioteca.Api.Infrastructure.Cqrs.Decorators;

/// <summary>
/// Camada mais interna do pipeline, dentro da transação (a reserva da chave e o efeito
/// que ela protege precisam ser atômicos — docs/architecture.md#o-pipeline-cqrs).
///
/// Hoje nenhum comando implementa <see cref="IIdempotentCommand"/>, então este decorator
/// só repassa para o handler. A reserva/replay via <c>IdempotencyStore</c> chega na
/// fase 4 junto com <c>CreateLoanCommand</c> — ver docs/idempotency.md. Ele já ocupa a
/// posição certa no pipeline para que essa mudança não reordene os decoradores.
/// </summary>
internal sealed class IdempotencyCommandDecorator<TCommand, TResult>(ICommandHandler<TCommand, TResult> inner)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken) =>
        inner.Handle(command, cancellationToken);
}
