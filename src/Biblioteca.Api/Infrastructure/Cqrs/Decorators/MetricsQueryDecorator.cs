namespace Biblioteca.Api.Infrastructure.Cqrs.Decorators;

internal sealed class MetricsQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ILogger<MetricsQueryDecorator<TQuery, TResult>> logger,
    TimeProvider timeProvider)
    : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    public async Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken)
    {
        var start = timeProvider.GetTimestamp();
        var result = await inner.Handle(query, cancellationToken);
        var elapsed = timeProvider.GetElapsedTime(start);

        logger.LogInformation(
            "Consulta {QueryType} concluída em {ElapsedMilliseconds}ms com resultado {Outcome}",
            typeof(TQuery).Name,
            elapsed.TotalMilliseconds,
            result.IsSuccess ? "success" : result.Error!.Code);

        return result;
    }
}
