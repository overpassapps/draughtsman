using Draughtsman.Core.Models;

namespace Draughtsman.Core.Storage;

/// <summary>
/// Storage provider contract. The server, the sync hub and the MCP layer talk to this and nothing else, so a provider
/// is a driver, not a rewrite (ADR 0003). Every method is exercised by the shared provider suite in
/// tests/Draughtsman.Storage.Tests, which is the specification of provider behaviour.
/// Yjs updates are opaque byte arrays; the store never decodes them (ADR 0002).
/// Composition (a write that also records a version and an audit row) belongs to the caller, not the store.
/// </summary>
public interface IStore
{
    /// <summary>Short provider name reported by /api/health, e.g. sqlite, postgres.</summary>
    string ProviderName { get; }

    Task<DocumentPage> ListDocuments(DocumentQuery query, CancellationToken cancellationToken = default);

    Task<Document?> GetDocument(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The <see cref="DocumentVersionTag"/> of the document as stored, or null when there is no such document: the
    /// base version a save must quote and the value a conditional read compares (AU-16). A provider that stores the
    /// tag answers from that column without touching the snapshot, which is what keeps a 1 Hz live-sync poll cheap;
    /// the default is the slow, always-correct computation through the model.
    /// </summary>
    async Task<string?> GetDocumentTag(string id, CancellationToken cancellationToken = default)
    {
        Document? document = await GetDocument(id, cancellationToken);
        return document is null ? null : DocumentVersionTag.Compute(document);
    }

    /// <summary>
    /// The document and its version tag, read together so the tag is the one of exactly the snapshot returned, not of
    /// one a concurrent save wrote between two reads. Null when there is no such document.
    /// </summary>
    async Task<StoredDocument?> GetStoredDocument(string id, CancellationToken cancellationToken = default)
    {
        Document? document = await GetDocument(id, cancellationToken);
        return document is null ? null : new StoredDocument(document, DocumentVersionTag.Compute(document));
    }

    /// <summary>Inserts a new document. Throws <see cref="DocumentExistsException"/> when the id is taken.</summary>
    Task CreateDocument(Document document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored snapshot of an existing document. <paramref name="expectedVersion"/> is the
    /// <see cref="DocumentVersionTag"/> of the snapshot the caller read before composing this one: the write lands
    /// only while the stored snapshot still has that tag, so a save from a stale base cannot overwrite a write that
    /// arrived in between. Every replacement requires this base version.
    /// The check and the write are one statement, so no write can slip between them.
    /// </summary>
    Task<ReplaceOutcome> ReplaceDocument(Document document, string expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the document, its update log and its versions for good, bin or not. Audit rows stay. Returns false when no
    /// document had that id. Nothing on a request path calls this for a live document: a person's delete goes to the bin
    /// (<see cref="SoftDeleteDocument"/>) and only the explicit second step, or the retention pass, reaches here.
    /// </summary>
    Task<bool> DeleteDocument(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a live document to the bin (OP-02): it keeps its snapshot, updates and versions but every read, listing and
    /// write treats it as gone until <see cref="RestoreDocument"/>. Returns false when no live document had that id
    /// (an already-binned one included), so a double delete is not reported twice.
    /// </summary>
    Task<bool> SoftDeleteDocument(string id, DateTimeOffset at, string? deletedByUserId, string? deletedByLabel, CancellationToken cancellationToken = default);

    /// <summary>Takes a document out of the bin, exactly as it was when deleted (its version tag and updatedAt are untouched).</summary>
    Task<RestoreOutcome> RestoreDocument(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The explicit second step: removes a document that is in the bin, for good, with its update log and versions. Returns
    /// false when it is not in the bin, a live document included: this can never destroy something that was restored in the
    /// meantime, which a check-then-<see cref="DeleteDocument"/> could.
    /// </summary>
    Task<bool> PurgeBinnedDocument(string id, CancellationToken cancellationToken = default);

    /// <summary>The bin, newest deletion first (then id).</summary>
    Task<BinPage> ListBin(BinQuery query, CancellationToken cancellationToken = default);

    /// <summary>One binned document, or null when it is not in the bin (live, purged or never there).</summary>
    Task<BinnedDocument?> GetBinnedDocument(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The whole snapshot of a binned document, for the administrator's "export everything including the bin". Null when it
    /// is not in the bin.
    /// </summary>
    Task<Document?> GetBinnedSnapshot(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a document: changes the title and restamps <c>meta.updatedAt</c>, regenerating the snapshot so its
    /// version tag moves. Guarded like a replace — the write lands only while the stored snapshot is still the
    /// <paramref name="expectedVersion"/> the caller read from.
    /// </summary>
    Task<RenameOutcome> RenameDocument(string id, string newTitle, string expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Appends one Yjs update and returns its sequence number (1 for the first). Throws <see cref="DocumentNotFoundException"/>.</summary>
    Task<long> AppendUpdate(string documentId, byte[] update, Actor actor, CancellationToken cancellationToken = default);

    /// <summary>Updates with Seq greater than <paramref name="afterSeq"/>, in order. Empty for an unknown document.</summary>
    Task<IReadOnlyList<DocumentUpdate>> GetUpdates(string documentId, long afterSeq = 0, CancellationToken cancellationToken = default);

    /// <summary>Versions newest first (creation time, then id, which is time-ordered). Paged by <paramref name="query"/>; empty for an unknown document.</summary>
    Task<VersionPage> ListVersions(string documentId, VersionQuery query, CancellationToken cancellationToken = default);

    Task<DocumentVersion?> GetVersion(string documentId, string versionId, CancellationToken cancellationToken = default);

    /// <summary>Snapshots the document as currently stored under a name. Throws <see cref="DocumentNotFoundException"/>.</summary>
    Task<DocumentVersion> CreateVersion(string documentId, string name, Actor actor, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Appends an audit row and returns it with its assigned id.</summary>
    Task<AuditEntry> AppendAudit(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Audit rows for a document, oldest first. Survives deletion of the document.</summary>
    Task<IReadOnlyList<AuditEntry>> ListAudit(string documentId, CancellationToken cancellationToken = default);

    /// <summary>The asset with this id, or null. The bytes themselves live on disk, read through <see cref="IAssetReader"/>.</summary>
    Task<AssetInfo?> GetAsset(string id, CancellationToken cancellationToken = default);

    /// <summary>The asset whose content has this SHA-256 hash, or null: how an identical upload deduplicates.</summary>
    Task<AssetInfo?> FindAssetByHash(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts asset metadata. Throws <see cref="AssetExistsException"/> when a row with the same hash already exists,
    /// so a caller that lost a race can look the existing asset up rather than storing the same bytes twice.
    /// </summary>
    Task<AssetInfo> CreateAsset(AssetInfo asset, CancellationToken cancellationToken = default);
}
