using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Cqrs.Decorators;
using FluentValidation;

namespace Biblioteca.UnitTests.Infrastructure.Cqrs.Decorators;

public sealed class ValidationCommandDecoratorTests
{
    private sealed class FakeCommandValidator : AbstractValidator<FakeCommand>
    {
        public FakeCommandValidator() => RuleFor(c => c.Value).NotEmpty();
    }

    [Fact]
    public async Task Handle_SemValidadorRegistrado_ChamaOInner()
    {
        var inner = new FakeCommandHandler();
        var decorator = new ValidationCommandDecorator<FakeCommand, string>(inner, []);

        var result = await decorator.Handle(new FakeCommand("x"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Handle_ComandoValido_ChamaOInner()
    {
        var inner = new FakeCommandHandler();
        var decorator = new ValidationCommandDecorator<FakeCommand, string>(inner, [new FakeCommandValidator()]);

        var result = await decorator.Handle(new FakeCommand("x"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Handle_ComandoInvalido_RetornaValidationFailedSemChamarOInner()
    {
        var inner = new FakeCommandHandler();
        var decorator = new ValidationCommandDecorator<FakeCommand, string>(inner, [new FakeCommandValidator()]);

        var result = await decorator.Handle(new FakeCommand(""), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("validation-failed", result.Error!.Code);
        Assert.Contains("Value", result.Error.ValidationErrors!.Keys);
        Assert.Equal(0, inner.CallCount);
    }
}
