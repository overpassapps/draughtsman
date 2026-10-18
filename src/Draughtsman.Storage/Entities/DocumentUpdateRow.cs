namespace Draughtsman.Storage.Entities;

/// <summary>The append-only Yjs update log. Keyed by (document, seq); the bytes are never decoded here.</summary>
public sealed class DocumentUpdateRow
{
    public required string DocumentId { get; set; }

    public long Seq { get; set; }

    public required byte[] Update { get; set; }

    public DateTime CreatedAt { get; set; }

    public required string Actor { get; set; }

    public DocumentRow? Document { get; set; }
}
