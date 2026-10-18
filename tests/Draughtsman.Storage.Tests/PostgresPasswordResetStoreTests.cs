using Draughtsman.Core.Auth;
using Draughtsman.Core.Storage;

namespace Draughtsman.Storage.Tests;

/// <summary>The password-reset provider suite against Postgres. Skips, with the reason, when no database is reachable.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresPasswordResetStoreTests(PostgresTestDatabase database) : PasswordResetStoreContractTests
{
    private readonly List<DraughtsmanDbContext> _contexts = [];

    protected override async Task<(IPasswordResetStore Resets, IUserStore Users, IStoreMaintenance Maintenance)> CreateStores()
    {
        Skip.If(database.SkipReason is not null, database.SkipReason);
        DraughtsmanDbContext db = StorageSetup.CreateContext(database.Options);
        _contexts.Add(db);
        await database.Reset(db);
        return (new EfPasswordResetStore(db), new EfUserStore(db), new EfStoreMaintenance(db));
    }

    protected override Task<IPasswordResetStore> CreateAdditionalResetStore()
    {
        DraughtsmanDbContext db = StorageSetup.CreateContext(database.Options);
        _contexts.Add(db);
        return Task.FromResult<IPasswordResetStore>(new EfPasswordResetStore(db));
    }

    protected override async Task DisposeStores()
    {
        foreach (DraughtsmanDbContext context in _contexts)
        {
            await context.DisposeAsync();
        }
    }
}
