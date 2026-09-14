using System.Net.Http.Json;
using Biblioteca.Api.Features.Catalog.Contracts;
using Biblioteca.Api.Features.Users.Contracts;

namespace Biblioteca.IntegrationTests.Infrastructure;

[Collection(PostgresCollection.Name)]
public abstract class IntegrationTestBase(PostgresApiFactory factory) : IAsyncLifetime
{
    protected PostgresApiFactory Factory { get; } = factory;
    protected HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Factory.ResetAsync();
        Client = Factory.CreateClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

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

    protected Task<HttpResponseMessage> PostLoanAsync(Guid bookId, Guid userId, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/loans")
        {
            Content = JsonContent.Create(new { bookId, userId }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        return Client.SendAsync(request);
    }
}
