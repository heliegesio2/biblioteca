using System.Text;
using Biblioteca.Api.Features.Audit;
using Biblioteca.Api.Features.Auth;
using Biblioteca.Api.Features.Catalog;
using Biblioteca.Api.Features.Loans;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Features.Users;
using Biblioteca.Api.Infrastructure.Auth;
using Biblioteca.Api.Infrastructure.Caching;
using Biblioteca.Api.Infrastructure.Cqrs;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.Api.Infrastructure.Idempotency;
using Biblioteca.Api.Infrastructure.Observability;
using Biblioteca.Api.Infrastructure.Observability.HealthChecks;
using Biblioteca.Api.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

// Segurança (fase 7): JWT Bearer com papéis (docs/security.md). POST /auth/token é o
// substituto declarado de um identity provider — só emite; nada no domínio depende de
// como o token chegou, só do ClaimsPrincipal resultante.
const string DevelopmentSigningKey = "dev-only-signing-key-biblioteca-nao-versionar-em-producao-jamais";

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));

var authIssuer = builder.Configuration["Auth:Issuer"] ?? "biblioteca";
var authAudience = builder.Configuration["Auth:Audience"] ?? "biblioteca";
var authSigningKey = builder.Configuration["Auth:SigningKey"];

if (builder.Environment.IsProduction())
{
    // Falhar no start é melhor do que servir tráfego com token forjável.
    if (string.IsNullOrEmpty(authSigningKey) ||
        Encoding.UTF8.GetByteCount(authSigningKey) < 32 ||
        authSigningKey == DevelopmentSigningKey)
    {
        throw new InvalidOperationException(
            "Auth:SigningKey ausente, curta demais (< 32 bytes) ou igual à chave de " +
            "desenvolvimento. A aplicação recusa subir em Production com um token forjável.");
    }
}
else if (string.IsNullOrEmpty(authSigningKey))
{
    authSigningKey = DevelopmentSigningKey;
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Mantém "sub"/"email"/"role" como estão no token, sem remapear para as URIs
        // longas que o JwtSecurityTokenHandler usa por padrão.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authIssuer,
            ValidateAudience = true,
            ValidAudience = authAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authSigningKey)),
            RoleClaimType = "role",
            NameClaimType = "sub",
            ClockSkew = TimeSpan.Zero,
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Fallback seguro por padrão: todo endpoint sem AllowAnonymous() explícito exige
    // usuário autenticado, mesmo que ninguém tenha lembrado de marcar isso nele.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("Librarian", policy => policy.RequireRole("librarian"));
    options.AddPolicy("SameUserOrLibrarian", policy => policy.Requirements.Add(new SameUserOrLibrarianRequirement()));
});
builder.Services.AddSingleton<IAuthorizationHandler, SameUserOrLibrarianHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentActor, CurrentActor>();

// Observabilidade (fase 8, docs/observability.md). Logs JSON puro em stdout — sem
// arquivo, sem agente no container; o coletor do cluster lê stdout em Kubernetes.
builder.Logging.AddJsonConsole();

builder.Services.AddSingleton<LoanMetrics>();

// OTEL_EXPORTER_OTLP_ENDPOINT vazio (o default local) desliga a exportação OTLP sem
// exigir coletor/Jaeger/conta de vendor para rodar o projeto — traces e métricas
// continuam sendo produzidos, só não saem do processo.
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Biblioteca.Api"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(BibliotecaActivitySource.Name);
        Npgsql.TracerProviderBuilderExtensions.AddNpgsql(tracing);
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter(LoanMetrics.MeterName)
            .AddMeter("Npgsql");
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            metrics.AddOtlpExporter();
        }
    });

// /health/live não consulta nada externo (nenhum check registrado é executado ali) —
// senão uma indisponibilidade do Postgres faria o kubelet reiniciar todos os pods em
// loop. /health/ready reprova só por "critical"; Redis "degraded" nunca reprova.
builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["critical"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["degraded"]);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
// Sem isto, um 401/403 que o middleware de autenticação/autorização só define via
// StatusCode (sem corpo) nunca passaria pelo IProblemDetailsService — o cliente veria
// status certo, corpo vazio, quebrando o contrato "todo erro é Problem Details"
// (docs/api-contract.md#erros--rfc-9457-problem-details).
app.UseStatusCodePages();
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapCatalogEndpoints();
app.MapUserEndpoints();
app.MapLoanEndpoints();
app.MapAuditEndpoints();

// Anônimos (um probe não carrega JWT). "live" não executa nenhum check registrado —
// só confirma que o pipeline responde.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResponseWriter = HealthReportWriter.WriteAsync })
    .AllowAnonymous();

// POST /auth/token não existe em Production — não há identity provider de mentira em
// tráfego real (docs/security.md#post-authtoken).
if (!app.Environment.IsProduction())
{
    app.MapAuthEndpoints();
}

app.Run();

// Necessário para que Biblioteca.IntegrationTests possa referenciar o tipo de entrada
// via WebApplicationFactory<Program> (top-level statements geram uma classe implícita
// e interna por padrão).
public partial class Program;
