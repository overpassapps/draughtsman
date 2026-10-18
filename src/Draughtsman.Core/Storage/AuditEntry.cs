namespace Draughtsman.Core.Storage;

/// <summary>
/// Who or what did what to which document. Rows outlive the document they describe so a deletion stays on record.
/// Action is a short verb from <see cref="AuditActions"/>; Id is assigned by the store (0 until appended).
/// <see cref="ActorUserId"/> is the authenticated user row's own id - a session's user, or a token's owner - never
/// the possibly-duplicate display name <see cref="Actor"/> carries; null only for a stdio MCP call, which has no
/// authenticated identity by design (security review F2/F9).
/// </summary>
public sealed record AuditEntry(string DocumentId, Actor Actor, string Action, DateTimeOffset At, string? ActorUserId = null)
{
    public long Id { get; init; }
}

/// <summary>The action vocabulary for audit rows. Strings, not an enum, so a later feature can add one without a migration.</summary>
public static class AuditActions
{
    public const string Created = "created";

    public const string Replaced = "replaced";

    public const string Deleted = "deleted";

    public const string Renamed = "renamed";

    /// <summary>A document taken out of the bin.</summary>
    public const string Restored = "restored";

    /// <summary>A binned document removed for good, by a person or by the retention pass.</summary>
    public const string Purged = "purged";

    /// <summary>The document was put back to an earlier version; the snapshot it replaced is the checkpoint just before.</summary>
    public const string VersionRestored = "version_restored";

    /// <summary>A person named a snapshot of the document as it was.</summary>
    public const string Snapshotted = "snapshotted";
}
