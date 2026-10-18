using Draughtsman.Core.Storage;

namespace Draughtsman.Storage.Tests;

/// <summary>The provider suite against Postgres. Skips, with the reason, when no database is reachable.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresStoreTests(PostgresTestDatabase database) : StoreContractTests
{
    private DraughtsmanDbContext? _db;

    protected override DraughtsmanDbContext Database => _db!;

    protected override async Task<IStore> CreateStore()
    {
        Skip.If(database.SkipReason is not null, database.SkipReason);
        _db = StorageSetup.CreateContext(database.Options);
        await database.Reset(_db);
        return new EfStore(_db, database.Options);
    }

    protected override async Task DisposeStore(IStore store)
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    [SkippableFact]
    public void Provider_name_is_postgres()
    {
        Assert.Equal("postgres", Store.ProviderName);
    }
}
