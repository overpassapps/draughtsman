using System.Text.Json;
using System.Text.Json.Serialization;

namespace Draughtsman.Server.Backup;

/// <summary>
/// What a backup bundle says about itself, written as <c>manifest.json</c> inside the zip (OP-01). <c>restore</c> reads it
/// before it touches anything: the format, the schema the database was at, whether it holds secrets, and the size and
/// SHA-256 of every file, so a truncated, edited or partly-copied bundle is refused rather than half-restored.
/// </summary>
public sealed record BackupManifest(
    string Format,
    DateTimeOffset CreatedAt,
    string DraughtsmanVersion,
    string Provider,
    string SchemaMigration,
    bool IncludesSecrets,
    string? Notice,
    BackupCounts Counts,
    IReadOnlyList<BackupFileEntry> Files)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n", // indented output otherwise uses the platform newline; a bundle is the same bytes on every host
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static BackupManifest Parse(string json)
    {
        return JsonSerializer.Deserialize<BackupManifest>(json, JsonOptions) ?? throw new BackupException("manifest.json is empty.");
    }
}

/// <summary>
/// Row counts taken from the database snapshot inside the bundle, which a restore checks the restored file against.
/// <paramref name="Documents"/> counts every document row, the bin included (a backup is of the whole database, so a document
/// deleted last week and not yet purged is in it and comes back in the bin when restored); <paramref name="BinnedDocuments"/>
/// is how many of those were in the bin. Null in a bundle made before the bin existed, which has no such column to check.
/// </summary>
public sealed record BackupCounts(long Documents, long Users, long Assets, long? BinnedDocuments = null);

/// <param name="Path">Inside the zip, always under <c>data/</c>, forward slashes.</param>
/// <param name="Kind">database | config | assets | branding | themes | secret</param>
public sealed record BackupFileEntry(string Path, long Size, string Sha256, string Kind);

/// <summary>A backup or restore that cannot go ahead, with the reason an operator needs. The message is the whole report.</summary>
public sealed class BackupException(string message) : Exception(message);
