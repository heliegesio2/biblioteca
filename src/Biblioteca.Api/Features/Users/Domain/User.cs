namespace Biblioteca.Api.Features.Users.Domain;

/// <summary>
/// Leitor da biblioteca. Não é apagado: o histórico de empréstimos depende dele.
/// A validação completa de formato de e-mail vive no validador do comando
/// (fase 2/3); aqui só a guarda mínima contra estado obviamente inválido.
/// </summary>
public sealed class User
{
    private User()
    {
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static User Create(string name, string email, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new ArgumentException("O nome deve ter entre 1 e 200 caracteres.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(email) || email.Length > 320 || !email.Contains('@'))
        {
            throw new ArgumentException("E-mail inválido.", nameof(email));
        }

        return new User
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Email = email,
            IsActive = true,
            CreatedAt = now,
        };
    }
}
