using Draughtsman.Core.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Storage.Tests;

/// <summary>The security event provider suite against SQLite: a fresh file per test, migrated like the server does.</summary>
public sealed class SqliteSecurityEventStoreTests : SecurityEventStoreContractTests
{
    private readonly string _folder = TestPaths.NewTempFolder();
    private DraughtsmanDbContext? _db;

    protected override async Task<ISecurityEventStore> CreateStore()
    {
        StorageOptions options = SqliteTestDatabase.Options(_folder);
        _db = StorageSetup.CreateContext(options);
        await StorageMigrator.Migrate(_db, options, NullLogger.Instance);
        return new EfSecurityEventStore(_db);
    }

    protected override async Task DisposeStore()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        TestPaths.DeleteTempFolder(_folder);
    }
}
