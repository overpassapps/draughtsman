namespace Draughtsman.Storage.Entities;

/// <summary>
/// The documents table. Snapshot is the full document JSON regenerated on every save; the other columns are
/// denormalised from it for listing and search. Timestamps are UTC DateTime rather than DateTimeOffset because SQLite
/// cannot order by DateTimeOffset and Npgsql maps timestamptz to UTC DateTime; the store converts at the boundary.
/// </summary>
public sealed class DocumentRow
{
    public required string Id { get; set; }

    public required string Title { get; set; }

    /// <summary>The title folded with ToLowerInvariant at save time, so search folds case identically on every provider.</summary>
    public required string TitleSearch { get; set; }

    public required string Type { get; set; }

    public required string Workspace { get; set; }

    public required string Snapshot { get; set; }

    public int SchemaVersion { get; set; }

    /// <summary>
    /// The <c>DocumentVersionTag</c> of <see cref="Snapshot"/>, written with it (AU-16), so a conditional read and the
    /// check of a save read this column rather than deserialising the snapshot. Null only for a row that predates the
    /// column: the store fills it in the first time that document's version is asked for.
    /// </summary>
    public string? Etag { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>When the document was moved to the bin (OP-02); null while it is live. Every read and listing filters on it.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>The user row that deleted it; null for a live document or a delete with no authenticated identity.</summary>
    public string? DeletedByUserId { get; set; }

    /// <summary>That user's display name when they deleted it, so the bin reads on its own after an account is renamed.</summary>
    public string? DeletedByLabel { get; set; }

    public List<DocumentUpdateRow> Updates { get; } = [];

    public List<DocumentVersionRow> Versions { get; } = [];
}
