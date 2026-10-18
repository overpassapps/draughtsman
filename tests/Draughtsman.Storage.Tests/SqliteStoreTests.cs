using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Storage.Tests;

/// <summary>The provider suite against SQLite: a fresh file per test, migrated through the same path the server uses.</summary>
public sealed class SqliteStoreTests : StoreContractTests
{
    private readonly string _folder = TestPaths.NewTempFolder();
    private DraughtsmanDbContext? _db;

    protected override DraughtsmanDbContext Database => _db!;

    protected override async Task<IStore> CreateStore()
    {
        StorageOptions options = SqliteTestDatabase.Options(_folder);
        _db = StorageSetup.CreateContext(options);
        await StorageMigrator.Migrate(_db, options, NullLogger.Instance);
        return new EfStore(_db, options);
    }

    protected override async Task DisposeStore(IStore store)
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        TestPaths.DeleteTempFolder(_folder);
    }

    [Fact]
    public void Provider_name_is_sqlite()
    {
        Assert.Equal("sqlite", Store.ProviderName);
    }

    /// <summary>
    /// The guard reads one version of a document, not two. A change to <see cref="Document"/> leaves stored snapshots
    /// whose bytes are no longer what serialising that model back produces - the four-place model change AGENTS.md
    /// calls routine. If a replace fingerprinted the column instead of the model, every one of those documents would
    /// answer 412 to the very tag its own read just handed out, and could never be saved again by any caller.
    /// Written here rather than in the provider suite because only a direct write can produce bytes the store did not.
    /// </summary>
    [Fact]
    public async Task A_snapshot_whose_bytes_are_not_the_models_own_is_still_saveable_from_the_version_a_read_gives()
    {
        Document original = MinimalDocument("doc_drift");
        await Store.CreateDocument(original);

        string drifted = DocumentJson.Serialize(original, indented: true);
        Assert.NotEqual(DocumentJson.Serialize(original), drifted);
        await OverwriteSnapshot("doc_drift", drifted);

        Document asRead = Assert.IsType<Document>(await Store.GetDocument("doc_drift"));
        ReplaceOutcome outcome = await Store.ReplaceDocument(asRead with { Title = "Edited" }, DocumentVersionTag.Compute(asRead));

        Assert.Equal(ReplaceOutcome.Replaced, outcome);
        Assert.Equal("Edited", (await Store.GetDocument("doc_drift"))!.Title);
    }

    private async Task OverwriteSnapshot(string id, string snapshot)
    {
        await _db!.Documents.Where(d => d.Id == id)
            .ExecuteUpdateAsync(row => row.SetProperty(d => d.Snapshot, snapshot));
        _db.ChangeTracker.Clear();
    }
}
