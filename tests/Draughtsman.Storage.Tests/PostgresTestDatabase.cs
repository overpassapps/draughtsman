using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// One Postgres database for the whole Postgres leg of the provider suite (ADR 0003, rule 5). The connection comes
/// from DRAUGHTSMAN_TEST_POSTGRES: CI's service container sets it, and docs/api.md shows the docker run for a local
/// one. When it is unset <see cref="SkipReason"/> says so and every test in the collection skips instead of failing.
/// </summary>
public sealed class PostgresTestDatabase : IAsyncLifetime
{
    public const string ConnectionStringVariable = "DRAUGHTSMAN_TEST_POSTGRES";

    public string? ConnectionString { get; private set; }

    public string? SkipReason { get; private set; }

    public StorageOptions Options => new(StorageProvider.Postgres, ConnectionString ?? throw new InvalidOperationException(SkipReason));

    public async Task InitializeAsync()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(fromEnvironment))
        {
            SkipReason = $"Postgres unavailable: {ConnectionStringVariable} is not set.";
            return;
        }

        ConnectionString = fromEnvironment;
        await using DraughtsmanDbContext db = StorageSetup.CreateContext(Options);
        await StorageMigrator.Migrate(db, Options, NullLogger.Instance);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>Empties every table so each test starts clean without paying for a database per test.</summary>
    public async Task Reset(DraughtsmanDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE security_events, audit, document_updates, document_versions, documents, assets, password_reset_tokens, sessions, api_tokens, users RESTART IDENTITY");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresTestDatabase>
{
    public const string Name = "postgres";
}
