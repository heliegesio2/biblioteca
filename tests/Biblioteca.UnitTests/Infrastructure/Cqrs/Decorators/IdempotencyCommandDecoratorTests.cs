using Biblioteca.Api.Infrastructure.Cqrs.Decorators;
using Biblioteca.Api.Infrastructure.Idempotency;

namespace Biblioteca.UnitTests.Infrastructure.Cqrs.Decorators;

public sealed class IdempotencyCommandDecoratorTests
{
    [Fact]
    public async Task Handle_ComandoNaoIdempotente_RepassaDiretoSemTocarNoStore()
    {
        // FakeCommand não implementa IIdempotentCommand, então nem IdempotencyStore
        // (null aqui) é acessado — o caminho real (reserva/replay/reuso, contra
        // PostgreSQL de verdade) é coberto por IdempotencyTests (integração), já que
        // exige uma transação ambiente que este teste unitário não tem como fornecer.
        var inner = new FakeCommandHandler();
        var decorator = new IdempotencyCommandDecorator<FakeCommand, string>(
            inner, store: null!, TimeProvider.System, new IdempotencyReplayAccessor());

        var result = await decorator.Handle(new FakeCommand("x"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("handled:x", result.Value);
        Assert.Equal(1, inner.CallCount);
    }
}
