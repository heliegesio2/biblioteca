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
            };
        });
}
