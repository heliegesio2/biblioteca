using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Cqrs.Decorators;
using Microsoft.Extensions.Logging.Abstractions;

namespace Biblioteca.UnitTests.Infrastructure.Cqrs.Decorators;

public sealed class MetricsCommandDecoratorTests
{
    private static MetricsCommandDecorator<FakeCommand, string> CreateDecorator(ICommandHandler<FakeCommand, string> inner) =>
        new(inner, NullLogger<MetricsCommandDecorator<FakeCommand, string>>.Instance, TimeProvider.System);

    [Fact]
    public async Task Handle_Sucesso_RepassaOResultadoSemAlterar()
    {
        var inner = new FakeCommandHandler();
        var decorator = CreateDecorator(inner);

        var result = await decorator.Handle(new FakeCommand("x"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("handled:x", result.Value);
        Assert.Equal(1, inner.CallCount);
    }

    private sealed class FailingCommandHandler(Error error) : ICommandHandler<FakeCommand, string>
    {
        public Task<Result<string>> Handle(FakeCommand command, CancellationToken cancellationToken) =>
            Task.FromResult(Result<string>.Failure(error));
    }

    [Fact]
    public async Task Handle_Falha_RepassaOErroSemAlterar()
    {
        var error = new Error("book-unavailable", "titulo", "detalhe", 409);
        var decorator = CreateDecorator(new FailingCommandHandler(error));

        var result = await decorator.Handle(new FakeCommand("x"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }
}
