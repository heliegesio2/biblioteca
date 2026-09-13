namespace Biblioteca.Api.Infrastructure.Cqrs;

/// <summary>
/// Marca um comando que precisa de proteção contra repetição (retry de cliente, timeout
/// de rede, load balancer reenviando). <see cref="Decorators.IdempotencyCommandDecorator{TCommand,TResult}"/>
/// ignora comandos que não implementam esta interface. Hoje nenhum comando implementa —
/// o primeiro é <c>CreateLoanCommand</c>, na fase 4, junto com o <c>IdempotencyStore</c>
/// que grava a chave na mesma transação do efeito que ela protege. Ver docs/idempotency.md.
/// </summary>
public interface IIdempotentCommand
{
    string IdempotencyKey { get; }
}
