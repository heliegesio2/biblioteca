var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

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
