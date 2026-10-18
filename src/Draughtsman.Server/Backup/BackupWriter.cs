using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Draughtsman.Server.Configuration;
using Draughtsman.Storage;
using Microsoft.Data.Sqlite;

namespace Draughtsman.Server.Backup;

/// <param name="DataFolder">The instance data folder to back up.</param>
/// <param name="OutputPath">The zip to create. It must not exist; nothing is ever overwritten.</param>
/// <param name="IncludeSecrets">Also carry the key ring, the stored AI settings and the key ring's own backups, and mark the bundle sensitive.</param>
public sealed record BackupRequest(string DataFolder, DraughtsmanConfig Config, StorageOptions Storage, string OutputPath, bool IncludeSecrets, string DraughtsmanVersion);

public sealed record BackupResult(string Path, string Sha256, BackupManifest Manifest, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes a backup bundle (OP-01). The database is a <c>VACUUM INTO</c> snapshot taken through SQLite itself, so it is one
/// consistent image even while the server is saving. The server runs SQLite in write-ahead-log mode, where the newest
/// commits live in <c>data.db-wal</c> and the main file can be a few KB while the log holds everything since the last
/// checkpoint, so a copy of <c>data.db</c> alone is missing recent work and a copy spanning a checkpoint is torn (see
/// <c>BackupConsistencyTests</c>, which reproduces both). The asset files are immutable and named by
/// their content, written before the row that points at them, so copying them after the snapshot can only carry extras,
/// never miss one the snapshot refers to.
/// </summary>
public static class BackupWriter
{
    private const int CopyBuffer = 81920;

    public static async Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Storage.Provider != StorageProvider.Sqlite)
        {
            throw new BackupException("Only a SQLite instance can be backed up by this command; use pg_dump for Postgres.");
        }

        string source = SqlitePath(request.Storage);
        if (!File.Exists(source))
        {
            throw new BackupException($"There is no database at {source}: nothing to back up. Check the data folder ({request.DataFolder}).");
        }

        string output = Path.GetFullPath(request.OutputPath);
        if (File.Exists(output) || Directory.Exists(output))
        {
            throw new BackupException($"{output} already exists; a backup never overwrites anything. Choose another name.");
        }

        string outputFolder = Path.GetDirectoryName(output) ?? throw new BackupException($"Cannot write a backup to {output}.");
        Directory.CreateDirectory(outputFolder);
        string staging = Path.Combine(outputFolder, ".draughtsman-backup-" + Guid.NewGuid().ToString("N"));
        string partial = output + ".part";
        Directory.CreateDirectory(staging);
        var warnings = new List<string>();
        try
        {
            string snapshot = Path.Combine(staging, BackupLayout.DatabaseFile);
            SnapshotSqlite(request.Storage.ConnectionString, snapshot);
            DropShortLivedCredentials(snapshot);
            (BackupCounts counts, string migration) = ReadSnapshot(snapshot);

            var entries = new List<BackupFileEntry>();
            await using (FileStream zipFile = new(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBuffer, useAsync: true))
            {
                RestrictToOwner(partial);
                using var zip = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: true);
                entries.Add(await AddFile(zip, snapshot, BackupLayout.DataPrefix + BackupLayout.DatabaseFile, "database", cancellationToken));

                foreach ((string relative, string kind) in BackupLayout.LooseFiles(request.DataFolder, request.Config))
                {
                    await AddIfPresent(zip, entries, request.DataFolder, relative, kind, warnings, cancellationToken);
                }

                foreach (string folder in BackupLayout.Folders)
                {
                    await AddFolder(zip, entries, request.DataFolder, folder, folder, warnings, cancellationToken);
                }

                if (request.IncludeSecrets)
                {
                    foreach (string folder in BackupLayout.SecretFolders)
                    {
                        await AddFolder(zip, entries, request.DataFolder, folder, "secret", warnings, cancellationToken);
                    }
                }

                var manifest = new BackupManifest(
                    BackupLayout.FormatId,
                    DateTimeOffset.UtcNow,
                    request.DraughtsmanVersion,
                    "sqlite",
                    migration,
                    request.IncludeSecrets,
                    request.IncludeSecrets ? SensitiveNotice : null,
                    counts,
                    entries);
                AddText(zip, BackupLayout.ManifestName, manifest.ToJson());
                AddText(zip, BackupLayout.ReadMeName, ReadMe(manifest));
            }

            // The manifest is inside the zip it describes, so re-read it from the finished file: what is reported is what a restore will see.
            string sha256 = await Sha256OfFile(partial, cancellationToken);
            BackupManifest written = ReadManifest(partial);
            File.Move(partial, output);
            await File.WriteAllTextAsync(output + ".sha256", $"{sha256}  {Path.GetFileName(output)}\n", cancellationToken);
            RestrictToOwner(output + ".sha256");
            return new BackupResult(output, sha256, written, warnings);
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteFile(partial);
        }
    }

    public const string SensitiveNotice = "SENSITIVE: this bundle holds the Data Protection key ring and the admin's stored AI settings. Anyone who can read it can read the stored AI API key and forge this instance's protected cookies. Store it like a password vault, never in a shared folder or ticket.";

    /// <summary>The SQLite file the connection string names.</summary>
    public static string SqlitePath(StorageOptions storage)
    {
        return Path.GetFullPath(new SqliteConnectionStringBuilder(storage.ConnectionString).DataSource);
    }

    /// <summary>
    /// <c>VACUUM INTO</c>: SQLite writes a complete, compacted copy of the database as of one read transaction, so a writer
    /// that commits meanwhile is either wholly in the copy or wholly out of it. The copy is made with the write-ahead log
    /// taken into account, which a file copy of <c>data.db</c> is not (see <c>BackupConsistencyTests</c>). Journal mode is a
    /// property of the database file and <c>VACUUM INTO</c> does not carry it over, so a WAL source gives a WAL snapshot: a
    /// restored instance runs in the mode the original did.
    /// </summary>
    public static void SnapshotSqlite(string connectionString, string target)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString) { Pooling = false, Mode = SqliteOpenMode.ReadWrite };
        bool wal;
        using (var connection = new SqliteConnection(builder.ToString()))
        {
            connection.Open();
            using (SqliteCommand mode = connection.CreateCommand())
            {
                mode.CommandText = "PRAGMA journal_mode";
                wal = string.Equals(mode.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase);
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO '" + target.Replace("'", "''", StringComparison.Ordinal) + "'";
            command.ExecuteNonQuery();
        }

        if (wal)
        {
            // Closing the last connection folds the log back into the file and removes the -wal/-shm files, leaving one file.
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
            snapshot.Open();
            using SqliteCommand command = snapshot.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=wal";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Password-reset links are credentials that live for an hour or a day, so a backup deliberately does not carry them: a restore
    /// weeks later must not bring a dead link back to life, and a bundle that travels (an off-site copy) must not hold hashes of
    /// links that were live when it was made. The table is emptied in the snapshot, never in the live database, with
    /// <c>secure_delete</c> and a <c>VACUUM</c> so the hashes do not linger in free pages of the file. A snapshot taken before the
    /// table existed has nothing to drop.
    /// </summary>
    private static void DropShortLivedCredentials(string snapshot)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = snapshot, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (SqliteCommand exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'password_reset_tokens'";
            if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                return;
            }
        }

        foreach (string sql in new[] { "PRAGMA secure_delete = ON", "DELETE FROM \"password_reset_tokens\"", "VACUUM" })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static (BackupCounts Counts, string Migration) ReadSnapshot(string snapshot)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        long Count(string table)
        {
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            return Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        // A database not yet migrated to the bin has no deleted_at column, so there is nothing to count in it.
        long? binned = null;
        using (SqliteCommand column = connection.CreateCommand())
        {
            column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('documents') WHERE name = 'deleted_at'";
            if (Convert.ToInt64(column.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                using SqliteCommand inBin = connection.CreateCommand();
                inBin.CommandText = "SELECT COUNT(*) FROM \"documents\" WHERE \"deleted_at\" IS NOT NULL";
                binned = Convert.ToInt64(inBin.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        using SqliteCommand latest = connection.CreateCommand();
        latest.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1";
        string migration = latest.ExecuteScalar() as string ?? string.Empty;
        return (new BackupCounts(Count("documents"), Count("users"), Count("assets"), binned), migration);
    }

    private static async Task AddIfPresent(ZipArchive zip, List<BackupFileEntry> entries, string dataFolder, string relative, string kind, List<string> warnings, CancellationToken cancellationToken)
    {
        string path = Path.Combine(dataFolder, relative);
        if (!File.Exists(path))
        {
            return;
        }

        await AddSafely(zip, entries, path, BackupLayout.DataPrefix + BackupLayout.Normalise(relative), kind, warnings, cancellationToken);
    }

    private static async Task AddFolder(ZipArchive zip, List<BackupFileEntry> entries, string dataFolder, string folder, string kind, List<string> warnings, CancellationToken cancellationToken)
    {
        string root = Path.Combine(dataFolder, folder);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            // Half-written uploads (AssetStore writes <name>.<hex>.tmp then renames) are not part of anything yet.
            if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = BackupLayout.Normalise(Path.GetRelativePath(dataFolder, file));
            await AddSafely(zip, entries, file, BackupLayout.DataPrefix + relative, kind, warnings, cancellationToken);
        }
    }

    /// <summary>A file that vanished between listing and reading (a branding file replaced, say) is noted, not a reason to lose the backup.</summary>
    private static async Task AddSafely(ZipArchive zip, List<BackupFileEntry> entries, string path, string entryName, string kind, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            entries.Add(await AddFile(zip, path, entryName, kind, cancellationToken));
        }
        catch (FileNotFoundException)
        {
            warnings.Add($"{entryName} disappeared while it was being copied and is not in the backup.");
        }
        catch (DirectoryNotFoundException)
        {
            warnings.Add($"{entryName} disappeared while it was being copied and is not in the backup.");
        }
    }

    private static async Task<BackupFileEntry> AddFile(ZipArchive zip, string path, string entryName, string kind, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        await using (Stream target = entry.Open())
        await using (FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CopyBuffer, useAsync: true))
        {
            byte[] buffer = new byte[CopyBuffer];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                size += read;
            }
        }

        return new BackupFileEntry(entryName, size, Convert.ToHexStringLower(hash.GetHashAndReset()), kind);
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string ReadMe(BackupManifest manifest)
    {
        var text = new StringBuilder();
        if (manifest.IncludesSecrets)
        {
            text.AppendLine(SensitiveNotice).AppendLine();
        }

        text.AppendLine("A Draughtsman backup, made " + manifest.CreatedAt.ToString("u", System.Globalization.CultureInfo.InvariantCulture) + " by Draughtsman " + manifest.DraughtsmanVersion + ".")
            .AppendLine()
            .AppendLine("Restore it on a host running the same or a newer Draughtsman, with the server stopped:")
            .AppendLine("    draughtsman restore <this file> --DRAUGHTSMAN_DATA=<an empty folder>")
            .AppendLine("restore checks every file against manifest.json first and refuses a non-empty folder unless you add --overwrite.")
            .AppendLine()
            .AppendLine(manifest.IncludesSecrets
                ? "It includes the key ring (keys/), the stored AI settings (ai/) and the key ring's own backups (backups/)."
                : "It does NOT include the key ring (keys/) or the stored AI settings (ai/): after a restore the admin re-enters the AI API key. Make a bundle with --include-secrets if the key must survive.")
            .AppendLine("The database here is a consistent snapshot (VACUUM INTO), not a copy of the live file.")
            .AppendLine("Password-reset links are deliberately not in it: they are short-lived credentials, so after a restore anyone who needs one asks for a new link.")
            .AppendLine("Documents that were in the bin when it was taken are in it, still in the bin, with their versions; a restore brings them back the same way.");
        return text.ToString().ReplaceLineEndings("\n");
    }

    public static async Task<string> Sha256OfFile(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBuffer, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static BackupManifest ReadManifest(string zipPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry entry = zip.GetEntry(BackupLayout.ManifestName) ?? throw new BackupException("The bundle just written has no manifest.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return BackupManifest.Parse(reader.ReadToEnd());
    }

    /// <summary>The bundle can hold password hashes and, with secrets, key material: owner-only wherever the platform has such a thing.</summary>
    internal static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover scratch folder beside the output is harmless.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
