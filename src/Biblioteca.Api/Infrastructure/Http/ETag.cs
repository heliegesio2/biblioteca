namespace Biblioteca.Api.Infrastructure.Http;

/// <summary>
/// Converte entre o <c>xmin</c> (token de concorrência do PostgreSQL, só em Book — ver
/// docs/concurrency.md) e o cabeçalho HTTP <c>ETag</c>/<c>If-Match</c>/<c>If-None-Match</c>.
/// </summary>
public static class ETag
{
    public static string From(uint xmin) => $"\"{xmin}\"";

    public static bool TryParse(string? headerValue, out uint xmin)
    {
        xmin = 0;

        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        var trimmed = headerValue.Trim().Trim('"');
        return uint.TryParse(trimmed, out xmin);
    }
}
