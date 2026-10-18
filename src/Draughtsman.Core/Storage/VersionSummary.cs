namespace Draughtsman.Core.Storage;

/// <summary>
/// A named snapshot's header, without the snapshot itself. <paramref name="Size"/> is the length of the stored snapshot
/// JSON in characters (what the version history shows as its size; 0 where a store does not know).
/// </summary>
public sealed record VersionSummary(string Id, string DocumentId, string Name, DateTimeOffset CreatedAt, Actor Actor, string? Reason, long Size = 0);
