using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Storage.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Draughtsman.Storage;

/// <summary>
/// <see cref="IStore"/> over EF Core. Everything here is lowest-common-denominator SQL that both providers translate
/// (ADR 0003, rule 6); the shared provider suite is what keeps it that way. One instance per request scope.
/// </summary>
public sealed class EfStore(DraughtsmanDbContext db, StorageOptions options) : IStore
{
    private const int AppendRetries = 3;

    private const int LabelLength = 256;

    private readonly DraughtsmanDbContext _db = db;

    public string ProviderName { get; } = options.ProviderName;

    public async Task<DocumentPage> ListDocuments(DocumentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IQueryable<DocumentRow> rows = Live().Where(d => d.Workspace == query.Workspace);
        if (query.Type is { } type)
        {
            string typeName = TypeName(type);
            rows = rows.Where(d => d.Type == typeName);
        }

        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            // Both sides are folded by .NET: the databases' own lower() differs (SQLite folds ASCII only), title_search does not.
            string pattern = "%" + EscapeLike(SearchTitle(query.Query.Trim())) + "%";
            rows = rows.Where(d => EF.Functions.Like(d.TitleSearch, pattern, "\\"));
        }

        int total = await rows.CountAsync(cancellationToken);
        List<DocumentSummary> items = await Order(rows, query)
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(d => new DocumentSummary(d.Id, d.Title, ParseType(d.Type), d.Workspace, FromUtc(d.CreatedAt), FromUtc(d.UpdatedAt)))
            .ToListAsync(cancellationToken);
        return new DocumentPage(items, total, query.Offset, query.Limit);
    }

    public async Task<Document?> GetDocument(string id, CancellationToken cancellationToken = default)
    {
        string? snapshot = await Live()
            .Where(d => d.Id == id)
            .Select(d => d.Snapshot)
            .SingleOrDefaultAsync(cancellationToken);
        return snapshot is null ? null : DocumentJson.Deserialize(snapshot);
    }

    public async Task CreateDocument(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (await _db.Documents.AnyAsync(d => d.Id == document.Id, cancellationToken))
        {
            throw new DocumentExistsException(document.Id);
        }

        string snapshot = DocumentJson.Serialize(document);
        var row = new DocumentRow
        {
            Id = document.Id,
            Title = document.Title,
            TitleSearch = SearchTitle(document.Title),
            Type = TypeName(document.Type),
            Workspace = DocumentQuery.DefaultWorkspace,
            Snapshot = snapshot,
            Etag = DocumentVersionTag.OfFreshSnapshot(snapshot),
            SchemaVersion = document.SchemaVersion,
            CreatedAt = ToUtc(document.Meta.CreatedAt),
            UpdatedAt = ToUtc(document.Meta.UpdatedAt),
        };
        _db.Documents.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            // Lost a race with a concurrent create of the same id; the caller sees the same outcome as the check above.
            _db.Entry(row).State = EntityState.Detached;
            throw new DocumentExistsException(document.Id);
        }
    }

    public async Task<string?> GetDocumentTag(string id, CancellationToken cancellationToken = default)
    {
        // The whole of a conditional read or a save's version check: one narrow read of the stored tag, never the snapshot.
        var row = await Live()
            .Where(d => d.Id == id)
            .Select(d => new { d.Etag })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        return row.Etag ?? (await GetStoredDocument(id, cancellationToken))?.Version;
    }

    public async Task<StoredDocument?> GetStoredDocument(string id, CancellationToken cancellationToken = default)
    {
        var row = await Live()
            .Where(d => d.Id == id)
            .Select(d => new { d.Snapshot, d.Etag })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        Document document = DocumentJson.Deserialize(row.Snapshot);
        if (row.Etag is not null)
        {
            return new StoredDocument(document, row.Etag);
        }

        // A row written before the etag column existed. Its tag is what a read always handed out, the fingerprint of the
        // model's serialisation, and it is stored now, once, so the next poll takes the cheap path. The UPDATE only
        // fills a still-empty tag on the very snapshot it was computed from, so it cannot overwrite a newer save's.
        string tag = DocumentVersionTag.Compute(document);
        await _db.Documents
            .Where(d => d.Id == id && d.DeletedAt == null && d.Etag == null && d.Snapshot == row.Snapshot)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.Etag, tag), cancellationToken);
        _db.ChangeTracker.Clear();
        return new StoredDocument(document, tag);
    }

    public async Task<ReplaceOutcome> ReplaceDocument(Document document, string expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        string? stored = await GetDocumentTag(document.Id, cancellationToken);
        if (stored is null)
        {
            return ReplaceOutcome.NotFound;
        }

        if (stored != expectedVersion)
        {
            return ReplaceOutcome.VersionMismatch;
        }

        // One UPDATE, and a guarded one carries the tag it was checked against in its WHERE clause, so a write that
        // lands between the read above and this statement takes the row out of the update rather than being
        // overwritten by it. Every write moves the tag (it fingerprints the snapshot, and updatedAt is restamped).
        // Lowest-common-denominator SQL: both providers translate it (ADR 0003, rule 6).
        IQueryable<DocumentRow> target = _db.Documents.Where(d => d.Id == document.Id && d.DeletedAt == null && d.Etag == stored);

        string snapshot = DocumentJson.Serialize(document);
        string etag = DocumentVersionTag.OfFreshSnapshot(snapshot);
        string title = document.Title;
        string titleSearch = SearchTitle(title);
        string type = TypeName(document.Type);
        int schemaVersion = document.SchemaVersion;
        DateTime createdAt = ToUtc(document.Meta.CreatedAt);
        DateTime updatedAt = ToUtc(document.Meta.UpdatedAt);
        int updated = await target.ExecuteUpdateAsync(
            row => row
                .SetProperty(d => d.Title, title)
                .SetProperty(d => d.TitleSearch, titleSearch)
                .SetProperty(d => d.Type, type)
                .SetProperty(d => d.Snapshot, snapshot)
                .SetProperty(d => d.Etag, etag)
                .SetProperty(d => d.SchemaVersion, schemaVersion)
                .SetProperty(d => d.CreatedAt, createdAt)
                .SetProperty(d => d.UpdatedAt, updatedAt),
            cancellationToken);

        // Bulk updates bypass the change tracker; drop any row it still holds so later reads in this scope see the write.
        _db.ChangeTracker.Clear();
        if (updated > 0)
        {
            return ReplaceOutcome.Replaced;
        }

        // Nothing matched: either the row went in the meantime, or a guarded write lost the race for it.
        bool exists = await Live().AnyAsync(d => d.Id == document.Id, cancellationToken);
        return exists ? ReplaceOutcome.VersionMismatch : ReplaceOutcome.NotFound;
    }

    public Task<bool> DeleteDocument(string id, CancellationToken cancellationToken = default)
    {
        return DeleteDocumentRows(_db, id, binnedOnly: false, cancellationToken);
    }

    public Task<bool> PurgeBinnedDocument(string id, CancellationToken cancellationToken = default)
    {
        return DeleteDocumentRows(_db, id, binnedOnly: true, cancellationToken);
    }

    /// <summary>
    /// The permanent removal, shared with the retention pass (<see cref="EfStoreMaintenance.PurgeBin"/>). With
    /// <paramref name="binnedOnly"/> it first takes the row with a guarded no-op UPDATE inside the transaction: a document
    /// restored meanwhile no longer matches, so it is skipped rather than stripped of its versions, and a restore that
    /// arrives mid-purge waits on that row lock and then finds nothing in the bin.
    /// </summary>
    internal static async Task<bool> DeleteDocumentRows(DraughtsmanDbContext db, string id, bool binnedOnly, CancellationToken cancellationToken)
    {
        // Explicit child deletes rather than relying on ON DELETE CASCADE, which SQLite only honours with foreign keys on.
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (binnedOnly)
        {
            int held = await db.Documents
                .Where(d => d.Id == id && d.DeletedAt != null)
                .ExecuteUpdateAsync(row => row.SetProperty(d => d.DeletedAt, d => d.DeletedAt), cancellationToken);
            if (held == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                return false;
            }
        }

        await db.DocumentUpdates.Where(u => u.DocumentId == id).ExecuteDeleteAsync(cancellationToken);
        await db.DocumentVersions.Where(v => v.DocumentId == id).ExecuteDeleteAsync(cancellationToken);
        int deleted = await db.Documents.Where(d => d.Id == id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // Bulk deletes bypass the change tracker; drop any row it still holds so the same id can be created again in this scope.
        db.ChangeTracker.Clear();
        return deleted > 0;
    }

    public async Task<bool> SoftDeleteDocument(string id, DateTimeOffset at, string? deletedByUserId, string? deletedByLabel, CancellationToken cancellationToken = default)
    {
        DateTime when = ToUtc(at);
        string? label = deletedByLabel is null ? null : Truncate(deletedByLabel, LabelLength);
        // One guarded UPDATE: only a live row is taken, so two deletes racing each other bin it once and report it once.
        int updated = await _db.Documents
            .Where(d => d.Id == id && d.DeletedAt == null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(d => d.DeletedAt, when)
                    .SetProperty(d => d.DeletedByUserId, deletedByUserId)
                    .SetProperty(d => d.DeletedByLabel, label),
                cancellationToken);
        _db.ChangeTracker.Clear();
        return updated > 0;
    }

    public async Task<RestoreOutcome> RestoreDocument(string id, CancellationToken cancellationToken = default)
    {
        int updated = await _db.Documents
            .Where(d => d.Id == id && d.DeletedAt != null)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(d => d.DeletedAt, (DateTime?)null)
                    .SetProperty(d => d.DeletedByUserId, (string?)null)
                    .SetProperty(d => d.DeletedByLabel, (string?)null),
                cancellationToken);
        _db.ChangeTracker.Clear();
        return updated > 0 ? RestoreOutcome.Restored : RestoreOutcome.NotInBin;
    }

    public async Task<BinPage> ListBin(BinQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        IQueryable<DocumentRow> rows = _db.Documents.AsNoTracking().Where(d => d.DeletedAt != null);
        if (query.InvolvingUserId is { } userId)
        {
            // Theirs when they deleted it, or when the audit trail says they created it.
            rows = rows.Where(d => d.DeletedByUserId == userId
                || _db.Audit.Any(a => a.DocumentId == d.Id && a.Action == AuditActions.Created && a.ActorUserId == userId));
        }

        int total = await rows.CountAsync(cancellationToken);
        List<BinRow> page = await BinRows(rows.OrderByDescending(d => d.DeletedAt).ThenBy(d => d.Id).Skip(query.Offset).Take(query.Limit))
            .ToListAsync(cancellationToken);
        return new BinPage(page.Select(ToBinned).ToList(), total, query.Offset, query.Limit);
    }

    public async Task<BinnedDocument?> GetBinnedDocument(string id, CancellationToken cancellationToken = default)
    {
        BinRow? row = await BinRows(_db.Documents.AsNoTracking().Where(d => d.DeletedAt != null && d.Id == id))
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToBinned(row);
    }

    public async Task<Document?> GetBinnedSnapshot(string id, CancellationToken cancellationToken = default)
    {
        string? snapshot = await _db.Documents.AsNoTracking()
            .Where(d => d.DeletedAt != null && d.Id == id)
            .Select(d => d.Snapshot)
            .SingleOrDefaultAsync(cancellationToken);
        return snapshot is null ? null : DocumentJson.Deserialize(snapshot);
    }

    private IQueryable<BinRow> BinRows(IQueryable<DocumentRow> rows)
    {
        return BinRows(rows, _db);
    }

    /// <summary>The bin projection, with the creating user taken from the audit trail's first <c>created</c> row.</summary>
    internal static IQueryable<BinRow> BinRows(IQueryable<DocumentRow> rows, DraughtsmanDbContext db)
    {
        return rows.Select(d => new BinRow
        {
            Id = d.Id,
            Title = d.Title,
            Type = d.Type,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
            DeletedAt = d.DeletedAt,
            DeletedByUserId = d.DeletedByUserId,
            DeletedByLabel = d.DeletedByLabel,
            CreatedByUserId = db.Audit
                .Where(a => a.DocumentId == d.Id && a.Action == AuditActions.Created)
                .OrderBy(a => a.Id)
                .Select(a => a.ActorUserId)
                .FirstOrDefault(),
        });
    }

    internal static BinnedDocument ToBinned(BinRow row)
    {
        return new BinnedDocument(row.Id, row.Title, ParseType(row.Type), FromUtc(row.CreatedAt), FromUtc(row.UpdatedAt), FromUtc(row.DeletedAt!.Value), row.DeletedByUserId, row.DeletedByLabel, row.CreatedByUserId);
    }

    /// <summary>A bin row as read from the database, before its timestamps and type are given their model types.</summary>
    internal sealed class BinRow
    {
        public string Id { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public DateTime CreatedAt { get; init; }

        public DateTime UpdatedAt { get; init; }

        public DateTime? DeletedAt { get; init; }

        public string? DeletedByUserId { get; init; }

        public string? DeletedByLabel { get; init; }

        public string? CreatedByUserId { get; init; }
    }

    public async Task<RenameOutcome> RenameDocument(string id, string newTitle, string expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newTitle);
        StoredDocument? stored = await GetStoredDocument(id, cancellationToken);
        if (stored is null)
        {
            return RenameOutcome.NotFound;
        }

        if (stored.Version != expectedVersion)
        {
            return RenameOutcome.VersionMismatch;
        }

        // Only the title and updatedAt change; the graph, theme and creation metadata stay as they were. The snapshot
        // is regenerated through the model so the version tag moves, exactly as a replace would.
        Document renamed = stored.Document with
        {
            Title = newTitle,
            Meta = stored.Document.Meta with { UpdatedAt = DateTimeOffset.UtcNow },
        };

        IQueryable<DocumentRow> target = _db.Documents.Where(d => d.Id == id && d.DeletedAt == null && d.Etag == stored.Version);
        string snapshot = DocumentJson.Serialize(renamed);
        string etag = DocumentVersionTag.OfFreshSnapshot(snapshot);
        int updated = await target.ExecuteUpdateAsync(
            row => row
                .SetProperty(d => d.Title, newTitle)
                .SetProperty(d => d.TitleSearch, SearchTitle(newTitle))
                .SetProperty(d => d.Snapshot, snapshot)
                .SetProperty(d => d.Etag, etag)
                .SetProperty(d => d.UpdatedAt, ToUtc(renamed.Meta.UpdatedAt)),
            cancellationToken);
        _db.ChangeTracker.Clear();
        if (updated > 0)
        {
            return RenameOutcome.Renamed;
        }

        bool exists = await Live().AnyAsync(d => d.Id == id, cancellationToken);
        return exists ? RenameOutcome.VersionMismatch : RenameOutcome.NotFound;
    }

    public async Task<long> AppendUpdate(string documentId, byte[] update, Actor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(actor);

        // Seq is max+1 inside a transaction; a concurrent append on Postgres surfaces as a key violation, so retry a few times.
        for (int attempt = 1; ; attempt++)
        {
            await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            if (!await _db.Documents.AnyAsync(d => d.Id == documentId && d.DeletedAt == null, cancellationToken))
            {
                throw new DocumentNotFoundException(documentId);
            }

            long last = await _db.DocumentUpdates
                .Where(u => u.DocumentId == documentId)
                .Select(u => (long?)u.Seq)
                .MaxAsync(cancellationToken) ?? 0;
            var row = new DocumentUpdateRow
            {
                DocumentId = documentId,
                Seq = last + 1,
                Update = update,
                CreatedAt = ToUtc(DateTimeOffset.UtcNow),
                Actor = actor.ToString(),
            };
            _db.DocumentUpdates.Add(row);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return row.Seq;
            }
            catch (DbUpdateException error) when (attempt < AppendRetries && IsUniqueViolation(error))
            {
                _db.Entry(row).State = EntityState.Detached;
                await transaction.RollbackAsync(cancellationToken);
            }
        }
    }

    public async Task<IReadOnlyList<DocumentUpdate>> GetUpdates(string documentId, long afterSeq = 0, CancellationToken cancellationToken = default)
    {
        List<DocumentUpdateRow> rows = await _db.DocumentUpdates.AsNoTracking()
            .Where(u => u.DocumentId == documentId && u.Seq > afterSeq)
            .OrderBy(u => u.Seq)
            .ToListAsync(cancellationToken);
        return rows.Select(u => new DocumentUpdate(u.DocumentId, u.Seq, u.Update, FromUtc(u.CreatedAt), Actor.Parse(u.Actor))).ToList();
    }

    public async Task<VersionPage> ListVersions(string documentId, VersionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await Live().AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return new VersionPage([], 0, query.Offset, query.Limit);
        }

        IQueryable<DocumentVersionRow> rows = _db.DocumentVersions.AsNoTracking().Where(v => v.DocumentId == documentId);
        int total = await rows.CountAsync(cancellationToken);
        var page = await rows
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id) // Version ids are time-ordered (StoreIds), so this settles same-millisecond ties.
            .Skip(query.Offset)
            .Take(query.Limit)
            .Select(v => new { v.Id, v.DocumentId, v.Name, v.CreatedAt, v.Actor, v.Reason, Size = v.Snapshot.Length })
            .ToListAsync(cancellationToken);
        List<VersionSummary> items = page
            .Select(v => new VersionSummary(v.Id, v.DocumentId, v.Name, FromUtc(v.CreatedAt), Actor.Parse(v.Actor), v.Reason, v.Size))
            .ToList();
        return new VersionPage(items, total, query.Offset, query.Limit);
    }

    public async Task<DocumentVersion?> GetVersion(string documentId, string versionId, CancellationToken cancellationToken = default)
    {
        if (!await Live().AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return null;
        }

        DocumentVersionRow? row = await _db.DocumentVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.DocumentId == documentId && v.Id == versionId, cancellationToken);
        return row is null ? null : ToVersion(row);
    }

    public async Task<DocumentVersion> CreateVersion(string documentId, string name, Actor actor, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(actor);

        string? snapshot = await Live()
            .Where(d => d.Id == documentId)
            .Select(d => d.Snapshot)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new DocumentNotFoundException(documentId);
        var row = new DocumentVersionRow
        {
            Id = StoreIds.NewVersionId(),
            DocumentId = documentId,
            Name = name,
            Snapshot = snapshot,
            CreatedAt = ToUtc(DateTimeOffset.UtcNow),
            Actor = actor.ToString(),
            Reason = reason,
        };
        _db.DocumentVersions.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsForeignKeyViolation(error))
        {
            // A concurrent delete took the document between the snapshot read and this insert.
            _db.Entry(row).State = EntityState.Detached;
            throw new DocumentNotFoundException(documentId);
        }

        return ToVersion(row);
    }

    public async Task<AuditEntry> AppendAudit(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var row = new AuditRow
        {
            DocumentId = entry.DocumentId,
            Actor = entry.Actor.Name,
            ActorKind = entry.Actor.Kind == ActorKind.Agent ? "agent" : "user",
            Model = entry.Actor.Model,
            Action = entry.Action,
            At = ToUtc(entry.At),
            ActorUserId = entry.ActorUserId,
        };
        _db.Audit.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return entry with { Id = row.Id, At = FromUtc(row.At) };
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAudit(string documentId, CancellationToken cancellationToken = default)
    {
        List<AuditRow> rows = await _db.Audit.AsNoTracking()
            .Where(a => a.DocumentId == documentId)
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(a => new AuditEntry(a.DocumentId, new Actor(a.ActorKind == "agent" ? ActorKind.Agent : ActorKind.User, a.Actor, a.Model), a.Action, FromUtc(a.At), a.ActorUserId) { Id = a.Id }).ToList();
    }

    public async Task<AssetInfo?> GetAsset(string id, CancellationToken cancellationToken = default)
    {
        AssetRow? row = await _db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return row is null ? null : ToAsset(row);
    }

    public async Task<AssetInfo?> FindAssetByHash(string hash, CancellationToken cancellationToken = default)
    {
        AssetRow? row = await _db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Hash == hash, cancellationToken);
        return row is null ? null : ToAsset(row);
    }

    public async Task<AssetInfo> CreateAsset(AssetInfo asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var row = new AssetRow
        {
            Id = asset.Id,
            Hash = asset.Hash,
            ContentType = asset.ContentType,
            Path = asset.Path,
            Size = asset.Size,
            CreatedAt = ToUtc(asset.CreatedAt),
        };
        _db.Assets.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            // Lost a race with a concurrent upload of the same bytes; the caller looks the existing asset up by hash.
            _db.Entry(row).State = EntityState.Detached;
            throw new AssetExistsException(asset.Hash);
        }

        return asset;
    }

    /// <summary>Documents that are not in the bin: what every read, write and listing works on.</summary>
    private IQueryable<DocumentRow> Live()
    {
        return _db.Documents.AsNoTracking().Where(d => d.DeletedAt == null);
    }

    /// <summary>The list's order: the column asked for (its natural direction, flipped by Reverse), then id, so pages never reshuffle.</summary>
    private static IOrderedQueryable<DocumentRow> Order(IQueryable<DocumentRow> rows, DocumentQuery query)
    {
        bool reverse = query.Reverse;
        IOrderedQueryable<DocumentRow> ordered = query.Sort switch
        {
            DocumentSort.Title => reverse ? rows.OrderByDescending(d => d.TitleSearch) : rows.OrderBy(d => d.TitleSearch),
            DocumentSort.Type => reverse ? rows.OrderByDescending(d => d.Type).ThenByDescending(d => d.TitleSearch) : rows.OrderBy(d => d.Type).ThenBy(d => d.TitleSearch),
            DocumentSort.Created => reverse ? rows.OrderBy(d => d.CreatedAt) : rows.OrderByDescending(d => d.CreatedAt),
            _ => reverse ? rows.OrderBy(d => d.UpdatedAt) : rows.OrderByDescending(d => d.UpdatedAt),
        };
        return ordered.ThenBy(d => d.Id);
    }

    private static string Truncate(string text, int length)
    {
        return text.Length <= length ? text : text[..length];
    }

    /// <summary>UTC, truncated to milliseconds: the precision the JSON form carries, so every provider round-trips the same value.</summary>
    internal static DateTime ToUtc(DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    internal static DateTimeOffset FromUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static string TypeName(DocumentType type)
    {
        return type.ToString().ToLowerInvariant();
    }

    private static DocumentType ParseType(string name)
    {
        return Enum.Parse<DocumentType>(name, ignoreCase: true);
    }

    /// <summary>The value stored in title_search, so the fold is .NET's on every provider rather than the database's.</summary>
    private static string SearchTitle(string title)
    {
        return title.ToLowerInvariant();
    }

    /// <summary>Only a key clash means the id is taken; every other save failure has to reach the caller as itself.</summary>
    private static bool IsUniqueViolation(DbUpdateException error)
    {
        const int SqliteConstraint = 19;
        return error.InnerException switch
        {
            SqliteException sqlite => sqlite.SqliteErrorCode == SqliteConstraint,
            PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation,
            _ => false,
        };
    }

    /// <summary>A child row whose document has just been deleted; the contract reports that as the document being gone.</summary>
    private static bool IsForeignKeyViolation(DbUpdateException error)
    {
        const int SqliteConstraintForeignKey = 787;
        return error.InnerException switch
        {
            SqliteException sqlite => sqlite.SqliteExtendedErrorCode == SqliteConstraintForeignKey,
            PostgresException postgres => postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation,
            _ => false,
        };
    }

    private static string EscapeLike(string text)
    {
        return text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static DocumentVersion ToVersion(DocumentVersionRow row)
    {
        return new DocumentVersion(row.Id, row.DocumentId, row.Name, FromUtc(row.CreatedAt), Actor.Parse(row.Actor), row.Reason, DocumentJson.Deserialize(row.Snapshot));
    }

    private static AssetInfo ToAsset(AssetRow row)
    {
        return new AssetInfo(row.Id, row.Hash, row.ContentType, row.Size, row.Path, FromUtc(row.CreatedAt));
    }
}
