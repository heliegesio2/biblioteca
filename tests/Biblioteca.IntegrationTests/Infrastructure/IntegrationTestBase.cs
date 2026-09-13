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
}
