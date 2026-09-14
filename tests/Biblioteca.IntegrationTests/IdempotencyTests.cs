using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Loans.Contracts;
using Biblioteca.Api.Infrastructure.Persistence;
using Biblioteca.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Os cinco cenários de docs/idempotency.md#como-isso-é-testado. Critério de pronto da
/// fase 4, junto com LastCopyConcurrencyTests e LoanHistoryTests.
/// </summary>
public sealed class IdempotencyTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task SameKey_SameBody_Sequential()
    {
        var bookId = await CreateBookAsync(totalCopies: 3);
        var userId = await CreateUserAsync();
        var key = Guid.NewGuid().ToString();

        var first = await PostLoanAsync(bookId, userId, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstLoan = await first.Content.ReadFromJsonAsync<LoanResponse>();
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));

        var second = await PostLoanAsync(bookId, userId, key);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondLoan = await second.Content.ReadFromJsonAsync<LoanResponse>();
        Assert.Equal(firstLoan!.Id, secondLoan!.Id);
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());

        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BibliotecaDbContext>();

        Assert.Equal(1, await dbContext.Loans.CountAsync(l => l.BookId == bookId));
        var book = await dbContext.Books.AsNoTracking().SingleAsync(b => b.Id == bookId);
        Assert.Equal(2, book.AvailableCopies); // decrementado uma vez só (3 -> 2)
    }

    [Fact(Timeout = 30000)]
    public async Task SameKey_Concurrent_TenRequests()
    {
        var bookId = await CreateBookAsync(totalCopies: 10); // exemplares de sobra: o
                                                              // que está sob teste é a
                                                              // idempotência, não a
                                                              // disponibilidade
        var userId = await CreateUserAsync();
        var key = Guid.NewGuid().ToString();

        using var barrier = new Barrier(10);
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await PostLoanAsync(bookId, userId, key);
        })).ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        var loanIds = new HashSet<Guid>();
        foreach (var response in responses)
        {
            var loan = await response.Content.ReadFromJsonAsync<LoanResponse>();
            loanIds.Add(loan!.Id);
        }

        Assert.Single(loanIds); // todas as respostas apontam para o mesmo empréstimo

        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BibliotecaDbContext>();

        Assert.Equal(1, await dbContext.Loans.CountAsync(l => l.BookId == bookId));
        var book = await dbContext.Books.AsNoTracking().SingleAsync(b => b.Id == bookId);
        Assert.Equal(9, book.AvailableCopies); // decrementado uma vez só
    }

    [Fact]
    public async Task SameKey_DifferentBody_Retorna409()
    {
        var bookId1 = await CreateBookAsync(isbn: "9780306406157", title: "Livro 1");
        var bookId2 = await CreateBookAsync(isbn: "0306406152", title: "Livro 2");
        var userId = await CreateUserAsync();
        var key = Guid.NewGuid().ToString();

        var first = await PostLoanAsync(bookId1, userId, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostLoanAsync(bookId2, userId, key); // mesma chave, corpo diferente

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("idempotency-key-reuse", problem!.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task MissingKey_Retorna400()
    {
        var bookId = await CreateBookAsync();
        var userId = await CreateUserAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/loans")
        {
            Content = JsonContent.Create(new { bookId, userId }),
        };
        // sem Idempotency-Key

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("idempotency-key-missing", problem!.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task KeyRolledBack_AfterBusinessRejection_PermiteRepetirComSucesso()
    {
        var bookId = await CreateBookAsync(totalCopies: 1);
        var userA = await CreateUserAsync();
        var userB = await CreateUserAsync();
        var key = Guid.NewGuid().ToString();

        // esgota o único exemplar com outro usuário, sem usar a chave em teste
        await PostLoanAsync(bookId, userA);

        var rejected = await PostLoanAsync(bookId, userB, key);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("book-unavailable", problem!.Extensions["code"]!.ToString());

        // devolve o exemplar; repetir com a MESMA chave agora deve suceder — a rejeição
        // de negócio não "queimou" a chave (docs/idempotency.md#interação-com-rejeições-de-negócio)
        using (var scope = Factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BibliotecaDbContext>();
            var loanA = await dbContext.Loans.SingleAsync(l => l.BookId == bookId && l.UserId == userA);
            var returnResponse = await Client.PostAsync($"/loans/{loanA.Id}/return", content: null);
            Assert.Equal(HttpStatusCode.OK, returnResponse.StatusCode);
        }

        var retried = await PostLoanAsync(bookId, userB, key);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
    }
}
