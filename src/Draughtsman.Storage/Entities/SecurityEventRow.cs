namespace Draughtsman.Storage.Entities;

/// <summary>
/// One instance-level authentication or administration event. Deliberately no foreign keys (like <see cref="AuditRow"/>):
/// the row must survive whatever it describes, and it carries the email as it was then so it reads on its own.
/// </summary>
public sealed class SecurityEventRow
{
    public long Id { get; set; }

    public required string Type { get; set; }

    public DateTime At { get; set; }

    public string? ActorUserId { get; set; }

    public string? ActorLabel { get; set; }

    public string? TargetUserId { get; set; }

    public string? TargetLabel { get; set; }

    public string? SourceIp { get; set; }

    public string? Method { get; set; }

    public string? Detail { get; set; }
}
