using System.Diagnostics;
using Biblioteca.Api.Infrastructure.Observability;

namespace Biblioteca.Api.Infrastructure.Http;

/// <summary>
/// RFC 9457 nativo do ASP.NET Core: <c>traceId</c> e <c>correlationId</c> são acrescentados
/// a <b>toda</b> resposta de erro (exceção não tratada, 4xx automático do framework ou
/// <c>Results.Problem</c> explícito em <see cref="ResultExtensions"/>) num lugar só.
/// </summary>
public static class ProblemDetailsSetup
{
    public static IServiceCollection AddBibliotecaProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["traceId"] =
                    Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
                context.ProblemDetails.Extensions["correlationId"] =
                    context.HttpContext.GetCorrelationId();

                // 401/403 gerados pelo middleware de autenticação/autorização (fallback
                // policy, papel exigido) não passam por ResultExtensions — sem isto,
                // ficariam sem "code", quebrando o contrato de que todo erro tem um
                // (docs/api-contract.md#códigos-de-erro-de-negócio).
                if (!context.ProblemDetails.Extensions.ContainsKey("code"))
                {
                    context.ProblemDetails.Extensions["code"] = context.ProblemDetails.Status switch
                    {
                        StatusCodes.Status401Unauthorized => "unauthenticated",
                        StatusCodes.Status403Forbidden => "forbidden",
                        _ => "error",
                    };
                }
            };
        });
}
