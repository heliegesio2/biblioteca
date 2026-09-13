namespace Biblioteca.Api.Infrastructure.Cqrs.Decorators;

/// <summary>
/// Duração e contador por resultado, genérico para qualquer comando. Métricas de
/// negócio (biblioteca.loans.created, etc.) são outra coisa — ver LoanMetrics, fase 8.
/// </summary>
internal sealed class MetricsCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<MetricsCommandDecorator<TCommand, TResult>> logger,
    TimeProvider timeProvider)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var start = timeProvider.GetTimestamp();
        var result = await inner.Handle(command, cancellationToken);
        var elapsed = timeProvider.GetElapsedTime(start);

        logger.LogInformation(
            "Comando {CommandType} concluído em {ElapsedMilliseconds}ms com resultado {Outcome}",
            typeof(TCommand).Name,
            elapsed.TotalMilliseconds,
            result.IsSuccess ? "success" : result.Error!.Code);

        return result;
    }
}
