namespace Biblioteca.Api.Infrastructure.Auth;

/// <summary>Seção <c>Auth</c> de configuração (README §6, docs/security.md). Nunca versionar
/// <see cref="SigningKey"/> real — só a chave local de desenvolvimento.</summary>
public sealed class AuthOptions
{
    public string Issuer { get; set; } = "biblioteca";
    public string Audience { get; set; } = "biblioteca";
    public string SigningKey { get; set; } = string.Empty;
}
