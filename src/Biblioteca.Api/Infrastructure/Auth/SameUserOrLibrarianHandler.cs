using Microsoft.AspNetCore.Authorization;

namespace Biblioteca.Api.Infrastructure.Auth;

/// <summary>
/// A regra transversal de docs/security.md#autorização: um <c>member</c> só enxerga e
/// movimenta os próprios recursos; <c>librarian</c> acessa qualquer um. Depende do
/// <c>userId</c> do recurso (não é expressável por política de papel), por isso é
/// avaliada como autorização baseada em recurso — chamada explicitamente pelo endpoint
/// (<see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/>), nunca como
/// `if` espalhado dentro dos handlers.
/// </summary>
public sealed class SameUserOrLibrarianRequirement : IAuthorizationRequirement;

public sealed class SameUserOrLibrarianHandler : AuthorizationHandler<SameUserOrLibrarianRequirement, Guid>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, SameUserOrLibrarianRequirement requirement, Guid resourceUserId)
    {
        if (context.User.IsInRole("librarian"))
        {
            context.Succeed(requirement);
        }
        else if (Guid.TryParse(context.User.FindFirst("sub")?.Value, out var currentUserId) && currentUserId == resourceUserId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
