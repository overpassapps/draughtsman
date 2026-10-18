namespace Draughtsman.Storage.Entities;

/// <summary>Named snapshots and pre-edit checkpoints. Deleted with their document.</summary>
public sealed class DocumentVersionRow
{
    public required string Id { get; set; }

    public required string DocumentId { get; set; }

    public required string Name { get; set; }

    public required string Snapshot { get; set; }

    public DateTime CreatedAt { get; set; }

    public required string Actor { get; set; }

    public string? Reason { get; set; }

    public DocumentRow? Document { get; set; }
}
