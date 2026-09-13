using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Migrations nunca rodam aqui (ADR-0008): só o DbContext é registrado; aplicar o
// schema é responsabilidade do serviço `migrator` (docker-compose.yml, fase 5+).
builder.Services.AddDbContext<BibliotecaDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.Run();

// Necessário para que Biblioteca.IntegrationTests possa referenciar o tipo de entrada
// via WebApplicationFactory<Program> (top-level statements geram uma classe implícita
// e interna por padrão).
public partial class Program;
