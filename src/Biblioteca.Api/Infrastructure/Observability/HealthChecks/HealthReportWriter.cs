using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Biblioteca.Api.Infrastructure.Observability.HealthChecks;

/// <summary>Formato de docs/observability.md#health-checks: status geral e um item por
/// checagem, com duração em milissegundos e descrição só quando houver.</summary>
internal static class HealthReportWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                duration = entry.Value.Duration.TotalMilliseconds,
                description = entry.Value.Description,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
