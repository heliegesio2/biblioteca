using Biblioteca.Api.Infrastructure.Cqrs.Decorators;

namespace Biblioteca.UnitTests.Infrastructure.Cqrs.Decorators;

public sealed class IdempotencyCommandDecoratorTests
{
    [Fact]
    public async Task Handle_ChamaOInnerDiretamente()
    {
        // Fase 2: nenhum comando implementa IIdempotentCommand ainda, então o decorator
        // só repassa. A lógica real (reserva/replay via IdempotencyStore) chega na fase 4
        // — este teste documenta o comportamento atual e deve ser revisto lá.
        var inner = new FakeCommandHandler();
        var decorator = new IdempotencyCommandDecorator<FakeCommand, string>(inner);

        var result = await decorator.Handle(new FakeCommand("x"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("handled:x", result.Value);
        Assert.Equal(1, inner.CallCount);
    }
}
