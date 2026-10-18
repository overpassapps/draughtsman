using Draughtsman.Core.Auth;
using Draughtsman.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Storage.Tests;

/// <summary>The password-reset provider suite against SQLite: a fresh file per test, migrated like the server does.</summary>
public sealed class SqlitePasswordResetStoreTests : PasswordResetStoreContractTests
{
    private readonly string _folder = TestPaths.NewTempFolder();
    private readonly List<DraughtsmanDbContext> _contexts = [];

    protected override async Task<(IPasswordResetStore Resets, IUserStore Users, IStoreMaintenance Maintenance)> CreateStores()
    {
        StorageOptions options = SqliteTestDatabase.Options(_folder);
        DraughtsmanDbContext db = StorageSetup.CreateContext(options);
        _contexts.Add(db);
        await StorageMigrator.Migrate(db, options, NullLogger.Instance);
        return (new EfPasswordResetStore(db), new EfUserStore(db), new EfStoreMaintenance(db));
    }

    protected override Task<IPasswordResetStore> CreateAdditionalResetStore()
    {
        StorageOptions options = SqliteTestDatabase.Options(_folder);
        DraughtsmanDbContext db = StorageSetup.CreateContext(options);
        _contexts.Add(db);
        return Task.FromResult<IPasswordResetStore>(new EfPasswordResetStore(db));
    }

    protected override async Task DisposeStores()
    {
        foreach (DraughtsmanDbContext context in _contexts)
        {
            await context.DisposeAsync();
        }

        TestPaths.DeleteTempFolder(_folder);
    }

    [Fact]
    public async Task The_migration_created_a_real_table_that_a_fresh_connection_can_read()
    {
        User user = await Users.CreateUser("durable@example.com", "Durable", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "durable-hash", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), null, invalidateOthers: false);

        StorageOptions options = SqliteTestDatabase.Options(_folder);
        await using DraughtsmanDbContext fresh = StorageSetup.CreateContext(options);

        Assert.NotNull(await new EfPasswordResetStore(fresh).GetByHash("durable-hash"));
    }
}
