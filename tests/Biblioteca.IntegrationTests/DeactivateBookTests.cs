using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Features.Users.Domain;
using Biblioteca.Api.Infrastructure.Persistence;
using Biblioteca.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Fase 3, critério de pronto: com empréstimo ativo -> 409; sem -> 204 e desativa; já
/// desativado -> 204 de novo (a operação é idempotente por natureza).
/// </summary>
public sealed class DeactivateBookTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    private async Task<Guid> CreateBookAsync()
    {
        var created = await (await Client.PostAsJsonAsync("/books",
                new CreateBookRequest("9780306406157", "Dom Casmurro", "Machado de Assis", 3)))
            .Content.ReadFromJsonAsync<BookResponse>();

        return created!.Id;
    }

    // Loans (fase 4) ainda não existe como comando; semeia um empréstimo ativo direto
    // via DbContext só para exercitar a regra de negócio de DeactivateBook.
    private async Task SeedActiveLoanAsync(Guid bookId)
    {
        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BibliotecaDbContext>();

        var now = DateTimeOffset.UtcNow;
        var user = User.Create("Ana Ribeiro", $"ana-{Guid.NewGuid()}@example.com", now);
        dbContext.Users.Add(user);

        var loan = Loan.Create(bookId, user.Id, now, now.AddDays(14));
        dbContext.Loans.Add(loan);

        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task Deactivate_SemEmprestimoAtivo_Retorna204()
    {
        var bookId = await CreateBookAsync();

        var response = await Client.DeleteAsync($"/books/{bookId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_ComEmprestimoAtivo_Retorna409ENaoDesativa()
    {
        var bookId = await CreateBookAsync();
        await SeedActiveLoanAsync(bookId);

        var response = await Client.DeleteAsync($"/books/{bookId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("book-has-active-loans", problem!.Extensions["code"]!.ToString());

        var book = await (await Client.GetAsync($"/books/{bookId}")).Content.ReadFromJsonAsync<BookResponse>();
        Assert.True(book!.IsActive);
    }

    [Fact]
    public async Task Deactivate_JaDesativado_Retorna204DeNovo()
    {
        var bookId = await CreateBookAsync();
        await Client.DeleteAsync($"/books/{bookId}");

        var response = await Client.DeleteAsync($"/books/{bookId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Deactivate_Inexistente_Retorna404()
    {
        var response = await Client.DeleteAsync($"/books/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
