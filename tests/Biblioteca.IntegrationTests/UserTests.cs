using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Users.Contracts;
using Biblioteca.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Biblioteca.IntegrationTests;

public sealed class UserTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task CreateUser_CorpoValido_Retorna201()
    {
        var response = await Client.PostAsJsonAsync("/users", new CreateUserRequest("Ana Ribeiro", "ana@example.com"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserResponse>();
        Assert.Equal("ana@example.com", user!.Email);
        Assert.True(user.IsActive);
        Assert.Equal($"/users/{user.Id}", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task CreateUser_EmailDuplicado_Retorna409()
    {
        await Client.PostAsJsonAsync("/users", new CreateUserRequest("Ana Ribeiro", "ana@example.com"));

        var response = await Client.PostAsJsonAsync("/users", new CreateUserRequest("Outra Pessoa", "ANA@EXAMPLE.COM"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("email-already-exists", problem!.Extensions["code"]!.ToString());
    }

    [Fact]
    public async Task CreateUser_EmailInvalido_Retorna400()
    {
        var response = await Client.PostAsJsonAsync("/users", new CreateUserRequest("Ana Ribeiro", "não-é-email"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetUserLoans_Inexistente_Retorna404()
    {
        var response = await Client.GetAsync($"/users/{Guid.NewGuid()}/loans");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
