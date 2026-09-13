using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Infrastructure.Cqrs.SelfTest;

/// <summary>
/// Comando interno, sem rota HTTP, que existe só para provar que o pipeline CQRS inteiro
/// funciona de ponta a ponta (fase 2, "pronto quando" de
/// docs/implementation-plan.md#fase-2--infraestrutura-cqrs): Metrics → Validation →
/// CacheInvalidation → Transaction → Idempotency → Handler, com um round-trip real ao
/// PostgreSQL dentro da transação. Não é uma feature de produto — exercitado só por
/// Biblioteca.IntegrationTests.
/// </summary>
public sealed record PingCommand(string Message) : ICommand<string>;

public sealed class PingCommandValidator : AbstractValidator<PingCommand>
{
    public PingCommandValidator()
    {
        RuleFor(c => c.Message).NotEmpty().MaximumLength(100);
    }
}

internal sealed class PingCommandHandler(BibliotecaDbContext dbContext) : ICommandHandler<PingCommand, string>
{
    public async Task<Result<string>> Handle(PingCommand command, CancellationToken cancellationToken)
    {
        // Round-trip real dentro da transação aberta pelo TransactionCommandDecorator —
        // prova que BEGIN/COMMIT acontece contra um PostgreSQL de verdade.
        await dbContext.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);

        return Result<string>.Success($"pong: {command.Message}");
    }
}
