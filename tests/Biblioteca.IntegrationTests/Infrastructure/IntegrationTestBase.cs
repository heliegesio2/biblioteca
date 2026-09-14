using System.Net.Http.Headers;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Auth.Contracts;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Users.Contracts;
using Biblioteca.Api.Features.Users.Domain;
using Biblioteca.Api.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Biblioteca.IntegrationTests.Infrastructure;

[Collection(PostgresCollection.Name)]
public abstract class IntegrationTestBase(PostgresApiFactory factory) : IAsyncLifetime
{
    protected PostgresApiFactory Factory { get; } = factory;

    /// <summary>Autenticado como `librarian` por padrão — a maioria dos testes já
    /// escritos antes da fase 7 não precisa mudar nada (`librarian` acessa tudo).
    /// Testes de `member`/anônimo usam <see cref="CreateAuthenticatedClientAsync"/> ou
    /// <see cref="Factory"/>.CreateClient() diretamente.</summary>
    protected HttpClient Client { get; private set; } = null!;

    /// <summary>E-mail do `librarian` padrão de <see cref="Client"/> — é o que aparece
    /// como `actor` nos eventos de auditoria gerados através dele.</summary>
    protected string DefaultActor { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Factory.ResetAsync();
        Client = Factory.CreateClient();

        // Semeado direto no banco (não via POST /users) porque criar usuário agora
        // exige ser librarian — problema de ovo e galinha na primeira autenticação.
        Guid librarianId;
        DefaultActor = $"librarian-{Guid.NewGuid()}@example.com";
        using (var scope = Factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<BibliotecaDbContext>();
            var librarian = User.Create("Bibliotecária Padrão", DefaultActor, DateTimeOffset.UtcNow);
            dbContext.Users.Add(librarian);
            await dbContext.SaveChangesAsync();
            librarianId = librarian.Id;
        }

        var token = await IssueTokenAsync(librarianId, "librarian");
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Um HttpClient próprio, autenticado como o papel informado — para testar
    /// o que muda quando quem chama não é o `librarian` padrão de <see cref="Client"/>.</summary>
    protected async Task<HttpClient> CreateAuthenticatedClientAsync(Guid userId, string role)
    {
        var token = await IssueTokenAsync(userId, role);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> IssueTokenAsync(Guid userId, string role)
    {
        using var anonymousClient = Factory.CreateClient();
        var response = await anonymousClient.PostAsJsonAsync("/auth/token", new IssueTokenRequest(userId, role));
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return token!.AccessToken;
    }

    protected async Task<Guid> CreateBookAsync(int totalCopies = 1, string? isbn = null, string? title = null)
    {
        var response = await Client.PostAsJsonAsync("/books", new CreateBookRequest(
            isbn ?? "9780306406157", title ?? "Dom Casmurro", "Machado de Assis", totalCopies));
        var book = await response.Content.ReadFromJsonAsync<BookResponse>();
        return book!.Id;
    }

    protected async Task<Guid> CreateUserAsync(string? name = null, string? email = null)
    {
        var response = await Client.PostAsJsonAsync("/users",
            new CreateUserRequest(name ?? "Ana Ribeiro", email ?? $"{Guid.NewGuid()}@example.com"));
        var user = await response.Content.ReadFromJsonAsync<UserResponse>();
        return user!.Id;
    }

    protected async Task<List<Guid>> CreateUsersAsync(int count)
    {
        var users = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            users.Add(await CreateUserAsync(email: $"user-{i}-{Guid.NewGuid()}@example.com"));
        }

        return users;
    }

    protected Task<HttpResponseMessage> PostLoanAsync(Guid bookId, Guid userId, string? idempotencyKey = null) =>
        PostLoanAsync(Client, bookId, userId, idempotencyKey);

    protected static Task<HttpResponseMessage> PostLoanAsync(
        HttpClient client, Guid bookId, Guid userId, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/loans")
        {
            Content = JsonContent.Create(new { bookId, userId }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }
}
