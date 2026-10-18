namespace Draughtsman.Core.Storage;

/// <summary>
/// Metadata for one uploaded asset (build-spec-v0.1.md section 7, the <c>assets</c> table). The bytes live on disk
/// under the instance data folder, never in a database column; <see cref="Path"/> is that file's location relative to
/// the data folder and <see cref="Hash"/> is the SHA-256 of the content, which content-addresses it and makes an
/// identical upload deduplicate onto the same row.
/// </summary>
public sealed record AssetInfo(string Id, string Hash, string ContentType, long Size, string Path, DateTimeOffset CreatedAt);
