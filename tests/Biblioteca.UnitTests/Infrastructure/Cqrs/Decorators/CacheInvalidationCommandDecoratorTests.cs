using Biblioteca.Api.Infrastructure.Caching;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Cqrs.Decorators;
using Biblioteca.Api.Infrastructure.Observability;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Biblioteca.UnitTests.Infrastructure.Cqrs.Decorators;

public sealed class CacheInvalidationCommandDecoratorTests
{
    // HybridCache sem AddStackExchangeRedisCache funciona só com L1 (memória) — real o
    // bastante para testar a drenagem sem precisar de Redis.
    private static HybridCache CreateInMemoryHybridCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    private sealed class SpyCacheInvalidationQueue : ICacheInvalidationQueue
    {
        private readonly List<string> _enqueued = [];
        public int DrainCallCount { get; private set; }

        public void Enqueue(string cacheKey) => _enqueued.Add(cacheKey);

        public IReadOnlyCollection<string> DrainPending()
        {
            DrainCallCount++;
            var drained = _enqueued.ToArray();
            _enqueued.Clear();
            return drained;
        }
    }

    private sealed class EnqueueingCommandHandler(ICacheInvalidationQueue queue, bool succeed)
        : ICommandHandler<FakeCommand, string>
    {
        public Task<Result<string>> Handle(FakeCommand command, CancellationToken cancellationToken)
        {
            queue.Enqueue("book:1");

            return Task.FromResult(succeed
                ? Result<string>.Success("ok")
                : Result<string>.Failure(new Error("x", "x", "x", 400)));
        }
    }

    [Fact]
    public async Task Handle_Sucesso_DrenaAFilaDepoisDoInner()
    {
        var queue = new SpyCacheInvalidationQueue();
        var decorator = new CacheInvalidationCommandDecorator<FakeCommand, string>(
            new EnqueueingCommandHandler(queue, succeed: true),
            queue,
            CreateInMemoryHybridCache(),
            new LoanMetrics(),
            NullLogger<CacheInvalidationCommandDecorator<FakeCommand, string>>.Instance);

        var result = await decorator.Handle(new FakeCommand("x"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, queue.DrainCallCount);
    }

    [Fact]
    public async Task Handle_Falha_NaoDrenaAFila()
    {
        var queue = new SpyCacheInvalidationQueue();
        var decorator = new CacheInvalidationCommandDecorator<FakeCommand, string>(
            new EnqueueingCommandHandler(queue, succeed: false),
            queue,
            CreateInMemoryHybridCache(),
            new LoanMetrics(),
            NullLogger<CacheInvalidationCommandDecorator<FakeCommand, string>>.Instance);

        var result = await decorator.Handle(new FakeCommand("x"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(0, queue.DrainCallCount);
    }
}
