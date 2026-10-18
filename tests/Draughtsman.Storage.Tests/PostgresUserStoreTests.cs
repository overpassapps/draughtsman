using Draughtsman.Core.Auth;

namespace Draughtsman.Storage.Tests;

/// <summary>The user/token/session provider suite against Postgres. Skips, with the reason, when no database is reachable.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresUserStoreTests(PostgresTestDatabase database) : UserStoreContractTests
{
    private readonly List<DraughtsmanDbContext> _additionalContexts = [];
    private DraughtsmanDbContext? _db;

    protected override async Task<IUserStore> CreateStore()
    {
        Skip.If(database.SkipReason is not null, database.SkipReason);
        _db = StorageSetup.CreateContext(database.Options);
        await database.Reset(_db);
        return new EfUserStore(_db);
    }

    protected override Task<IUserStore> CreateAdditionalStore()
    {
        DraughtsmanDbContext db = StorageSetup.CreateContext(database.Options);
        _additionalContexts.Add(db);
        return Task.FromResult<IUserStore>(new EfUserStore(db));
    }

    protected override async Task DisposeStore(IUserStore store)
    {
        foreach (DraughtsmanDbContext extra in _additionalContexts)
        {
            await extra.DisposeAsync();
        }

        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Email_uniqueness_is_enforced_by_the_database_not_only_the_application()
    {
        // Postgres and SQLite fold differently by default; this pins that the unique index still catches a
        // case-only duplicate on the provider whose default collation is case-sensitive.
        await Store.CreateUser("collision@example.com", "First", UserRole.Editor, "hash", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Store.CreateUser("COLLISION@EXAMPLE.COM", "Second", UserRole.Editor, "hash", DateTimeOffset.UtcNow));
    }
}
