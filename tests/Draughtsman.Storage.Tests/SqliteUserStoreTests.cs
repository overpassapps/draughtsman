using Draughtsman.Core.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Storage.Tests;

/// <summary>The user/token/session provider suite against SQLite: a fresh file per test, migrated like the server does.</summary>
public sealed class SqliteUserStoreTests : UserStoreContractTests
{
    private readonly string _folder = TestPaths.NewTempFolder();
    private readonly List<DraughtsmanDbContext> _additionalContexts = [];
    private DraughtsmanDbContext? _db;

    protected override async Task<IUserStore> CreateStore()
    {
        StorageOptions options = SqliteTestDatabase.Options(_folder);
        _db = StorageSetup.CreateContext(options);
        await StorageMigrator.Migrate(_db, options, NullLogger.Instance);
        return new EfUserStore(_db);
    }

    protected override Task<IUserStore> CreateAdditionalStore()
    {
        StorageOptions options = SqliteTestDatabase.Options(_folder);
        DraughtsmanDbContext db = StorageSetup.CreateContext(options);
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

        TestPaths.DeleteTempFolder(_folder);
    }

    [Fact]
    public async Task A_second_store_instance_over_the_same_database_sees_what_the_first_wrote()
    {
        // The migration created real tables, not something only the writing DbContext instance can see.
        await Store.CreateUser("durable@example.com", "Durable", UserRole.Admin, "hash", DateTimeOffset.UtcNow);

        StorageOptions options = SqliteTestDatabase.Options(_folder);
        await using DraughtsmanDbContext freshConnection = StorageSetup.CreateContext(options);
        var reopened = new EfUserStore(freshConnection);

        Assert.NotNull(await reopened.GetUserByEmail("durable@example.com"));
        Assert.Null(await reopened.GetUserByEmail("nobody@example.com"));
    }
}
