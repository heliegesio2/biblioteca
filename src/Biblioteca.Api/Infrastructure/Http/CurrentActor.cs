namespace Biblioteca.Api.Infrastructure.Http;

/// <summary>
/// JWT → ator da auditoria (docs/security.md#autenticação, docs/auditing.md). O claim
/// <c>email</c> do token é o que entra em <c>audit_events.actor</c>. Sem usuário
/// autenticado (rotinas internas), cai para <c>"system"</c>.
/// </summary>
public interface ICurrentActor
{
    string Value { get; }
}

internal sealed class CurrentActor(IHttpContextAccessor httpContextAccessor) : ICurrentActor
{
    public string Value => httpContextAccessor.HttpContext?.User.FindFirst("email")?.Value ?? "system";
}
