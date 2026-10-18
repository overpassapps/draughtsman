namespace Draughtsman.Storage.Entities;

/// <summary>
/// Who or what changed which document. Deliberately no foreign key to documents so the row survives the document's
/// deletion; a regulated organisation wants to see the deletion, not lose the history with it.
/// </summary>
public sealed class AuditRow
{
    public long Id { get; set; }

    public required string DocumentId { get; set; }

    public required string Actor { get; set; }

    public required string ActorKind { get; set; }

    public string? Model { get; set; }

    public required string Action { get; set; }

    public DateTime At { get; set; }

    /// <summary>The authenticated user row's own id (session user or token owner); null only for a stdio MCP call.</summary>
    public string? ActorUserId { get; set; }
}
