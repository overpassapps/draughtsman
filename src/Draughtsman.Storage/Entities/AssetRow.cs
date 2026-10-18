namespace Draughtsman.Storage.Entities;

/// <summary>
/// The assets table (build-spec-v0.1.md section 7): one row per uploaded image. The bytes are never here - they live
/// on disk under the data folder at <see cref="Path"/> (relative to it) - so the database stays small and a backup of
/// the folder is a backup of everything. <see cref="Hash"/> is the SHA-256 of the content and carries a unique index,
/// which is what makes an identical upload deduplicate instead of storing the file twice.
/// </summary>
public sealed class AssetRow
{
    public required string Id { get; set; }

    public required string Hash { get; set; }

    public required string ContentType { get; set; }

    public required string Path { get; set; }

    public long Size { get; set; }

    public DateTime CreatedAt { get; set; }
}
