using System.Collections.Concurrent;
using Biblioteca.Api.Infrastructure.Observability;

namespace Biblioteca.Api.Infrastructure.Cqrs;

public interface IDispatcher
{
    Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken);

    Task<Result<TResult>> Send<TResult>(IQuery<TResult> query, CancellationToken cancellationToken);
}

/// <summary>
/// Sem MediatR (ADR-0002): resolve <see cref="ICommandHandler{TCommand,TResult}"/> /
/// <see cref="IQueryHandler{TQuery,TResult}"/> pelo container. Como só conhecemos
/// <c>ICommand&lt;TResult&gt;</c>/<c>IQuery&lt;TResult&gt;</c> aqui — não o tipo concreto
/// do comando —, um wrapper genérico fechado em tempo de execução (via
/// <see cref="Activator.CreateInstance(Type)"/>, cacheado por tipo) faz a ponte sem
/// reflexão a cada chamada.
/// </summary>
internal sealed class Dispatcher(IServiceProvider serviceProvider) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> CommandWrappers = new();
    private static readonly ConcurrentDictionary<Type, object> QueryWrappers = new();

    public async Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken)
    {
        var wrapper = (ICommandWrapper<TResult>)CommandWrappers.GetOrAdd(
            command.GetType(),
            commandType => Activator.CreateInstance(
                typeof(CommandWrapper<,>).MakeGenericType(commandType, typeof(TResult)))!);

        using var activity = BibliotecaActivitySource.Instance.StartActivity(command.GetType().Name);
        return await wrapper.Handle(command, serviceProvider, cancellationToken);
    }

    public Task<Result<TResult>> Send<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
    {
        var wrapper = (IQueryWrapper<TResult>)QueryWrappers.GetOrAdd(
            query.GetType(),
            queryType => Activator.CreateInstance(
                typeof(QueryWrapper<,>).MakeGenericType(queryType, typeof(TResult)))!);

        return wrapper.Handle(query, serviceProvider, cancellationToken);
    }

    private interface ICommandWrapper<TResult>
    {
        Task<Result<TResult>> Handle(object command, IServiceProvider provider, CancellationToken cancellationToken);
    }

    private interface IQueryWrapper<TResult>
    {
        Task<Result<TResult>> Handle(object query, IServiceProvider provider, CancellationToken cancellationToken);
    }

    private sealed class CommandWrapper<TCommand, TResult> : ICommandWrapper<TResult>
        where TCommand : ICommand<TResult>
    {
        public Task<Result<TResult>> Handle(object command, IServiceProvider provider, CancellationToken cancellationToken) =>
            provider.GetRequiredService<ICommandHandler<TCommand, TResult>>().Handle((TCommand)command, cancellationToken);
    }

    private sealed class QueryWrapper<TQuery, TResult> : IQueryWrapper<TResult>
        where TQuery : IQuery<TResult>
    {
        public Task<Result<TResult>> Handle(object query, IServiceProvider provider, CancellationToken cancellationToken) =>
            provider.GetRequiredService<IQueryHandler<TQuery, TResult>>().Handle((TQuery)query, cancellationToken);
    }
}
