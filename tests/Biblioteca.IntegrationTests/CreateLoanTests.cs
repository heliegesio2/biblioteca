using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Loans.Contracts;
using Biblioteca.Api.Infrastructure.Persistence;
using Biblioteca.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Biblioteca.IntegrationTests;

public sealed class CreateLoanTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task CaminhoFeliz_Retorna201ComDueAtCorreto()
    {
        var bookId = await CreateBookAsync(totalCopies: 3);
        var userId = await CreateUserAsync();

        var response = await PostLoanAsync(bookId, userId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var loan = await response.Content.ReadFromJsonAsync<LoanResponse>();
        Assert.Equal("Active", loan!.Status);
        Assert.Equal($"/loans/{loan.Id}", response.Headers.Location!.OriginalString);
        // Loans:LoanPeriodDays default = 14 (appsettings.json)
        Assert.Equal(14, (loan.DueAt - loan.BorrowedAt).Days);
    }

    [Fact]
    public async Task LivroInexistente_Retorna404()
    {
        var userId = await CreateUserAsync();

        var response = await PostLoanAsync(Guid.NewGuid(), userId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LivroInativo_Retorna422()
    {
        var bookId = await CreateBookAsync();
        var userId = await CreateUserAsync();
        await Client.DeleteAsync($"/books/{bookId}");

        var response = await PostLoanAsync(bookId, userId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("book-inactive", problem!.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task LimiteDeEmprestimosAtingido_Retorna422()
    {
        var userId = await CreateUserAsync();

        // default Loans:MaxActiveLoansPerUser = 5 (appsettings.json)
        for (var i = 0; i < 5; i++)
        {
            var bookId = await CreateBookAsync(isbn: i switch
            {
                0 => "9780306406157",
                1 => "0306406152",
                2 => "020163371X",
                3 => "9780134685991",
                _ => "9780132350884",
            }, title: $"Livro {i}");
            var created = await PostLoanAsync(bookId, userId);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var sixthBookId = await CreateBookAsync(isbn: "9781491950357", title: "Sexto livro");
        var response = await PostLoanAsync(sixthBookId, userId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("user-loan-limit-reached", problem!.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task EmprestimoDuplicadoDoMesmoLivro_Retorna409()
    {
        var bookId = await CreateBookAsync(totalCopies: 5);
        var userId = await CreateUserAsync();
        await PostLoanAsync(bookId, userId);

        var response = await PostLoanAsync(bookId, userId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("duplicate-active-loan", problem!.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task ExemplarDecrementaERegistraAuditoria()
    {
        var bookId = await CreateBookAsync(totalCopies: 2);
        var userId = await CreateUserAsync();

        var response = await PostLoanAsync(bookId, userId);
        var loan = await response.Content.ReadFromJsonAsync<LoanResponse>();

        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BibliotecaDbContext>();

        var book = await dbContext.Books.AsNoTracking().SingleAsync(b => b.Id == bookId);
        Assert.Equal(1, book.AvailableCopies);

        var auditEvent = await dbContext.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.EntityType == "Loan" && e.EntityId == loan!.Id && e.Action == "LoanCreated");
        Assert.Contains("availableCopiesAfter", auditEvent.Payload);
    }
}
