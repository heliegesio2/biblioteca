namespace Biblioteca.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresApiFactory>
{
    public const string Name = "postgres";
}
