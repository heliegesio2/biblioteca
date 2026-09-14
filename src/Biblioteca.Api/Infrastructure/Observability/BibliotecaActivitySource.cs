using System.Diagnostics;

namespace Biblioteca.Api.Infrastructure.Observability;

/// <summary>Spans manuais de docs/observability.md#traces, além dos automáticos de
/// ASP.NET Core/HttpClient/Npgsql: um por comando (no <c>Dispatcher</c>) e o de
/// <c>loan.decrement_availability</c>, o mais útil do sistema (`rows_affected` distingue
/// "sem exemplar" de "algo falhou" sem precisar de log).</summary>
public static class BibliotecaActivitySource
{
    public const string Name = "Biblioteca.Api";

    public static readonly ActivitySource Instance = new(Name);
}
