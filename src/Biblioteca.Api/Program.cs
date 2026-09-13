using Biblioteca.Api.Features.Audit;
using Biblioteca.Api.Features.Catalog;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Observability;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var apiAssembly = typeof(Program).Assembly;

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Migrations nunca rodam aqui (ADR-0008): só o DbContext é registrado; aplicar o
// schema é responsabilidade do serviço `migrator` (docker-compose.yml, fase 5+).
builder.Services.AddDbContext<BibliotecaDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Pipeline CQRS (docs/architecture.md#o-pipeline-cqrs) e Problem Details (RFC 9457).
builder.Services.AddCqrs(apiAssembly);
builder.Services.AddValidatorsFromAssembly(apiAssembly);
builder.Services.AddBibliotecaProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICorrelationIdAccessor, CorrelationIdAccessor>();
builder.Services.AddScoped<IAuditWriter, AuditWriter>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseHttpsRedirection();

app.MapCatalogEndpoints();

app.Run();

// Necessário para que Biblioteca.IntegrationTests possa referenciar o tipo de entrada
// via WebApplicationFactory<Program> (top-level statements geram uma classe implícita
// e interna por padrão).
public partial class Program;
