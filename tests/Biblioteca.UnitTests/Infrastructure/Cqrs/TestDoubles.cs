using Biblioteca.Api.Infrastructure.Cqrs;

namespace Biblioteca.UnitTests.Infrastructure.Cqrs;

public sealed record FakeCommand(string Value) : ICommand<string>;

public sealed class FakeCommandHandler : ICommandHandler<FakeCommand, string>
{
    public int CallCount { get; private set; }

    public Task<Result<string>> Handle(FakeCommand command, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(Result<string>.Success($"handled:{command.Value}"));
    }
}

public sealed record FakeQuery(string Value) : IQuery<string>;

public sealed class FakeQueryHandler : IQueryHandler<FakeQuery, string>
{
    public Task<Result<string>> Handle(FakeQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<string>.Success($"queried:{query.Value}"));
}
