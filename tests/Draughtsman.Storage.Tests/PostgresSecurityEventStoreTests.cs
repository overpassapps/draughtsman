using Draughtsman.Core.Auth;

namespace Draughtsman.Storage.Tests;

/// <summary>The security event provider suite against Postgres. Skips, with the reason, when no database is reachable.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresSecurityEventStoreTests(PostgresTestDatabase database) : SecurityEventStoreContractTests
{
    private DraughtsmanDbContext? _db;

    protected override async Task<ISecurityEventStore> CreateStore()
    {
        Skip.If(database.SkipReason is not null, database.SkipReason);
        _db = StorageSetup.CreateContext(database.Options);
        await database.Reset(_db);
        return new EfSecurityEventStore(_db);
    }

    protected override async Task DisposeStore()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }
}
