using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// The specification of <see cref="IStore"/> behaviour (ADR 0003, rule 5). Every provider subclass runs every test
/// here unchanged; anything a provider cannot pass is a provider bug, not a reason to special-case the test.
/// Each test starts with an empty store supplied by <see cref="CreateStore"/>.
/// </summary>
public abstract class StoreContractTests : IAsyncLifetime
{
    private static readonly Actor s_user = new(ActorKind.User, "alice");
    private static readonly Actor s_agent = new(ActorKind.Agent, "claude-code", "claude-fable-5-1");

    protected IStore Store { get; private set; } = null!;

    /// <summary>The context under <see cref="Store"/>, for the few tests that must write or inspect a row the store itself would not.</summary>
    protected abstract DraughtsmanDbContext Database { get; }

    /// <summary>Returns an empty store. Called once per test.</summary>
    protected abstract Task<IStore> CreateStore();

    protected virtual Task DisposeStore(IStore store)
    {
        return Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        Store = await CreateStore();
    }

    public async Task DisposeAsync()
    {
        await DisposeStore(Store);
    }

    [SkippableFact]
    public void Provider_name_is_set()
    {
        Assert.False(string.IsNullOrWhiteSpace(Store.ProviderName));
    }

    [SkippableFact]
    public async Task Empty_store_has_nothing()
    {
        Assert.Null(await Store.GetDocument("doc_none"));
        DocumentPage page = await Store.ListDocuments(new DocumentQuery());
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.Empty(await Store.GetUpdates("doc_none"));
        Assert.Empty((await Store.ListVersions("doc_none", new VersionQuery())).Items);
        Assert.Null(await Store.GetVersion("doc_none", "v_none"));
        Assert.Empty(await Store.ListAudit("doc_none"));
        Assert.Null(await Store.GetAsset("asset_none"));
        Assert.Null(await Store.FindAssetByHash("hash_none"));
    }

    [SkippableFact]
    public async Task Create_then_get_round_trips_the_whole_document()
    {
        Document document = FullDocument("doc_full");

        await Store.CreateDocument(document);
        Document? stored = await Store.GetDocument(document.Id);

        AssertSameDocument(document, stored);
    }

    [SkippableFact]
    public async Task Get_returns_a_fresh_instance_each_time()
    {
        Document document = MinimalDocument("doc_fresh");
        await Store.CreateDocument(document);

        Document? first = await Store.GetDocument(document.Id);
        Document? second = await Store.GetDocument(document.Id);

        Assert.NotNull(first);
        Assert.NotSame(first, second);
        AssertSameDocument(first, second);
    }

    [SkippableFact]
    public async Task Create_with_a_taken_id_throws()
    {
        await Store.CreateDocument(MinimalDocument("doc_dup"));

        DocumentExistsException error = await Assert.ThrowsAsync<DocumentExistsException>(() => Store.CreateDocument(MinimalDocument("doc_dup", "Other title")));

        Assert.Equal("doc_dup", error.DocumentId);
        Assert.Equal("Minimal", (await Store.GetDocument("doc_dup"))!.Title);
    }

    [SkippableFact]
    public async Task Timestamps_round_trip_as_utc_instants_to_the_millisecond()
    {
        var created = new DateTimeOffset(2026, 9, 21, 9, 30, 15, 123, TimeSpan.FromHours(5));
        var updated = new DateTimeOffset(2026, 9, 21, 10, 0, 0, 999, TimeSpan.FromHours(-3));
        Document document = MinimalDocument("doc_time") with { Meta = new Meta(created, updated, "test") };

        await Store.CreateDocument(document);
        Document? stored = await Store.GetDocument(document.Id);
        DocumentSummary summary = Assert.Single((await Store.ListDocuments(new DocumentQuery())).Items);

        Assert.NotNull(stored);
        Assert.Equal(created.ToUniversalTime(), stored.Meta.CreatedAt);
        Assert.Equal(updated.ToUniversalTime(), stored.Meta.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, stored.Meta.CreatedAt.Offset);
        Assert.Equal(created.ToUniversalTime(), summary.CreatedAt);
        Assert.Equal(updated.ToUniversalTime(), summary.UpdatedAt);
    }

    [SkippableFact]
    public async Task Replace_overwrites_the_snapshot_and_reports_whether_it_existed()
    {
        Document original = MinimalDocument("doc_rep");
        await Store.CreateDocument(original);
        Document replacement = FullDocument("doc_rep") with
        {
            Title = "Replaced",
            Type = DocumentType.Architecture,
            Meta = original.Meta with { UpdatedAt = original.Meta.UpdatedAt.AddMinutes(5) },
        };

        ReplaceOutcome replaced = await Store.ReplaceDocument(replacement, DocumentVersionTag.Compute(original));
        ReplaceOutcome missing = await Store.ReplaceDocument(MinimalDocument("doc_absent"), DocumentVersionTag.Compute(original));
        Document? stored = await Store.GetDocument("doc_rep");
        DocumentSummary summary = Assert.Single((await Store.ListDocuments(new DocumentQuery())).Items);

        Assert.Equal(ReplaceOutcome.Replaced, replaced);
        Assert.Equal(ReplaceOutcome.NotFound, missing);
        AssertSameDocument(replacement, stored);
        Assert.Equal("Replaced", summary.Title);
        Assert.Equal(DocumentType.Architecture, summary.Type);
        Assert.Equal(replacement.Meta.UpdatedAt, summary.UpdatedAt);
        Assert.Null(await Store.GetDocument("doc_absent"));
    }

    [SkippableFact]
    public async Task A_replace_guarded_by_a_base_version_lands_only_while_that_version_is_current()
    {
        Document original = MinimalDocument("doc_guard");
        await Store.CreateDocument(original);
        string baseVersion = DocumentVersionTag.Compute(original);

        // Someone else writes first; the base version the first caller read is no longer the stored one.
        Document theirs = original with { Title = "Theirs", Meta = original.Meta with { UpdatedAt = original.Meta.UpdatedAt.AddMinutes(1) } };
        ReplaceOutcome landed = await Store.ReplaceDocument(theirs, baseVersion);

        Document mine = original with { Title = "Mine", Meta = original.Meta with { UpdatedAt = original.Meta.UpdatedAt.AddMinutes(2) } };
        ReplaceOutcome refused = await Store.ReplaceDocument(mine, baseVersion);
        ReplaceOutcome rebased = await Store.ReplaceDocument(mine, DocumentVersionTag.Compute(theirs));

        Assert.Equal(ReplaceOutcome.Replaced, landed);
        Assert.Equal(ReplaceOutcome.VersionMismatch, refused);
        Assert.Equal(ReplaceOutcome.Replaced, rebased);
        Assert.Equal("Mine", (await Store.GetDocument("doc_guard"))!.Title);
    }

    [SkippableFact]
    public async Task A_guarded_replace_of_a_document_that_is_gone_is_not_found_rather_than_a_mismatch()
    {
        Document original = MinimalDocument("doc_gone");
        await Store.CreateDocument(original);
        string baseVersion = DocumentVersionTag.Compute(original);
        await Store.DeleteDocument("doc_gone");

        Assert.Equal(ReplaceOutcome.NotFound, await Store.ReplaceDocument(original with { Title = "Late" }, baseVersion));
    }

    [SkippableFact]
    public async Task Rename_changes_the_title_and_updated_at_but_nothing_else_and_moves_the_version()
    {
        Document original = MinimalDocument("doc_rename");
        await Store.CreateDocument(original);
        string baseVersion = DocumentVersionTag.Compute(original);

        RenameOutcome outcome = await Store.RenameDocument("doc_rename", "Renamed", baseVersion);
        Document? renamed = await Store.GetDocument("doc_rename");
        DocumentSummary summary = Assert.Single((await Store.ListDocuments(new DocumentQuery())).Items);

        Assert.Equal(RenameOutcome.Renamed, outcome);
        Assert.NotNull(renamed);
        Assert.Equal("Renamed", renamed.Title);
        Assert.Equal("Renamed", summary.Title);
        Assert.True(renamed.Meta.UpdatedAt >= original.Meta.UpdatedAt);
        Assert.Equal(original.Meta.CreatedAt, renamed.Meta.CreatedAt);
        Assert.Equal(original.Meta.CreatedBy, renamed.Meta.CreatedBy);
        Assert.Equal(original.Nodes, renamed.Nodes);
        Assert.Equal(original.Edges, renamed.Edges);
        Assert.Equal(original.Type, renamed.Type);
        // The snapshot was regenerated, so its version tag moved on.
        Assert.NotEqual(baseVersion, DocumentVersionTag.Compute(renamed));
    }

    [SkippableFact]
    public async Task A_rename_guarded_by_a_base_version_lands_only_while_that_version_is_current()
    {
        Document original = MinimalDocument("doc_rename_guard");
        await Store.CreateDocument(original);
        string baseVersion = DocumentVersionTag.Compute(original);

        Document theirs = original with { Title = "Theirs", Meta = original.Meta with { UpdatedAt = original.Meta.UpdatedAt.AddMinutes(1) } };
        await Store.ReplaceDocument(theirs, baseVersion);

        RenameOutcome refused = await Store.RenameDocument("doc_rename_guard", "Mine", baseVersion);
        RenameOutcome rebased = await Store.RenameDocument("doc_rename_guard", "Mine", DocumentVersionTag.Compute(theirs));

        Assert.Equal(RenameOutcome.VersionMismatch, refused);
        Assert.Equal(RenameOutcome.Renamed, rebased);
        Assert.Equal("Mine", (await Store.GetDocument("doc_rename_guard"))!.Title);
    }

    [SkippableFact]
    public async Task A_rename_of_a_document_that_is_gone_is_not_found()
    {
        Document original = MinimalDocument("doc_rename_gone");
        await Store.CreateDocument(original);
        string baseVersion = DocumentVersionTag.Compute(original);
        await Store.DeleteDocument("doc_rename_gone");

        Assert.Equal(RenameOutcome.NotFound, await Store.RenameDocument("doc_rename_gone", "Late", baseVersion));
    }

    [SkippableFact]
    public async Task The_stored_version_tag_is_the_tag_of_the_document_after_every_kind_of_write()
    {
        Document original = FullDocument("doc_tag");
        await Store.CreateDocument(original);
        await AssertTagMatchesStoredDocument("doc_tag");

        Document replaced = original with { Title = "Replaced", Meta = original.Meta with { UpdatedAt = original.Meta.UpdatedAt.AddMinutes(1) } };
        await Store.ReplaceDocument(replaced, (await Store.GetDocumentTag("doc_tag"))!);
        await AssertTagMatchesStoredDocument("doc_tag");

        await Store.RenameDocument("doc_tag", "Renamed", (await Store.GetDocumentTag("doc_tag"))!);
        await AssertTagMatchesStoredDocument("doc_tag");

        Assert.Null(await Store.GetDocumentTag("doc_tag_absent"));
        Assert.Null(await Store.GetStoredDocument("doc_tag_absent"));
    }

    /// <summary>
    /// AU-16: a conditional read must not cost a parse of the snapshot. With the snapshot made unreadable, the tag can
    /// still be answered (it comes from its own column), while reading the document itself fails - the proof that the
    /// answer did not go through the snapshot. The implementation this replaced deserialised, serialised and hashed the
    /// snapshot for every call, so it throws here.
    /// </summary>
    [SkippableFact]
    public async Task The_version_tag_is_answered_from_its_own_column_without_reading_the_snapshot()
    {
        Document original = MinimalDocument("doc_tag_column");
        await Store.CreateDocument(original);
        string tag = DocumentVersionTag.Compute(original);

        await Database.Documents.Where(d => d.Id == "doc_tag_column")
            .ExecuteUpdateAsync(row => row.SetProperty(d => d.Snapshot, "{ this is not a document"));
        Database.ChangeTracker.Clear();

        Assert.Equal(tag, await Store.GetDocumentTag("doc_tag_column"));
        await Assert.ThrowsAnyAsync<Exception>(() => Store.GetDocument("doc_tag_column"));
    }

    [SkippableFact]
    public async Task A_row_from_before_the_tag_column_gets_its_tag_on_first_ask_and_can_then_be_saved()
    {
        Document original = FullDocument("doc_tag_legacy");
        await Store.CreateDocument(original);
        string expected = DocumentVersionTag.Compute(original);

        // What an upgraded database holds: the snapshot, and no tag yet.
        await Database.Documents.Where(d => d.Id == "doc_tag_legacy")
            .ExecuteUpdateAsync(row => row.SetProperty(d => d.Etag, (string?)null));
        Database.ChangeTracker.Clear();
        Assert.Null(await Database.Documents.AsNoTracking().Where(d => d.Id == "doc_tag_legacy").Select(d => d.Etag).SingleAsync());

        Assert.Equal(expected, await Store.GetDocumentTag("doc_tag_legacy"));
        Assert.Equal(expected, await Database.Documents.AsNoTracking().Where(d => d.Id == "doc_tag_legacy").Select(d => d.Etag).SingleAsync());

        // And a save straight after an upgrade, with no read in between, still works from the tag a read would have given.
        await Database.Documents.Where(d => d.Id == "doc_tag_legacy")
            .ExecuteUpdateAsync(row => row.SetProperty(d => d.Etag, (string?)null));
        Database.ChangeTracker.Clear();
        ReplaceOutcome outcome = await Store.ReplaceDocument(original with { Title = "After upgrade", Meta = original.Meta with { UpdatedAt = original.Meta.UpdatedAt.AddMinutes(1) } }, expected);
        Assert.Equal(ReplaceOutcome.Replaced, outcome);
        Assert.Equal("After upgrade", (await Store.GetDocument("doc_tag_legacy"))!.Title);
    }

    [SkippableFact]
    public async Task Pruning_checkpoints_keeps_the_newest_per_document_and_never_touches_a_named_version()
    {
        var maintenance = new EfStoreMaintenance(Database);
        await Store.CreateDocument(MinimalDocument("doc_prune_a"));
        await Store.CreateDocument(MinimalDocument("doc_prune_b"));
        var aCheckpoints = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            aCheckpoints.Add((await Store.CreateVersion("doc_prune_a", "checkpoint", s_agent)).Id);
        }

        DocumentVersion named = await Store.CreateVersion("doc_prune_a", "release 1.0", s_user);
        DocumentVersion bFirst = await Store.CreateVersion("doc_prune_b", "checkpoint", s_agent);
        DocumentVersion bSecond = await Store.CreateVersion("doc_prune_b", "checkpoint", s_agent);

        Assert.Equal(0, await maintenance.PruneCheckpoints(0));
        Assert.Equal(0, await maintenance.PruneCheckpoints(-1));
        Assert.Equal(6, (await Store.ListVersions("doc_prune_a", new VersionQuery())).Total);

        int removed = await maintenance.PruneCheckpoints(3);

        Assert.Equal(2, removed);
        List<string> remainingA = (await Store.ListVersions("doc_prune_a", new VersionQuery())).Items.Select(v => v.Id).ToList();
        Assert.Equal(4, remainingA.Count);
        Assert.Contains(named.Id, remainingA);
        Assert.All(aCheckpoints.Skip(2), id => Assert.Contains(id, remainingA));
        Assert.All(aCheckpoints.Take(2), id => Assert.DoesNotContain(id, remainingA));
        Assert.Equal([bSecond.Id, bFirst.Id], (await Store.ListVersions("doc_prune_b", new VersionQuery())).Items.Select(v => v.Id));
        Assert.Equal(0, await maintenance.PruneCheckpoints(3));
    }

    [SkippableFact]
    public async Task Purging_sessions_removes_the_long_ended_and_keeps_the_live_and_the_recent()
    {
        var maintenance = new EfStoreMaintenance(Database);
        DateTime now = DateTime.UtcNow;
        Database.Users.Add(new Entities.UserRow { Id = "usr_purge", Email = "purge@example.com", DisplayName = "Purge", Role = "Editor", PasswordHash = "hash", CreatedAt = now, Active = true });
        Database.Sessions.AddRange(
            Session("ses_expired_long_ago", now.AddDays(-60), now.AddDays(-30), revoked: false),
            Session("ses_expired_yesterday", now.AddDays(-31), now.AddDays(-1), revoked: false),
            Session("ses_active", now.AddDays(-1), now.AddDays(29), revoked: false),
            Session("ses_revoked_old", now.AddDays(-20), now.AddDays(10), revoked: true),
            Session("ses_revoked_recent", now.AddDays(-1), now.AddDays(29), revoked: true));
        await Database.SaveChangesAsync();
        Database.ChangeTracker.Clear();

        int removed = await maintenance.PurgeSessions(DateTimeOffset.UtcNow.AddDays(-7));

        Assert.Equal(2, removed);
        List<string> left = await Database.Sessions.AsNoTracking().Select(x => x.Id).OrderBy(x => x).ToListAsync();
        Assert.Equal(["ses_active", "ses_expired_yesterday", "ses_revoked_recent"], left);
    }

    [SkippableFact]
    public async Task Total_asset_bytes_sums_every_stored_asset()
    {
        var maintenance = new EfStoreMaintenance(Database);
        Assert.Equal(0, await maintenance.TotalAssetBytes());

        await Store.CreateAsset(new AssetInfo("ast_one", "hash-one", "image/png", 1000, "assets/ha/hash-one.png", DateTimeOffset.UtcNow));
        await Store.CreateAsset(new AssetInfo("ast_two", "hash-two", "image/png", 234, "assets/ha/hash-two.png", DateTimeOffset.UtcNow));

        Assert.Equal(1234, await maintenance.TotalAssetBytes());
    }

    private static Entities.SessionRow Session(string id, DateTime created, DateTime expires, bool revoked)
    {
        return new Entities.SessionRow { Id = id, UserId = "usr_purge", TokenHash = "hash-" + id, CreatedAt = created, ExpiresAt = expires, Revoked = revoked };
    }

    private async Task AssertTagMatchesStoredDocument(string id)
    {
        Document stored = (await Store.GetDocument(id))!;
        StoredDocument withTag = (await Store.GetStoredDocument(id))!;
        Assert.Equal(DocumentVersionTag.Compute(stored), await Store.GetDocumentTag(id));
        Assert.Equal(DocumentVersionTag.Compute(stored), withTag.Version);
        AssertSameDocument(stored, withTag.Document);
    }

    [SkippableFact]
    public async Task Delete_removes_document_updates_and_versions_but_keeps_audit()
    {
        await Store.CreateDocument(MinimalDocument("doc_del"));
        await Store.AppendUpdate("doc_del", [1, 2, 3], s_user);
        await Store.CreateVersion("doc_del", "v1", s_user);
        await Store.AppendAudit(new AuditEntry("doc_del", s_user, AuditActions.Created, DateTimeOffset.UtcNow));

        bool deleted = await Store.DeleteDocument("doc_del");
        bool again = await Store.DeleteDocument("doc_del");

        Assert.True(deleted);
        Assert.False(again);
        Assert.Null(await Store.GetDocument("doc_del"));
        Assert.Empty(await Store.GetUpdates("doc_del"));
        Assert.Empty((await Store.ListVersions("doc_del", new VersionQuery())).Items);
        Assert.Single(await Store.ListAudit("doc_del"));
        Assert.Equal(0, (await Store.ListDocuments(new DocumentQuery())).Total);
    }

    [SkippableFact]
    public async Task Same_id_can_be_created_again_after_deletion()
    {
        await Store.CreateDocument(MinimalDocument("doc_again"));
        await Store.AppendUpdate("doc_again", [9], s_user);
        await Store.DeleteDocument("doc_again");

        await Store.CreateDocument(MinimalDocument("doc_again", "Second life"));
        long seq = await Store.AppendUpdate("doc_again", [10], s_user);

        Assert.Equal("Second life", (await Store.GetDocument("doc_again"))!.Title);
        Assert.Equal(1, seq);
        Assert.Empty((await Store.ListVersions("doc_again", new VersionQuery())).Items);
    }

    [SkippableFact]
    public async Task List_orders_by_updated_at_descending_and_pages_with_total()
    {
        DateTimeOffset start = new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 5; i++)
        {
            await Store.CreateDocument(MinimalDocument($"doc_{i}", $"Document {i}") with { Meta = new Meta(start, start.AddMinutes(i), "test") });
        }

        DocumentPage all = await Store.ListDocuments(new DocumentQuery());
        DocumentPage page = await Store.ListDocuments(new DocumentQuery(Offset: 1, Limit: 2));
        DocumentPage beyond = await Store.ListDocuments(new DocumentQuery(Offset: 10, Limit: 2));

        Assert.Equal(["doc_4", "doc_3", "doc_2", "doc_1", "doc_0"], all.Items.Select(d => d.Id));
        Assert.Equal(5, all.Total);
        Assert.Equal(["doc_3", "doc_2"], page.Items.Select(d => d.Id));
        Assert.Equal(5, page.Total);
        Assert.Equal(1, page.Offset);
        Assert.Equal(2, page.Limit);
        Assert.Empty(beyond.Items);
        Assert.Equal(5, beyond.Total);
    }

    [SkippableFact]
    public async Task List_query_matches_title_substring_case_insensitively()
    {
        await Store.CreateDocument(MinimalDocument("doc_a", "Order Service"));
        await Store.CreateDocument(MinimalDocument("doc_b", "Payment flow"));
        await Store.CreateDocument(MinimalDocument("doc_c", "Reorder queue"));

        DocumentPage order = await Store.ListDocuments(new DocumentQuery("order"));
        DocumentPage upper = await Store.ListDocuments(new DocumentQuery("ORDER"));
        DocumentPage none = await Store.ListDocuments(new DocumentQuery("invoice"));
        DocumentPage blank = await Store.ListDocuments(new DocumentQuery("  "));

        Assert.Equal(["doc_a", "doc_c"], order.Items.Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(2, order.Total);
        Assert.Equal(2, upper.Total);
        Assert.Equal(0, none.Total);
        Assert.Equal(3, blank.Total);
    }

    [SkippableFact]
    public async Task List_query_folds_case_beyond_ascii()
    {
        await Store.CreateDocument(MinimalDocument("doc_u", "Über Service"));
        await Store.CreateDocument(MinimalDocument("doc_p", "Payment flow"));

        DocumentPage lower = await Store.ListDocuments(new DocumentQuery("über"));
        DocumentPage upper = await Store.ListDocuments(new DocumentQuery("ÜBER"));

        Assert.Equal("doc_u", Assert.Single(lower.Items).Id);
        Assert.Equal("doc_u", Assert.Single(upper.Items).Id);
    }

    [SkippableFact]
    public async Task Long_incompressible_title_is_stored_and_searchable()
    {
        // Titles are unbounded (docs/schema/document.schema.json); nothing derived from one may cap what a provider accepts.
        var random = new Random(20260921);
        string title = string.Concat(Enumerable.Range(0, 8000).Select(_ => (char)random.Next('a', 'z' + 1)));
        await Store.CreateDocument(MinimalDocument("doc_long", title));

        DocumentPage page = await Store.ListDocuments(new DocumentQuery(title.Substring(4000, 64)));

        Assert.Equal(title, Assert.Single(page.Items).Title);
        Assert.Equal(title, (await Store.GetDocument("doc_long"))!.Title);
    }

    [SkippableFact]
    public async Task List_query_treats_wildcard_characters_literally()
    {
        await Store.CreateDocument(MinimalDocument("doc_pct", "100% done"));
        await Store.CreateDocument(MinimalDocument("doc_us", "snake_case"));
        await Store.CreateDocument(MinimalDocument("doc_plain", "100 done and snakeXcase"));

        DocumentPage percent = await Store.ListDocuments(new DocumentQuery("100%"));
        DocumentPage underscore = await Store.ListDocuments(new DocumentQuery("snake_"));

        Assert.Equal("doc_pct", Assert.Single(percent.Items).Id);
        Assert.Equal("doc_us", Assert.Single(underscore.Items).Id);
    }

    [SkippableFact]
    public async Task Updates_are_appended_in_dense_sequence_with_exact_bytes()
    {
        await Store.CreateDocument(MinimalDocument("doc_upd"));
        byte[] first = [0, 1, 2, 255, 0, 128];
        byte[] second = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

        long seq1 = await Store.AppendUpdate("doc_upd", first, s_user);
        long seq2 = await Store.AppendUpdate("doc_upd", second, s_agent);
        long seq3 = await Store.AppendUpdate("doc_upd", [], s_user);
        IReadOnlyList<DocumentUpdate> all = await Store.GetUpdates("doc_upd");
        IReadOnlyList<DocumentUpdate> tail = await Store.GetUpdates("doc_upd", afterSeq: 1);

        Assert.Equal([1, 2, 3], new[] { seq1, seq2, seq3 });
        Assert.Equal([1, 2, 3], all.Select(u => u.Seq));
        Assert.Equal(first, all[0].Update);
        Assert.Equal(second, all[1].Update);
        Assert.Empty(all[2].Update);
        Assert.Equal(s_user, all[0].Actor);
        Assert.Equal(s_agent, all[1].Actor);
        Assert.All(all, u => Assert.Equal("doc_upd", u.DocumentId));
        Assert.All(all, u => Assert.Equal(TimeSpan.Zero, u.CreatedAt.Offset));
        Assert.Equal([2, 3], tail.Select(u => u.Seq));
        Assert.Empty(await Store.GetUpdates("doc_upd", afterSeq: 3));
    }

    [SkippableFact]
    public async Task Update_bytes_are_not_shared_with_the_caller()
    {
        await Store.CreateDocument(MinimalDocument("doc_iso"));
        byte[] payload = [1, 2, 3];

        await Store.AppendUpdate("doc_iso", payload, s_user);
        payload[0] = 99;
        IReadOnlyList<DocumentUpdate> read = await Store.GetUpdates("doc_iso");
        read[0].Update[1] = 99;
        IReadOnlyList<DocumentUpdate> again = await Store.GetUpdates("doc_iso");

        Assert.Equal([1, 2, 3], again[0].Update);
    }

    [SkippableFact]
    public async Task Append_update_to_unknown_document_throws()
    {
        DocumentNotFoundException error = await Assert.ThrowsAsync<DocumentNotFoundException>(() => Store.AppendUpdate("doc_ghost", [1], s_user));

        Assert.Equal("doc_ghost", error.DocumentId);
    }

    [SkippableFact]
    public async Task Update_logs_are_per_document()
    {
        await Store.CreateDocument(MinimalDocument("doc_x"));
        await Store.CreateDocument(MinimalDocument("doc_y"));

        await Store.AppendUpdate("doc_x", [1], s_user);
        await Store.AppendUpdate("doc_x", [2], s_user);
        long ySeq = await Store.AppendUpdate("doc_y", [3], s_user);

        Assert.Equal(1, ySeq);
        Assert.Equal(2, (await Store.GetUpdates("doc_x")).Count);
        Assert.Single(await Store.GetUpdates("doc_y"));
    }

    [SkippableFact]
    public async Task Version_snapshots_the_document_as_stored_at_that_moment()
    {
        Document original = FullDocument("doc_ver");
        await Store.CreateDocument(original);

        DocumentVersion created = await Store.CreateVersion("doc_ver", "created", s_agent, "Initial import");
        await Store.ReplaceDocument(original with { Title = "Changed", Nodes = [] }, DocumentVersionTag.Compute(original));
        DocumentVersion? fetched = await Store.GetVersion("doc_ver", created.Id);

        Assert.StartsWith(StoreIds.VersionPrefix, created.Id, StringComparison.Ordinal);
        Assert.Equal("doc_ver", created.DocumentId);
        Assert.Equal("created", created.Name);
        Assert.Equal(s_agent, created.Actor);
        Assert.Equal("Initial import", created.Reason);
        Assert.Equal(TimeSpan.Zero, created.CreatedAt.Offset);
        AssertSameDocument(original, created.Document);
        Assert.NotNull(fetched);
        Assert.Equal(created.ToSummary(), fetched.ToSummary());
        AssertSameDocument(created.Document, fetched.Document);
        Assert.Equal("Changed", (await Store.GetDocument("doc_ver"))!.Title);
    }

    [SkippableFact]
    public async Task Versions_list_newest_first_without_snapshots()
    {
        await Store.CreateDocument(MinimalDocument("doc_vl"));
        DocumentVersion v1 = await Store.CreateVersion("doc_vl", "first", s_user);
        DocumentVersion v2 = await Store.CreateVersion("doc_vl", "second", s_user, "checkpoint");
        DocumentVersion v3 = await Store.CreateVersion("doc_vl", "third", s_agent);

        VersionPage page = await Store.ListVersions("doc_vl", new VersionQuery());
        IReadOnlyList<VersionSummary> versions = page.Items;

        Assert.Equal([v3.Id, v2.Id, v1.Id], versions.Select(v => v.Id));
        Assert.Equal(v2.ToSummary(), versions[1]);
        Assert.Null(versions[2].Reason);
        Assert.Equal(s_agent, versions[0].Actor);
        Assert.Equal(3, page.Total);
        Assert.Equal(0, page.Offset);
        Assert.Equal(VersionQuery.DefaultLimit, page.Limit);
    }

    [SkippableFact]
    public async Task Versions_list_pages_with_total()
    {
        await Store.CreateDocument(MinimalDocument("doc_vp"));
        for (int i = 0; i < 5; i++)
        {
            await Store.CreateVersion("doc_vp", $"v{i}", s_user);
        }

        VersionPage all = await Store.ListVersions("doc_vp", new VersionQuery());
        VersionPage page = await Store.ListVersions("doc_vp", new VersionQuery(Offset: 1, Limit: 2));
        VersionPage beyond = await Store.ListVersions("doc_vp", new VersionQuery(Offset: 10, Limit: 2));

        Assert.Equal(5, all.Total);
        Assert.Equal(5, all.Items.Count);
        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(1, page.Offset);
        Assert.Equal(2, page.Limit);
        Assert.Empty(beyond.Items);
        Assert.Equal(5, beyond.Total);
    }

    [SkippableFact]
    public async Task Version_lookup_is_scoped_to_its_document()
    {
        await Store.CreateDocument(MinimalDocument("doc_v1"));
        await Store.CreateDocument(MinimalDocument("doc_v2"));
        DocumentVersion version = await Store.CreateVersion("doc_v1", "mine", s_user);

        Assert.NotNull(await Store.GetVersion("doc_v1", version.Id));
        Assert.Null(await Store.GetVersion("doc_v2", version.Id));
        Assert.Null(await Store.GetVersion("doc_v1", "v_missing"));
        Assert.Empty((await Store.ListVersions("doc_v2", new VersionQuery())).Items);
    }

    [SkippableFact]
    public async Task Create_version_for_unknown_document_throws()
    {
        DocumentNotFoundException error = await Assert.ThrowsAsync<DocumentNotFoundException>(() => Store.CreateVersion("doc_ghost", "x", s_user));

        Assert.Equal("doc_ghost", error.DocumentId);
    }

    [SkippableFact]
    public async Task Audit_rows_keep_actor_kind_and_model_in_order()
    {
        var at = new DateTimeOffset(2026, 9, 21, 12, 0, 0, 500, TimeSpan.FromHours(1));
        AuditEntry created = await Store.AppendAudit(new AuditEntry("doc_aud", s_user, AuditActions.Created, at));
        AuditEntry replaced = await Store.AppendAudit(new AuditEntry("doc_aud", s_agent, AuditActions.Replaced, at.AddSeconds(1)));
        await Store.AppendAudit(new AuditEntry("doc_other", s_user, AuditActions.Deleted, at));

        IReadOnlyList<AuditEntry> rows = await Store.ListAudit("doc_aud");

        Assert.True(created.Id > 0);
        Assert.True(replaced.Id > created.Id);
        Assert.Equal(at.ToUniversalTime(), created.At);
        Assert.Equal([created, replaced], rows);
        Assert.Equal(ActorKind.User, rows[0].Actor.Kind);
        Assert.Null(rows[0].Actor.Model);
        Assert.Equal(ActorKind.Agent, rows[1].Actor.Kind);
        Assert.Equal("claude-code", rows[1].Actor.Name);
        Assert.Equal("claude-fable-5-1", rows[1].Actor.Model);
        Assert.Equal(AuditActions.Replaced, rows[1].Action);
    }

    [SkippableFact]
    public async Task Assets_round_trip_and_deduplicate_by_hash()
    {
        var created = new DateTimeOffset(2026, 9, 21, 9, 0, 0, 123, TimeSpan.Zero);
        var asset = new AssetInfo("asset_a", "hash-a", "image/png", 1234, "assets/aa/hash-a.png", created);

        AssetInfo stored = await Store.CreateAsset(asset);
        AssetInfo? fetched = await Store.GetAsset("asset_a");
        AssetInfo? byHash = await Store.FindAssetByHash("hash-a");

        Assert.Equal(asset, stored);
        Assert.Equal(asset, fetched);
        Assert.Equal(asset, byHash);
        Assert.Equal(TimeSpan.Zero, fetched!.CreatedAt.Offset);
        Assert.Equal(created, fetched.CreatedAt);

        // The same content (same hash) cannot be stored twice under a new id; the caller looks the winner up instead.
        AssetExistsException error = await Assert.ThrowsAsync<AssetExistsException>(() => Store.CreateAsset(asset with { Id = "asset_b" }));
        Assert.Equal("hash-a", error.Hash);
        Assert.Equal("asset_a", (await Store.FindAssetByHash("hash-a"))!.Id);
    }

    /// <summary>Records compare collections by reference, so equality is the serialised JSON, which the project keeps byte-stable.</summary>
    protected static void AssertSameDocument(Document expected, Document? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(DocumentJson.Serialize(expected), DocumentJson.Serialize(actual));
    }

    // ---- The bin (OP-02): a soft delete every provider must honour identically. ----

    private static readonly DateTimeOffset s_binNow = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public async Task Soft_delete_hides_the_document_from_every_read_and_write_but_keeps_what_it_holds()
    {
        await Store.CreateDocument(MinimalDocument("doc_bin"));
        await Store.AppendUpdate("doc_bin", [1, 2], s_user);
        DocumentVersion version = await Store.CreateVersion("doc_bin", "before", s_user);
        string tag = (await Store.GetDocumentTag("doc_bin"))!;

        Assert.True(await Store.SoftDeleteDocument("doc_bin", s_binNow, "usr_a", "Alice"));

        Assert.Null(await Store.GetDocument("doc_bin"));
        Assert.Null(await Store.GetDocumentTag("doc_bin"));
        Assert.Null(await Store.GetStoredDocument("doc_bin"));
        Assert.Equal(0, (await Store.ListDocuments(new DocumentQuery())).Total);
        Assert.Equal(0, (await Store.ListDocuments(new DocumentQuery("minimal"))).Total);
        Assert.Equal(ReplaceOutcome.NotFound, await Store.ReplaceDocument(MinimalDocument("doc_bin", "Changed"), tag));
        Assert.Equal(RenameOutcome.NotFound, await Store.RenameDocument("doc_bin", "Renamed", tag));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => Store.AppendUpdate("doc_bin", [3], s_user));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => Store.CreateVersion("doc_bin", "after", s_user));
        Assert.Empty((await Store.ListVersions("doc_bin", new VersionQuery())).Items);
        Assert.Null(await Store.GetVersion("doc_bin", version.Id));

        // Hidden is not gone: the rows a restore needs are all still there, and the id stays taken.
        Assert.Equal(1, await Database.DocumentVersions.CountAsync(v => v.DocumentId == "doc_bin"));
        Assert.Equal(1, await Database.DocumentUpdates.CountAsync(u => u.DocumentId == "doc_bin"));
        await Assert.ThrowsAsync<DocumentExistsException>(() => Store.CreateDocument(MinimalDocument("doc_bin")));
        BinnedDocument binned = Assert.IsType<BinnedDocument>(await Store.GetBinnedDocument("doc_bin"));
        Assert.Equal(s_binNow, binned.DeletedAt);
        Assert.Equal("usr_a", binned.DeletedByUserId);
        Assert.Equal("Alice", binned.DeletedByLabel);
        Assert.Equal("Minimal", binned.Title);

        // Already in the bin, so a second delete binned nothing and says so.
        Assert.False(await Store.SoftDeleteDocument("doc_bin", s_binNow.AddHours(1), "usr_b", "Bob"));
        Assert.Equal("usr_a", (await Store.GetBinnedDocument("doc_bin"))!.DeletedByUserId);
        Assert.False(await Store.SoftDeleteDocument("doc_missing", s_binNow, null, null));
    }

    [SkippableFact]
    public async Task Restore_brings_the_document_back_exactly_as_it_was()
    {
        Document original = FullDocument("doc_back");
        await Store.CreateDocument(original);
        await Store.AppendUpdate("doc_back", [7], s_user);
        DocumentVersion version = await Store.CreateVersion("doc_back", "keep me", s_user);
        string tag = (await Store.GetDocumentTag("doc_back"))!;
        Assert.True(await Store.SoftDeleteDocument("doc_back", s_binNow, "usr_a", "Alice"));

        Assert.Equal(RestoreOutcome.Restored, await Store.RestoreDocument("doc_back"));

        AssertSameDocument(original, await Store.GetDocument("doc_back"));
        Assert.Equal(tag, await Store.GetDocumentTag("doc_back"));
        Assert.Equal(1, (await Store.ListDocuments(new DocumentQuery())).Total);
        Assert.Equal(version.Id, Assert.Single((await Store.ListVersions("doc_back", new VersionQuery())).Items).Id);
        Assert.Single(await Store.GetUpdates("doc_back"));
        Assert.Null(await Store.GetBinnedDocument("doc_back"));
        Assert.Equal(0, (await Store.ListBin(new BinQuery())).Total);
        DocumentRow row = await Database.Documents.AsNoTracking().SingleAsync(d => d.Id == "doc_back");
        Assert.Null(row.DeletedAt);
        Assert.Null(row.DeletedByUserId);
        Assert.Null(row.DeletedByLabel);

        // Live again, so there is nothing in the bin to restore, and a missing id is the same answer.
        Assert.Equal(RestoreOutcome.NotInBin, await Store.RestoreDocument("doc_back"));
        Assert.Equal(RestoreOutcome.NotInBin, await Store.RestoreDocument("doc_missing"));
        // And it can be written again.
        Assert.Equal(ReplaceOutcome.Replaced, await Store.ReplaceDocument(MinimalDocument("doc_back", "Edited"), tag));
    }

    [SkippableFact]
    public async Task Bin_lists_newest_deletion_first_and_narrows_to_the_entries_a_user_is_party_to()
    {
        foreach (string id in new[] { "doc_a", "doc_b", "doc_c", "doc_d" })
        {
            await Store.CreateDocument(MinimalDocument(id, "Title " + id));
        }

        // a: created by usr_1, deleted by usr_2. b: created and deleted by usr_3. c: created by usr_2, deleted by usr_4.
        // d: created by nobody the audit knows (a row from before it recorded user ids), deleted by usr_4.
        await Store.AppendAudit(new AuditEntry("doc_a", s_user, AuditActions.Created, s_binNow, "usr_1"));
        await Store.AppendAudit(new AuditEntry("doc_b", s_user, AuditActions.Created, s_binNow, "usr_3"));
        await Store.AppendAudit(new AuditEntry("doc_c", s_user, AuditActions.Created, s_binNow, "usr_2"));
        await Store.AppendAudit(new AuditEntry("doc_d", s_user, AuditActions.Created, s_binNow));
        await Store.SoftDeleteDocument("doc_a", s_binNow.AddMinutes(1), "usr_2", "Two");
        await Store.SoftDeleteDocument("doc_b", s_binNow.AddMinutes(2), "usr_3", "Three");
        await Store.SoftDeleteDocument("doc_c", s_binNow.AddMinutes(3), "usr_4", "Four");
        await Store.SoftDeleteDocument("doc_d", s_binNow.AddMinutes(4), "usr_4", "Four");

        BinPage everything = await Store.ListBin(new BinQuery());
        Assert.Equal(["doc_d", "doc_c", "doc_b", "doc_a"], everything.Items.Select(b => b.Id));
        Assert.Equal(4, everything.Total);
        Assert.Equal("usr_2", everything.Items.Single(b => b.Id == "doc_c").CreatedByUserId);
        Assert.Equal("usr_1", everything.Items.Single(b => b.Id == "doc_a").CreatedByUserId);
        Assert.Null(everything.Items.Single(b => b.Id == "doc_d").CreatedByUserId);

        Assert.Equal(["doc_c", "doc_a"], (await Store.ListBin(new BinQuery("usr_2"))).Items.Select(b => b.Id));
        Assert.Equal(["doc_a"], (await Store.ListBin(new BinQuery("usr_1"))).Items.Select(b => b.Id));
        Assert.Equal(["doc_b"], (await Store.ListBin(new BinQuery("usr_3"))).Items.Select(b => b.Id));
        Assert.Equal(["doc_d", "doc_c"], (await Store.ListBin(new BinQuery("usr_4"))).Items.Select(b => b.Id));
        Assert.Empty((await Store.ListBin(new BinQuery("usr_nobody"))).Items);

        BinPage second = await Store.ListBin(new BinQuery(null, Offset: 1, Limit: 2));
        Assert.Equal(["doc_c", "doc_b"], second.Items.Select(b => b.Id));
        Assert.Equal(4, second.Total);
    }

    [SkippableFact]
    public async Task Purging_a_binned_document_removes_it_for_good_and_never_touches_a_live_one()
    {
        await Store.CreateDocument(MinimalDocument("doc_live"));
        await Store.CreateVersion("doc_live", "v", s_user);
        await Store.CreateDocument(MinimalDocument("doc_gone"));
        await Store.AppendUpdate("doc_gone", [1], s_user);
        await Store.CreateVersion("doc_gone", "v", s_user);
        await Store.AppendAudit(new AuditEntry("doc_gone", s_user, AuditActions.Created, s_binNow));

        Assert.False(await Store.PurgeBinnedDocument("doc_live"));
        Assert.NotNull(await Store.GetDocument("doc_live"));
        Assert.Single((await Store.ListVersions("doc_live", new VersionQuery())).Items);
        Assert.False(await Store.PurgeBinnedDocument("doc_gone"));
        Assert.NotNull(await Store.GetDocument("doc_gone"));

        await Store.SoftDeleteDocument("doc_gone", s_binNow, null, null);
        Assert.True(await Store.PurgeBinnedDocument("doc_gone"));

        Assert.Null(await Store.GetBinnedDocument("doc_gone"));
        Assert.Equal(RestoreOutcome.NotInBin, await Store.RestoreDocument("doc_gone"));
        Assert.Equal(0, await Database.DocumentVersions.CountAsync(v => v.DocumentId == "doc_gone"));
        Assert.Equal(0, await Database.DocumentUpdates.CountAsync(u => u.DocumentId == "doc_gone"));
        Assert.Equal(1, await Database.Audit.CountAsync(a => a.DocumentId == "doc_gone"));
        Assert.False(await Store.PurgeBinnedDocument("doc_gone"));
        Assert.Equal(1, (await Store.ListDocuments(new DocumentQuery())).Total);
    }

    [SkippableFact]
    public async Task Retention_purge_removes_only_what_has_been_in_the_bin_past_the_cutoff()
    {
        // An old document that was never deleted is not retention's business, however long ago it was last touched.
        Document ancient = MinimalDocument("doc_ancient") with { Meta = new Meta(s_binNow.AddYears(-3), s_binNow.AddYears(-3), "test") };
        await Store.CreateDocument(ancient);
        await Store.CreateDocument(MinimalDocument("doc_old_bin"));
        await Store.CreateDocument(MinimalDocument("doc_recent_bin"));
        await Store.CreateVersion("doc_old_bin", "v", s_user);
        await Store.SoftDeleteDocument("doc_old_bin", s_binNow.AddDays(-40), "usr_a", "Alice");
        await Store.SoftDeleteDocument("doc_recent_bin", s_binNow.AddDays(-5), "usr_a", "Alice");

        var maintenance = new EfStoreMaintenance(Database);
        IReadOnlyList<BinnedDocument> purged = await maintenance.PurgeBin(s_binNow.AddDays(-30));

        Assert.Equal("doc_old_bin", Assert.Single(purged).Id);
        Assert.Null(await Store.GetBinnedDocument("doc_old_bin"));
        Assert.Equal(0, await Database.DocumentVersions.CountAsync(v => v.DocumentId == "doc_old_bin"));
        Assert.NotNull(await Store.GetBinnedDocument("doc_recent_bin"));
        Assert.NotNull(await Store.GetDocument("doc_ancient"));

        // Exactly at the cutoff is not yet past it; a moment later is.
        Assert.Empty(await maintenance.PurgeBin(s_binNow.AddDays(-5)));
        Assert.Equal("doc_recent_bin", Assert.Single(await maintenance.PurgeBin(s_binNow.AddDays(-5).AddSeconds(1))).Id);
        Assert.NotNull(await Store.GetDocument("doc_ancient"));
        Assert.Empty(await maintenance.PurgeBin(s_binNow.AddYears(1)));
    }

    [SkippableFact]
    public async Task List_sorts_by_title_type_modified_and_created_in_either_direction_and_filters_by_type()
    {
        DateTimeOffset t = s_binNow;
        await Store.CreateDocument(MinimalDocument("doc_1", "banana") with { Type = DocumentType.Architecture, Meta = new Meta(t.AddDays(-3), t.AddDays(-1), "test") });
        await Store.CreateDocument(MinimalDocument("doc_2", "Cherry") with { Type = DocumentType.Flowchart, Meta = new Meta(t.AddDays(-1), t.AddDays(-3), "test") });
        await Store.CreateDocument(MinimalDocument("doc_3", "apple") with { Type = DocumentType.Flowchart, Meta = new Meta(t.AddDays(-2), t.AddDays(-2), "test") });

        async Task<string[]> Ids(DocumentQuery query) => (await Store.ListDocuments(query)).Items.Select(d => d.Id).ToArray();

        Assert.Equal(["doc_1", "doc_3", "doc_2"], await Ids(new DocumentQuery()));
        Assert.Equal(["doc_2", "doc_3", "doc_1"], await Ids(new DocumentQuery { Reverse = true }));
        Assert.Equal(["doc_2", "doc_3", "doc_1"], await Ids(new DocumentQuery { Sort = DocumentSort.Created }));
        Assert.Equal(["doc_1", "doc_3", "doc_2"], await Ids(new DocumentQuery { Sort = DocumentSort.Created, Reverse = true }));
        Assert.Equal(["doc_3", "doc_1", "doc_2"], await Ids(new DocumentQuery { Sort = DocumentSort.Title }));
        Assert.Equal(["doc_2", "doc_1", "doc_3"], await Ids(new DocumentQuery { Sort = DocumentSort.Title, Reverse = true }));
        Assert.Equal(["doc_1", "doc_3", "doc_2"], await Ids(new DocumentQuery { Sort = DocumentSort.Type }));
        Assert.Equal(["doc_2", "doc_3", "doc_1"], await Ids(new DocumentQuery { Sort = DocumentSort.Type, Reverse = true }));
        Assert.Equal(["doc_3", "doc_2"], await Ids(new DocumentQuery { Type = DocumentType.Flowchart, Sort = DocumentSort.Title }));
        Assert.Equal(["doc_1"], await Ids(new DocumentQuery { Type = DocumentType.Architecture }));
        Assert.Empty(await Ids(new DocumentQuery { Type = DocumentType.Sequence }));

        // A page of a sorted list is a slice of the same order, and search still narrows it.
        Assert.Equal(["doc_1", "doc_2"], await Ids(new DocumentQuery(Offset: 1, Limit: 2) { Sort = DocumentSort.Title }));
        Assert.Equal(["doc_2"], await Ids(new DocumentQuery("cherry") { Sort = DocumentSort.Title }));
    }

    [SkippableFact]
    public async Task Version_listing_reports_the_size_of_each_snapshot()
    {
        await Store.CreateDocument(FullDocument("doc_size"));
        DocumentVersion version = await Store.CreateVersion("doc_size", "big", s_user);

        VersionSummary summary = Assert.Single((await Store.ListVersions("doc_size", new VersionQuery())).Items);

        Assert.Equal(version.Id, summary.Id);
        Assert.True(summary.Size > 100, $"a full document's snapshot is well over 100 characters, got {summary.Size}");
        // The size is that of the stored text, so two versions of different documents differ and an empty one is small.
        await Store.CreateDocument(MinimalDocument("doc_small"));
        await Store.CreateVersion("doc_small", "small", s_user);
        VersionSummary small = Assert.Single((await Store.ListVersions("doc_small", new VersionQuery())).Items);
        Assert.InRange(small.Size, 1, summary.Size - 1);
    }

    protected static Document MinimalDocument(string id, string title = "Minimal")
    {
        DateTimeOffset now = new(2026, 9, 20, 22, 0, 0, TimeSpan.Zero);
        return new Document(id, Document.CurrentSchemaVersion, title, DocumentType.Flowchart, [], [], new Meta(now, now, "test"));
    }

    /// <summary>Exercises every optional member so a provider that drops one is caught.</summary>
    protected static Document FullDocument(string id)
    {
        Document document = MinimalDocument(id, "Full \"quoted\" title with ünïcode") with
        {
            Theme = new Dictionary<string, string> { ["node.fill"] = "#FFFFFF", ["edge.stroke"] = "#333" },
            Flythrough = new Flythrough(
            [
                new Keyframe("k_1", new ViewRect(-10.5, 0, 640, 400)) { Caption = "The \"whole\" lane", DurationMs = 1200.5, HoldMs = 0, Easing = KeyframeEasing.EaseOut },
                new Keyframe("k_2", new ViewRect(10.25, 10, 200, 120)),
            ])
            {
                Loop = true,
            },
            Nodes =
            [
                new Node("c_1", "container", "Lane", "rectangle") { Geometry = new Geometry(0, 0, 400, 200) },
                new Node("n_1", "process", "Step", "rounded")
                {
                    Geometry = new Geometry(10.5, 10.25, 100, 50) { Rotation = 15 },
                    Parent = "c_1",
                    Style = new NodeStyle { Fill = "#FFF", Dash = Dash.Dashed, StrokeWidth = 2, Font = "Inter" },
                    Ports = [new Port("out", 1, 0.5)],
                    Props = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["owner"] = System.Text.Json.JsonDocument.Parse("\"team-a\"").RootElement,
                        ["tags"] = System.Text.Json.JsonDocument.Parse("[\"x\",1,true]").RootElement,
                    },
                },
                new Node("n_2", "decision", "Ok?", "diamond") { Geometry = new Geometry(200, 10, 100, 100), Parent = "c_1" },
            ],
            Edges =
            [
                new Edge("e_1", "flow", new Endpoint("n_1") { Port = "out" }, new Endpoint("n_2"))
                {
                    Label = "next",
                    Routing = new Routing(RoutingMode.Orthogonal) { Waypoints = [new Point(150, 35)] },
                    Style = new EdgeStyle { ArrowEnd = ArrowHead.Arrow, Dash = Dash.Dotted },
                },
            ],
        };
        return document with
        {
            Meta = document.Meta with
            {
                AiProvenance = [new AiProvenance("claude-code", document.Meta.UpdatedAt, AiAction.Created, ["n_1", "e_1"]) { Model = "claude-fable-5-1" }],
            },
        };
    }
}
