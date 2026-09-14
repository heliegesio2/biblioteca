using System.Net.Http.Json;
using System.Text.Json;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Loans.Contracts;
using Biblioteca.IntegrationTests.Infrastructure;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Critério de pronto da fase 6: a trilha de qualquer livro reconstrói a história do seu
/// estoque (docs/implementation-plan.md#fase-6--auditoria-e-consulta). Um evento por
/// mudança, actor/correlationId/UTC corretos, from/to do BookUpdated, filtros de consulta.
/// </summary>
public sealed class AuditTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    private async Task<HttpResponseMessage> SendWithCorrelationIdAsync(HttpRequestMessage request, string correlationId)
    {
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        return await Client.SendAsync(request);
    }

    [Fact]
    public async Task CreateBook_GeraEventoBookCreated_ComCorrelationIdEUtc()
    {
        var correlationId = Guid.NewGuid().ToString();
        var beforeCall = DateTimeOffset.UtcNow;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/books")
        {
            Content = JsonContent.Create(new CreateBookRequest("9780306406157", "Dom Casmurro", "Machado de Assis", 3)),
        };
        var response = await SendWithCorrelationIdAsync(request, correlationId);
        var book = await response.Content.ReadFromJsonAsync<BookResponse>();

        var page = await GetAuditEventsAsync(entityType: "Book", entityId: book!.Id);

        var created = Assert.Single(page.Items);
        Assert.Equal("BookCreated", created.Action);
        Assert.Equal("Book", created.EntityType);
        Assert.Equal(book.Id, created.EntityId);
        Assert.Equal(correlationId, created.CorrelationId);
        Assert.Equal(DateTimeKind.Utc, created.OccurredAt.UtcDateTime.Kind);
        Assert.True(created.OccurredAt >= beforeCall);
        Assert.Equal(DefaultActor, created.Actor); // e-mail do JWT, não mais "system" (fase 7)
    }

    [Fact]
    public async Task UpdateBook_GeraEventoComFromToApenasDosCamposAlterados()
    {
        var created = await (await Client.PostAsJsonAsync("/books",
                new CreateBookRequest("9780306406157", "Dom Casmurro", "Machado de Assis", 3)))
            .Content.ReadFromJsonAsync<BookResponse>();

        var getResponse = await Client.GetAsync($"/books/{created!.Id}");
        var etag = getResponse.Headers.ETag!.Tag;

        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/books/{created.Id}")
        {
            Content = JsonContent.Create(new UpdateBookRequest("Novo título", null, 5)),
        };
        patchRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        await Client.SendAsync(patchRequest);

        var page = await GetAuditEventsAsync(entityType: "Book", entityId: created.Id, action: "BookUpdated");

        var updated = Assert.Single(page.Items);
        var payload = updated.Payload;

        Assert.Equal("Dom Casmurro", payload.GetProperty("title").GetProperty("from").GetString());
        Assert.Equal("Novo título", payload.GetProperty("title").GetProperty("to").GetString());
        Assert.Equal(3, payload.GetProperty("totalCopies").GetProperty("from").GetInt32());
        Assert.Equal(5, payload.GetProperty("totalCopies").GetProperty("to").GetInt32());
        Assert.False(payload.TryGetProperty("author", out _)); // autor não mudou, não entra no payload
    }

    [Fact]
    public async Task DeactivateBook_GeraEventoBookDeactivated()
    {
        var bookId = await CreateBookAsync();

        await Client.DeleteAsync($"/books/{bookId}");

        var page = await GetAuditEventsAsync(entityType: "Book", entityId: bookId, action: "BookDeactivated");
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task CicloDeEmprestimo_GeraOsTresEventosNaOrdem()
    {
        var bookId = await CreateBookAsync(totalCopies: 1);
        var userId = await CreateUserAsync();

        var loan = await (await PostLoanAsync(bookId, userId)).Content.ReadFromJsonAsync<LoanResponse>();
        await Client.PostAsync($"/loans/{loan!.Id}/return", content: null);

        var page = await GetAuditEventsAsync(entityType: "Loan", entityId: loan.Id);

        // ordem decrescente por id (mais recente primeiro)
        Assert.Equal(["LoanReturned", "LoanCreated"], page.Items.Select(e => e.Action).ToArray());
    }

    [Fact]
    public async Task FiltroPorActor_RetornaSoEventosDaquelaIdentidade()
    {
        // O actor real (fase 7) é o e-mail do JWT de quem chamou — DefaultActor é o do
        // Client (librarian) da fixture.
        var bookId = await CreateBookAsync();

        var matching = await GetAuditEventsAsync(actor: DefaultActor);
        var nonMatching = await GetAuditEventsAsync(actor: "outra-pessoa@example.com");

        Assert.Contains(matching.Items, e => e.EntityId == bookId);
        Assert.DoesNotContain(nonMatching.Items, e => e.EntityId == bookId);
    }

    [Fact]
    public async Task FiltroPorIntervaloDeTempo_ExcluiEventosForaDaJanela()
    {
        var bookId = await CreateBookAsync();

        var future = DateTimeOffset.UtcNow.AddMinutes(5);
        var page = await GetAuditEventsAsync(entityId: bookId, from: future);

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Paginacao_ComCursor_NaoRepeteNemPulaEventos()
    {
        var bookId = await CreateBookAsync(totalCopies: 10);
        for (var i = 0; i < 3; i++)
        {
            var getResponse = await Client.GetAsync($"/books/{bookId}");
            var etag = getResponse.Headers.ETag!.Tag;

            using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/books/{bookId}")
            {
                Content = JsonContent.Create(new UpdateBookRequest($"Título {i}", null, null)),
            };
            patchRequest.Headers.TryAddWithoutValidation("If-Match", etag);
            await Client.SendAsync(patchRequest);
        }

        // 1 BookCreated + 3 BookUpdated = 4 eventos
        var firstPage = await GetAuditEventsAsync(entityId: bookId, limit: 2);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await GetAuditEventsAsync(entityId: bookId, limit: 2, cursor: firstPage.NextCursor);
        Assert.Equal(2, secondPage.Items.Count);

        var allIds = firstPage.Items.Concat(secondPage.Items).Select(e => e.Id).ToList();
        Assert.Equal(allIds.Distinct().Count(), allIds.Count); // sem repetição
    }

    private async Task<AuditEventPage> GetAuditEventsAsync(
        string? entityType = null, Guid? entityId = null, string? action = null, string? actor = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, int? limit = null, string? cursor = null)
    {
        var query = new List<string>();
        if (entityType is not null) query.Add($"entityType={entityType}");
        if (entityId is not null) query.Add($"entityId={entityId}");
        if (action is not null) query.Add($"action={action}");
        if (actor is not null) query.Add($"actor={Uri.EscapeDataString(actor)}");
        if (from is not null) query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to is not null) query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        if (limit is not null) query.Add($"limit={limit}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");

        var url = "/audit-events" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        var response = await Client.GetAsync(url);
        var page = await response.Content.ReadFromJsonAsync<AuditEventPage>();
        return page!;
    }

    private sealed record AuditEventItem(
        long Id, string EntityType, Guid EntityId, string Action, string Actor,
        DateTimeOffset OccurredAt, string CorrelationId, JsonElement Payload);

    private sealed record AuditEventPage(List<AuditEventItem> Items, string? NextCursor);
}
