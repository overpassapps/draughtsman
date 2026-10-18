using Draughtsman.Core.Models;
using Draughtsman.Core.Storage;

namespace Draughtsman.Mcp.Tests;

/// <summary>A minimal in-memory <see cref="IStore"/> so the tool tests exercise real tool logic without EF or SQLite.</summary>
public sealed class FakeStore : IStore
{
    private readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DocumentVersion>> _versions = new(StringComparer.Ordinal);
    private readonly List<AuditEntry> _audit = [];
    private long _nextAuditId = 1;
    private int _nextVersionId = 1;

    public string ProviderName => "fake";

    public Task<DocumentPage> ListDocuments(DocumentQuery query, CancellationToken cancellationToken = default)
    {
        IEnumerable<Document> matches = _documents.Values;
        if (!string.IsNullOrEmpty(query.Query))
        {
            matches = matches.Where(d => d.Title.Contains(query.Query, StringComparison.OrdinalIgnoreCase));
        }

        List<Document> ordered = matches.OrderByDescending(d => d.Meta.UpdatedAt).ToList();
        List<DocumentSummary> page = ordered.Skip(query.Offset).Take(query.Limit)
            .Select(d => new DocumentSummary(d.Id, d.Title, d.Type, query.Workspace, d.Meta.CreatedAt, d.Meta.UpdatedAt))
            .ToList();
        return Task.FromResult(new DocumentPage(page, ordered.Count, query.Offset, query.Limit));
    }

    public Task<Document?> GetDocument(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_documents.GetValueOrDefault(id));
    }

    public Task CreateDocument(Document document, CancellationToken cancellationToken = default)
    {
        if (!_documents.TryAdd(document.Id, document))
        {
            throw new DocumentExistsException(document.Id);
        }

        return Task.CompletedTask;
    }

    // The bin is a storage-provider matter (StoreContractTests); the tool tests never put a document in it.
    public Task<bool> SoftDeleteDocument(string id, DateTimeOffset at, string? deletedByUserId, string? deletedByLabel, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<RestoreOutcome> RestoreDocument(string id, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<bool> PurgeBinnedDocument(string id, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<BinPage> ListBin(BinQuery query, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<BinnedDocument?> GetBinnedDocument(string id, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<Document?> GetBinnedSnapshot(string id, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<ReplaceOutcome> ReplaceDocument(Document document, string expectedVersion, CancellationToken cancellationToken = default)
    {
        if (!_documents.TryGetValue(document.Id, out Document? stored))
        {
            return Task.FromResult(ReplaceOutcome.NotFound);
        }

        if (DocumentVersionTag.Compute(stored) != expectedVersion)
        {
            return Task.FromResult(ReplaceOutcome.VersionMismatch);
        }

        _documents[document.Id] = document;
        return Task.FromResult(ReplaceOutcome.Replaced);
    }

    public Task<bool> DeleteDocument(string id, CancellationToken cancellationToken = default)
    {
        _versions.Remove(id);
        return Task.FromResult(_documents.Remove(id));
    }

    public Task<RenameOutcome> RenameDocument(string id, string newTitle, string expectedVersion, CancellationToken cancellationToken = default)
    {
        if (!_documents.TryGetValue(id, out Document? stored))
        {
            return Task.FromResult(RenameOutcome.NotFound);
        }

        if (DocumentVersionTag.Compute(stored) != expectedVersion)
        {
            return Task.FromResult(RenameOutcome.VersionMismatch);
        }

        _documents[id] = stored with { Title = newTitle, Meta = stored.Meta with { UpdatedAt = DateTimeOffset.UtcNow } };
        return Task.FromResult(RenameOutcome.Renamed);
    }

    public Task<long> AppendUpdate(string documentId, byte[] update, Actor actor, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("FakeStore does not model the Yjs update log; the MCP tools never call this.");
    }

    public Task<IReadOnlyList<DocumentUpdate>> GetUpdates(string documentId, long afterSeq = 0, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<DocumentUpdate>>([]);
    }

    public Task<VersionPage> ListVersions(string documentId, VersionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<VersionSummary> all = _versions.GetValueOrDefault(documentId, [])
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => v.ToSummary())
            .ToList();
        List<VersionSummary> page = all.Skip(query.Offset).Take(query.Limit).ToList();
        return Task.FromResult(new VersionPage(page, all.Count, query.Offset, query.Limit));
    }

    public Task<DocumentVersion?> GetVersion(string documentId, string versionId, CancellationToken cancellationToken = default)
    {
        DocumentVersion? version = _versions.GetValueOrDefault(documentId, []).FirstOrDefault(v => v.Id == versionId);
        return Task.FromResult(version);
    }

    public Func<string, Task>? OnBeforeCreateVersionAsync { get; set; }

    public Task<DocumentVersion> CreateVersion(string documentId, string name, Actor actor, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (OnBeforeCreateVersionAsync is not null)
        {
            OnBeforeCreateVersionAsync(documentId).GetAwaiter().GetResult();
        }

        if (!_documents.TryGetValue(documentId, out Document? document))
        {
            throw new DocumentNotFoundException(documentId);
        }

        var version = new DocumentVersion($"v_{_nextVersionId++}", documentId, name, DateTimeOffset.UtcNow, actor, reason, document);
        List<DocumentVersion> list = _versions.TryGetValue(documentId, out List<DocumentVersion>? existing) ? existing : _versions[documentId] = [];
        list.Add(version);
        return Task.FromResult(version);
    }

    public Task<AuditEntry> AppendAudit(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        AuditEntry stored = entry with { Id = _nextAuditId++ };
        _audit.Add(stored);
        return Task.FromResult(stored);
    }

    public Task<IReadOnlyList<AuditEntry>> ListAudit(string documentId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<AuditEntry>>(_audit.Where(a => a.DocumentId == documentId).ToList());
    }

    private readonly Dictionary<string, AssetInfo> _assets = new(StringComparer.Ordinal);

    public Task<AssetInfo?> GetAsset(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_assets.GetValueOrDefault(id));
    }

    public Task<AssetInfo?> FindAssetByHash(string hash, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_assets.Values.FirstOrDefault(a => a.Hash == hash));
    }

    public Task<AssetInfo> CreateAsset(AssetInfo asset, CancellationToken cancellationToken = default)
    {
        if (_assets.Values.Any(a => a.Hash == asset.Hash))
        {
            throw new AssetExistsException(asset.Hash);
        }

        _assets[asset.Id] = asset;
        return Task.FromResult(asset);
    }
}
