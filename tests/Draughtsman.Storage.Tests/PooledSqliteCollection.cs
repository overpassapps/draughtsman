namespace Draughtsman.Storage.Tests;

/// <summary>
/// The one place a test may use the server's real, pooled SQLite connection string and call <c>SqliteConnection.ClearAllPools()</c>: the rollback tests in
/// <see cref="StorageMigratorSafetyTests"/> exercise the migrator's own <c>ClearAllPools()</c> (it must release the pooled file handle before it puts the
/// pre-migration copy back, which only shows on Windows) and so need the pooled configuration the server runs with. That call reclaims every pool in the
/// process, so these tests run alone, after every parallel collection has finished. See <see cref="SqliteTestDatabase.Options"/> for why every other test is unpooled.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PooledSqliteCollection
{
    public const string Name = "Pooled SQLite";
}
