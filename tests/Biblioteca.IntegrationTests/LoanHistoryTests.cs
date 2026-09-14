using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Loans.Contracts;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.IntegrationTests.Infrastructure;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Critério de pronto da fase 4: nada some do histórico depois de devolução/cancelamento
/// (docs/auditing.md#preservação-do-histórico, docs/domain-model.md#máquina-de-estados).
/// </summary>
public sealed class LoanHistoryTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task HistoricoEPreservado_AposDevolucaoECancelamento()
    {
        var bookId = await CreateBookAsync(totalCopies: 2);
        var userA = await CreateUserAsync();
        var userB = await CreateUserAsync();

        var loanA = await (await PostLoanAsync(bookId, userA)).Content.ReadFromJsonAsync<LoanResponse>();
        var loanB = await (await PostLoanAsync(bookId, userB)).Content.ReadFromJsonAsync<LoanResponse>();

        var returnResponse = await Client.PostAsync($"/loans/{loanA!.Id}/return", content: null);
        Assert.Equal(HttpStatusCode.OK, returnResponse.StatusCode);

        var cancelResponse = await Client.PostAsJsonAsync(
            $"/loans/{loanB!.Id}/cancel", new CancelLoanRequest("erro de registro no balcão"));
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        var history = await (await Client.GetAsync($"/books/{bookId}/history"))
            .Content.ReadFromJsonAsync<HistoryPage>();

        Assert.Equal(2, history!.Items.Count); // nada sumiu

        var returnedInHistory = history.Items.Single(l => l.Id == loanA.Id);
        Assert.Equal("Returned", returnedInHistory.Status);
        Assert.NotEqual(default, returnedInHistory.BorrowedAt);

        var cancelledInHistory = history.Items.Single(l => l.Id == loanB.Id);
        Assert.Equal("Cancelled", cancelledInHistory.Status);

        var availability = await (await Client.GetAsync($"/books/{bookId}/availability"))
            .Content.ReadFromJsonAsync<AvailabilityResponse>();
        Assert.Equal(2, availability!.AvailableCopies); // os dois exemplares voltaram

        var loansUserA = await (await Client.GetAsync($"/users/{userA}/loans"))
            .Content.ReadFromJsonAsync<UserHistoryPage>();
        Assert.Contains(loansUserA!.Items, l => l.Id == loanA.Id && l.Status == "Returned");
    }

    // Ver nota em CatalogTests sobre por que um DTO concreto local em vez de PagedResponse<T>.
    private sealed record HistoryPage(List<BookHistoryItemResponse> Items, string? NextCursor);

    private sealed record UserHistoryItem(Guid Id, Guid BookId, string Status);

    private sealed record UserHistoryPage(List<UserHistoryItem> Items, string? NextCursor);
}
