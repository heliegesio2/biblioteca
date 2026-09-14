using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Loans.Contracts;
using Biblioteca.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// Critério de pronto da fase 7: um `member` não alcança recurso de terceiro nem
/// `/audit-events`, não escreve no catálogo (docs/security.md#autorização).
/// </summary>
public sealed class AuthorizationTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    private async Task<(HttpClient Client, Guid UserId)> CreateMemberClientAsync()
    {
        var userId = await CreateUserAsync();
        var client = await CreateAuthenticatedClientAsync(userId, "member");
        return (client, userId);
    }

    [Fact]
    public async Task SemToken_Retorna401()
    {
        using var anonymousClient = Factory.CreateClient();

        var response = await anonymousClient.GetAsync("/books");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("unauthenticated", problem!.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task Member_NaoConsegueCriarLivro()
    {
        var (member, _) = await CreateMemberClientAsync();

        var response = await member.PostAsJsonAsync("/books",
            new CreateBookRequest("9780306406157", "Dom Casmurro", "Machado de Assis", 3));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("forbidden", problem!.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task Member_NaoConsegueAtualizarNemDesativarLivro()
    {
        var bookId = await CreateBookAsync();
        var getResponse = await Client.GetAsync($"/books/{bookId}");
        var etag = getResponse.Headers.ETag!.Tag;
        var (member, _) = await CreateMemberClientAsync();

        using var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/books/{bookId}")
        {
            Content = JsonContent.Create(new UpdateBookRequest("Outro título", null, null)),
        };
        patchRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        var patchResponse = await member.SendAsync(patchRequest);
        Assert.Equal(HttpStatusCode.Forbidden, patchResponse.StatusCode);

        var deleteResponse = await member.DeleteAsync($"/books/{bookId}");
        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task Member_ConsegueLerCatalogo()
    {
        var bookId = await CreateBookAsync();
        var (member, _) = await CreateMemberClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/books")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/books/{bookId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/books/{bookId}/availability")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/books/{bookId}/history")).StatusCode);
    }

    [Fact]
    public async Task Member_NaoConsegueCriarUsuario()
    {
        var (member, _) = await CreateMemberClientAsync();

        var response = await member.PostAsJsonAsync("/users", new { name = "Outro", email = "outro@example.com" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Member_NaoConsegueVerEmprestimosDeOutroUsuario()
    {
        var (member, _) = await CreateMemberClientAsync();
        var outroUserId = await CreateUserAsync();

        var response = await member.GetAsync($"/users/{outroUserId}/loans");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Member_ConsegueVerOsPropriosEmprestimos()
    {
        var (member, memberId) = await CreateMemberClientAsync();

        var response = await member.GetAsync($"/users/{memberId}/loans");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Member_ConsegueCriarEmprestimoParaSiMesmo()
    {
        var bookId = await CreateBookAsync(totalCopies: 3);
        var (member, memberId) = await CreateMemberClientAsync();

        var response = await PostLoanAsync(member, bookId, memberId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Member_NaoConsegueCriarEmprestimoParaOutroUsuario()
    {
        var bookId = await CreateBookAsync(totalCopies: 3);
        var (member, _) = await CreateMemberClientAsync();
        var outroUserId = await CreateUserAsync();

        var response = await PostLoanAsync(member, bookId, outroUserId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Member_ConsegueDevolverOProprioEmprestimoMasNaoOAlheio()
    {
        var bookId = await CreateBookAsync(totalCopies: 3);
        var (memberA, memberAId) = await CreateMemberClientAsync();
        var (memberB, memberBId) = await CreateMemberClientAsync();

        var loanA = await (await PostLoanAsync(memberA, bookId, memberAId)).Content.ReadFromJsonAsync<LoanResponse>();
        var loanB = await (await PostLoanAsync(memberB, bookId, memberBId)).Content.ReadFromJsonAsync<LoanResponse>();

        var ownReturn = await memberA.PostAsync($"/loans/{loanA!.Id}/return", content: null);
        Assert.Equal(HttpStatusCode.OK, ownReturn.StatusCode);

        var othersReturn = await memberA.PostAsync($"/loans/{loanB!.Id}/return", content: null);
        Assert.Equal(HttpStatusCode.Forbidden, othersReturn.StatusCode);
    }

    [Fact]
    public async Task Member_NaoConsegueCancelarEmprestimo()
    {
        var bookId = await CreateBookAsync(totalCopies: 3);
        var (member, memberId) = await CreateMemberClientAsync();
        var loan = await (await PostLoanAsync(member, bookId, memberId)).Content.ReadFromJsonAsync<LoanResponse>();

        var response = await member.PostAsync($"/loans/{loan!.Id}/cancel", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Member_NaoConsegueVerAuditEvents()
    {
        var (member, _) = await CreateMemberClientAsync();

        var response = await member.GetAsync("/audit-events");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Librarian_ConsegueAcessarQualquerEmprestimoDeUsuario()
    {
        var bookId = await CreateBookAsync(totalCopies: 3);
        var (member, memberId) = await CreateMemberClientAsync();
        var loan = await (await PostLoanAsync(member, bookId, memberId)).Content.ReadFromJsonAsync<LoanResponse>();

        // Client (fixture) já é librarian.
        var loansResponse = await Client.GetAsync($"/users/{memberId}/loans");
        Assert.Equal(HttpStatusCode.OK, loansResponse.StatusCode);

        var returnResponse = await Client.PostAsync($"/loans/{loan!.Id}/return", content: null);
        Assert.Equal(HttpStatusCode.OK, returnResponse.StatusCode);
    }
}
