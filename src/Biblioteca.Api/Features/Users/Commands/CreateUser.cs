using Biblioteca.Api.Features.Users.Contracts;
using Biblioteca.Api.Features.Users.Domain;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Features.Users.Commands;

public sealed record CreateUserCommand(string Name, string Email) : ICommand<UserResponse>;

public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

internal sealed class CreateUserHandler(BibliotecaDbContext dbContext, TimeProvider timeProvider)
    : ICommandHandler<CreateUserCommand, UserResponse>
{
    public async Task<Result<UserResponse>> Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        // Mensagem específica no caminho comum; citext + ux_users_email cobre a corrida
        // rara (mesmo padrão de CreateBook para o ISBN).
        var emailTaken = await dbContext.Users.AnyAsync(u => u.Email == command.Email, cancellationToken);
        if (emailTaken)
        {
            return new Error("email-already-exists", "E-mail já cadastrado",
                $"Já existe um usuário cadastrado com o e-mail '{command.Email}'.", StatusCodes.Status409Conflict);
        }

        var now = timeProvider.GetUtcNow();
        var user = User.Create(command.Name, command.Email, now);
        dbContext.Users.Add(user);

        // Sem evento de auditoria: o vocabulário fechado de docs/auditing.md cobre
        // apenas mudanças de Book e Loan.

        return Result<UserResponse>.Success(ToResponse(user));
    }

    internal static UserResponse ToResponse(User user) => new(user.Id, user.Name, user.Email, user.IsActive, user.CreatedAt);
}
