using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.IntegrationTests.Infrastructure;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Fase 3, critério de pronto: If-Match correto -> 200; desatualizado -> 412; ausente ->
/// 428 (docs/concurrency.md#onde-usamos-concorrência-otimista-patch-booksid).
/// </summary>
public sealed class UpdateBookConcurrencyTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    private async Task<(Guid Id, string ETag)> CreateBookAsync()
    {
        var created = await (await Client.PostAsJsonAsync("/books",
                new CreateBookRequest("9780306406157", "Dom Casmurro", "Machado de Assis", 3)))
            .Content.ReadFromJsonAsync<BookResponse>();

        var getResponse = await Client.GetAsync($"/books/{created!.Id}");
        return (created.Id, getResponse.Headers.ETag!.Tag);
    }

    private HttpRequestMessage PatchRequest(Guid id, string? ifMatch, UpdateBookRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/books/{id}") { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    [Fact]
    public async Task Patch_IfMatchCorreto_Retorna200ComNovoETag()
    {
        var (id, etag) = await CreateBookAsync();

        using var request = PatchRequest(id, etag, new UpdateBookRequest("Novo título", null, null));
        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Patch_IfMatchDesatualizado_Retorna412()
    {
        var (id, etag) = await CreateBookAsync();

        // primeira edição consome o ETag original
        using var firstRequest = PatchRequest(id, etag, new UpdateBookRequest("Primeira edição", null, null));
        await Client.SendAsync(firstRequest);

        // segunda tentativa, ainda com o ETag original (agora obsoleto)
        using var staleRequest = PatchRequest(id, etag, new UpdateBookRequest("Segunda edição", null, null));
        var response = await Client.SendAsync(staleRequest);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Patch_SemIfMatch_Retorna428()
    {
        var (id, _) = await CreateBookAsync();

        using var request = PatchRequest(id, ifMatch: null, new UpdateBookRequest("Título", null, null));
        var response = await Client.SendAsync(request);

        Assert.Equal((HttpStatusCode)428, response.StatusCode);
    }

    [Fact]
    public async Task Patch_DuasEdicoesConcorrentesComMesmoETag_UmaSucedeEAOutraRecusa()
    {
        var (id, etag) = await CreateBookAsync();

        var responses = await Task.WhenAll(
            Client.SendAsync(PatchRequest(id, etag, new UpdateBookRequest("Edição A", null, null))),
            Client.SendAsync(PatchRequest(id, etag, new UpdateBookRequest("Edição B", null, null))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
    }
}
