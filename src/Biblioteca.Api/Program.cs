using Biblioteca.Api.Features.Audit;
using Biblioteca.Api.Features.Catalog;
using Biblioteca.Api.Features.Loans;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Features.Users;
using Biblioteca.Api.Infrastructure.Caching;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Idempotency;
using Biblioteca.Api.Infrastructure.Observability;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

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

// Empréstimos (fase 4) — parâmetros de negócio vêm de configuração, nunca hard-coded.
builder.Services.AddSingleton(new LoanPolicy(
    builder.Configuration.GetValue("Loans:LoanPeriodDays", 14),
    builder.Configuration.GetValue("Loans:MaxActiveLoansPerUser", 5)));

// Idempotência de POST /loans (ADR-0005): chave e efeito na mesma transação.
builder.Services.Configure<IdempotencyOptions>(builder.Configuration.GetSection("Idempotency"));
builder.Services.AddScoped<IdempotencyStore>();
builder.Services.AddScoped<IIdempotencyReplayAccessor, IdempotencyReplayAccessor>();
builder.Services.AddHostedService<IdempotencyCleanupService>();

// Cache (fase 5): Redis como L2, memória do processo como L1 — o cache nunca decide
// nada, só serve leitura (docs/caching.md). TTL/expiração local por chamada específica
// (GetAvailability) sobrescrevem o default abaixo, que vale para o livro (GetBookById).
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));
builder.Services.AddStackExchangeRedisCache(options =>
{
    // Timeouts curtos de propósito: "degrada, não derruba" só é verdade se a
    // degradação for rápida. Sem isso, um Redis lento/instável (não só "fora do ar")
    // deixaria toda leitura cacheada esperando o timeout padrão do cliente (vários
    // segundos) antes de cair para o banco — trocando o problema de disponibilidade
    // por um de latência, exatamente o que docs/caching.md#redis-fora-do-ar quer evitar.
    options.ConfigurationOptions = new StackExchange.Redis.ConfigurationOptions
    {
        EndPoints = { builder.Configuration.GetConnectionString("Redis")! },
        ConnectTimeout = 1000,
        SyncTimeout = 1000,
        AsyncTimeout = 1000,
        ConnectRetry = 1,
        AbortOnConnectFail = false,
    };
});
builder.Services.AddHybridCache(options =>
{
    var bookTtlSeconds = builder.Configuration.GetValue("Cache:BookTtlSeconds", 300);
    options.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromSeconds(bookTtlSeconds),
        LocalCacheExpiration = TimeSpan.FromSeconds(5),
    };
});

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
app.MapUserEndpoints();
app.MapLoanEndpoints();
app.MapAuditEndpoints();

app.Run();

// Necessário para que Biblioteca.IntegrationTests possa referenciar o tipo de entrada
// via WebApplicationFactory<Program> (top-level statements geram uma classe implícita
// e interna por padrão).
public partial class Program;
