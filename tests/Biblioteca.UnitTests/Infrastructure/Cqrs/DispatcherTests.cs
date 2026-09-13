using Biblioteca.Api.Infrastructure.Cqrs;
using Microsoft.Extensions.DependencyInjection;

namespace Biblioteca.UnitTests.Infrastructure.Cqrs;

public sealed class DispatcherTests
{
    private static IDispatcher BuildDispatcher(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IDispatcher, Dispatcher>();
        configure?.Invoke(services);
        return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
    }

    [Fact]
    public async Task Send_Command_ResolveEExecutaOHandlerRegistrado()
    {
        var dispatcher = BuildDispatcher(s => s.AddScoped<ICommandHandler<FakeCommand, string>, FakeCommandHandler>());

        var result = await dispatcher.Send(new FakeCommand("x"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("handled:x", result.Value);
    }

    [Fact]
    public async Task Send_Query_ResolveEExecutaOHandlerRegistrado()
    {
        var dispatcher = BuildDispatcher(s => s.AddScoped<IQueryHandler<FakeQuery, string>, FakeQueryHandler>());

        var result = await dispatcher.Send(new FakeQuery("y"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("queried:y", result.Value);
    }

    [Fact]
    public async Task Send_SemHandlerRegistrado_Lanca()
    {
        var dispatcher = BuildDispatcher();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.Send(new FakeCommand("x"), CancellationToken.None));
    }

    [Fact]
    public async Task Send_MesmoTipoDeComandoDuasVezes_ReusaOWrapperCacheado()
    {
        var handler = new FakeCommandHandler();
        var dispatcher = BuildDispatcher(s => s.AddScoped<ICommandHandler<FakeCommand, string>>(_ => handler));

        await dispatcher.Send(new FakeCommand("a"), CancellationToken.None);
        await dispatcher.Send(new FakeCommand("b"), CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
    }
}
