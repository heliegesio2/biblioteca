using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Infrastructure.Http;
using Biblioteca.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Fase 3, critério de pronto: CRUD, ISBN duplicado, busca e paginação — contra
/// PostgreSQL real (Testcontainers, ADR-0006).
/// </summary>
public sealed class CatalogTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    private static CreateBookRequest SampleBook(string? isbn = null, string? title = null) => new(
        isbn ?? "9780306406157",
        title ?? "Dom Casmurro",
        "Machado de Assis",
        3);

    [Fact]
    public async Task CreateBook_CorpoValido_Retorna201ComLocationEBody()
    {
        var response = await Client.PostAsJsonAsync("/books", SampleBook());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var book = await response.Content.ReadFromJsonAsync<BookResponse>();
        Assert.NotNull(book);
        Assert.Equal("9780306406157", book!.Isbn);
        Assert.Equal(3, book.TotalCopies);
        Assert.Equal(3, book.AvailableCopies);
        Assert.True(book.IsActive);
        Assert.Equal($"/books/{book.Id}", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task CreateBook_IsbnComHifens_NormalizaParaOMesmoValorDoSemHifens()
    {
        var response = await Client.PostAsJsonAsync("/books", SampleBook(isbn: "978-0-306-40615-7"));
        var book = await response.Content.ReadFromJsonAsync<BookResponse>();

        Assert.Equal("9780306406157", book!.Isbn);
    }

    [Fact]
    public async Task CreateBook_IsbnDuplicado_Retorna409()
    {
        await Client.PostAsJsonAsync("/books", SampleBook());
        var response = await Client.PostAsJsonAsync("/books", SampleBook(title: "Outro título"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("isbn-already-exists", problem!.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task CreateBook_IsbnInvalido_Retorna400ValidationFailed()
    {
        // dígito verificador errado (9780306406157 é o válido) — não confundir com um
        // ISBN "estruturalmente" inválido: 13 dígitos quaisquer podem, por acidente,
        // somar certo (ex.: só zeros).
        var response = await Client.PostAsJsonAsync("/books", SampleBook(isbn: "9780306406158"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains("Isbn", problem!.Errors.Keys);
    }

    [Fact]
    public async Task GetBookById_Existente_Retorna200ComETag()
    {
        var created = await (await Client.PostAsJsonAsync("/books", SampleBook())).Content.ReadFromJsonAsync<BookResponse>();

        var response = await Client.GetAsync($"/books/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.ETag is not null);
        var book = await response.Content.ReadFromJsonAsync<BookResponse>();
        Assert.Equal(created.Id, book!.Id);
    }

    [Fact]
    public async Task GetBookById_Inexistente_Retorna404()
    {
        var response = await Client.GetAsync($"/books/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetBookById_IfNoneMatchIgualAoAtual_Retorna304()
    {
        var created = await (await Client.PostAsJsonAsync("/books", SampleBook())).Content.ReadFromJsonAsync<BookResponse>();
        var first = await Client.GetAsync($"/books/{created!.Id}");
        var etag = first.Headers.ETag!.Tag;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/books/{created.Id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [Fact]
    public async Task UpdateBook_TituloEQuantidade_AplicaMudancasEDeltaNosDisponiveis()
    {
        var created = await (await Client.PostAsJsonAsync("/books", SampleBook())).Content.ReadFromJsonAsync<BookResponse>();
        var getResponse = await Client.GetAsync($"/books/{created!.Id}");
        var etag = getResponse.Headers.ETag!.Tag;

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/books/{created.Id}")
        {
            Content = JsonContent.Create(new UpdateBookRequest("Dom Casmurro (edição comentada)", null, 5)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<BookResponse>();
        Assert.Equal("Dom Casmurro (edição comentada)", updated!.Title);
        Assert.Equal(5, updated.TotalCopies);
        Assert.Equal(5, updated.AvailableCopies); // 3 -> 5, delta +2, nenhum emprestado
        Assert.NotEqual(etag, response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task DeactivateBook_SemEmprestimoAtivo_Retorna204EDesativa()
    {
        var created = await (await Client.PostAsJsonAsync("/books", SampleBook())).Content.ReadFromJsonAsync<BookResponse>();

        var response = await Client.DeleteAsync($"/books/{created!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var afterDelete = await (await Client.GetAsync($"/books/{created.Id}")).Content.ReadFromJsonAsync<BookResponse>();
        Assert.False(afterDelete!.IsActive);
    }

    [Fact]
    public async Task SearchBooks_PorTitulo_EncontraApenasOLivroCorrespondente()
    {
        await Client.PostAsJsonAsync("/books", SampleBook(isbn: "9780306406157", title: "Dom Casmurro"));
        await Client.PostAsJsonAsync("/books", SampleBook(isbn: "0306406152", title: "Memórias Póstumas"));

        var response = await Client.GetAsync("/books?search=Casmurro");
        var page = await response.Content.ReadFromJsonAsync<SearchResultPage>();

        Assert.Single(page!.Items);
        Assert.Equal("Dom Casmurro", page.Items[0].Title);
    }

    [Fact]
    public async Task SearchBooks_LimiteMenorQueOTotal_PaginaComCursor()
    {
        for (var i = 0; i < 3; i++)
        {
            var isbn = i == 0 ? "9780306406157" : i == 1 ? "0306406152" : "020163371X";
            await Client.PostAsJsonAsync("/books", SampleBook(isbn: isbn, title: $"Livro {i}"));
        }

        var firstPage = await (await Client.GetAsync("/books?limit=2")).Content.ReadFromJsonAsync<SearchResultPage>();
        Assert.Equal(2, firstPage!.Items.Count);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await (await Client.GetAsync($"/books?limit=2&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}"))
            .Content.ReadFromJsonAsync<SearchResultPage>();
        Assert.Single(secondPage!.Items);
        Assert.Null(secondPage.NextCursor);
    }

    // System.Text.Json não desserializa bem para IReadOnlyList<T> em generics
    // aninhados; PagedResponse<T> (produção) usa a interface de propósito — aqui, um
    // DTO concreto só para o teste ler a mesma resposta.
    private sealed record SearchResultPage(List<BookResponse> Items, string? NextCursor);
}
