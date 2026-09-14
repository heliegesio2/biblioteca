using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Loans.Contracts;
using Biblioteca.Api.Infrastructure.Persistence;
using Biblioteca.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Critério de pronto da fase 5 (docs/implementation-plan.md#fase-5--cache): empréstimo
/// e devolução refletem imediatamente em GET /availability, e a API continua respondendo
/// com o Redis fora do ar. Redis real via Testcontainers (ADR-0006).
/// </summary>
public sealed class CacheTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Availability_SecondRead_HitsCache()
    {
        var bookId = await CreateBookAsync(totalCopies: 3);

        var first = await Client.GetAsync($"/books/{bookId}/availability");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Apaga a linha direto no banco, sem passar pela API — só a API invalida o
        // cache. Se a segunda leitura ainda responder 200, o valor veio do cache, não
        // do banco (que já não tem mais a linha).
        using (var scope = Factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BibliotecaDbContext>();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM books WHERE id = {bookId}");
        }

        var second = await Client.GetAsync($"/books/{bookId}/availability");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var availability = await second.Content.ReadFromJsonAsync<AvailabilityResponse>();
        Assert.Equal(3, availability!.AvailableCopies);
    }

    [Fact]
    public async Task Loan_InvalidatesAvailability()
    {
        var bookId = await CreateBookAsync(totalCopies: 1);
        var userId = await CreateUserAsync();

        var before = await (await Client.GetAsync($"/books/{bookId}/availability"))
            .Content.ReadFromJsonAsync<AvailabilityResponse>();
        Assert.Equal(1, before!.AvailableCopies);

        var loanResponse = await PostLoanAsync(bookId, userId);
        Assert.Equal(HttpStatusCode.Created, loanResponse.StatusCode);

        var after = await (await Client.GetAsync($"/books/{bookId}/availability"))
            .Content.ReadFromJsonAsync<AvailabilityResponse>();
        Assert.Equal(0, after!.AvailableCopies); // reflete na hora, sem esperar o TTL
    }

    [Fact]
    public async Task Return_InvalidatesAvailability()
    {
        var bookId = await CreateBookAsync(totalCopies: 1);
        var userId = await CreateUserAsync();
        var loan = await (await PostLoanAsync(bookId, userId)).Content.ReadFromJsonAsync<LoanResponse>();

        await Client.GetAsync($"/books/{bookId}/availability"); // popula o cache com 0

        await Client.PostAsync($"/loans/{loan!.Id}/return", content: null);

        var after = await (await Client.GetAsync($"/books/{bookId}/availability"))
            .Content.ReadFromJsonAsync<AvailabilityResponse>();
        Assert.Equal(1, after!.AvailableCopies);
    }

    [Fact]
    public async Task PatchBook_InvalidatesBook()
    {
        var bookId = await CreateBookAsync();

        var first = await Client.GetAsync($"/books/{bookId}");
        var etag = first.Headers.ETag!.Tag;
        await first.Content.ReadFromJsonAsync<BookResponse>(); // popula o cache

        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/books/{bookId}")
        {
            Content = JsonContent.Create(new UpdateBookRequest("Novo título", null, null)),
        };
        patchRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        await Client.SendAsync(patchRequest);

        var second = await Client.GetAsync($"/books/{bookId}");
        var updated = await second.Content.ReadFromJsonAsync<BookResponse>();

        Assert.Equal("Novo título", updated!.Title);
        Assert.NotEqual(etag, second.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task RedisDown_ReadsStillWork()
    {
        var bookId = await CreateBookAsync();

        await Factory.StopRedisAsync();
        try
        {
            var response = await Client.GetAsync($"/books/{bookId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var book = await response.Content.ReadFromJsonAsync<BookResponse>();
            Assert.Equal(bookId, book!.Id);
        }
        finally
        {
            await Factory.StartRedisAsync();
        }
    }

    [Fact]
    public async Task IfNoneMatch_Returns304_ComCacheQuente()
    {
        var bookId = await CreateBookAsync();

        var first = await Client.GetAsync($"/books/{bookId}");
        var etag = first.Headers.ETag!.Tag;
        await first.Content.ReadFromJsonAsync<BookResponse>(); // popula o cache

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/books/{bookId}");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }
}
