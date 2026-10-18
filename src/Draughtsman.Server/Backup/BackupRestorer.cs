using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Draughtsman.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Draughtsman.Server.Backup;

public sealed record RestoreResult(BackupManifest Manifest, string DataFolder, string? SetAsideFolder, IReadOnlyList<string> Notes);

/// <summary>
/// Verifies and restores a bundle made by <see cref="BackupWriter"/> (OP-01). Everything that can be checked is checked before
/// the data folder is touched: the zip's own checksum when there is one, the manifest, that nothing in the zip is unlisted or
/// unsafe, every file's size and SHA-256, that this build knows the schema the database was at, and that the database file
/// passes SQLite's integrity check and holds the row counts the manifest recorded. The files are extracted into a staging
/// folder inside the data folder and moved into place last, so a restore that fails leaves the instance as it was.
/// </summary>
public static class BackupRestorer
{
    private const int CopyBuffer = 81920;

    public const string SetAsidePrefix = "pre-restore-";

    private const string StagingPrefix = ".restore-staging-";

    /// <summary>Reads the bundle and checks all of it. Throws <see cref="BackupException"/> naming the first thing wrong.</summary>
    /// <param name="expectedSha256">The zip's SHA-256 as the operator was given it; when null, a <c>&lt;bundle&gt;.sha256</c> file beside it is used if present.</param>
    public static async Task<(BackupManifest Manifest, IReadOnlyList<string> Notes)> VerifyAsync(string bundlePath, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        string path = Path.GetFullPath(bundlePath);
        if (!File.Exists(path))
        {
            throw new BackupException($"There is no backup bundle at {path}.");
        }

        var notes = new List<string>();
        string actual = await BackupWriter.Sha256OfFile(path, cancellationToken);
        string? expected = expectedSha256?.Trim().ToLowerInvariant();
        string checksumFile = path + ".sha256";
        if (expected is null && File.Exists(checksumFile))
        {
            expected = (await File.ReadAllTextAsync(checksumFile, cancellationToken)).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        }

        if (expected is null)
        {
            notes.Add("No checksum was given and there is no .sha256 file beside the bundle, so only the checksums inside its manifest were checked.");
        }
        else if (expected != actual)
        {
            throw new BackupException($"The bundle's checksum does not match: it is {actual}, expected {expected}. The file was truncated or changed after it was made; do not restore from it.");
        }

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new BackupException($"{path} is not a readable zip file.");
        }

        using (zip)
        {
            ZipArchiveEntry manifestEntry = zip.GetEntry(BackupLayout.ManifestName) ?? throw new BackupException("The bundle has no manifest.json, so it is not a Draughtsman backup (or is damaged).");
            BackupManifest manifest;
            using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
            {
                try
                {
                    manifest = BackupManifest.Parse(await reader.ReadToEndAsync(cancellationToken));
                }
                catch (System.Text.Json.JsonException error)
                {
                    throw new BackupException("manifest.json cannot be read: " + error.Message);
                }
            }

            if (manifest.Format != BackupLayout.FormatId)
            {
                throw new BackupException($"This bundle is format '{manifest.Format}'; this build restores '{BackupLayout.FormatId}'. Use the Draughtsman version that made it.");
            }

            if (!string.Equals(manifest.Provider, "sqlite", StringComparison.Ordinal))
            {
                throw new BackupException($"This bundle is from a {manifest.Provider} instance; restore is for SQLite bundles. Use pg_restore for Postgres (docs/upgrade-backup.md).");
            }

            CheckSchema(manifest);
            await CheckEntries(zip, manifest, cancellationToken);
            return (manifest, notes);
        }
    }

    public static async Task<RestoreResult> RestoreAsync(string bundlePath, string dataFolder, bool overwrite, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        (BackupManifest manifest, IReadOnlyList<string> verifyNotes) = await VerifyAsync(bundlePath, expectedSha256, cancellationToken);
        var notes = new List<string>(verifyNotes);
        string data = Path.GetFullPath(dataFolder);

        if (Directory.Exists(data))
        {
            string[] existing = Directory.EnumerateFileSystemEntries(data).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
            if (existing.Length > 0 && !overwrite)
            {
                throw new BackupException(
                    $"The data folder {data} is not empty ({string.Join(", ", existing.Take(6))}{(existing.Length > 6 ? ", ..." : string.Empty)}). Nothing was changed. "
                    + "Restore into an empty folder (and point the server at it), or re-run with --overwrite to set the current database and files aside in a pre-restore-<time> folder first.");
            }
        }

        Directory.CreateDirectory(data);
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string staging = Path.Combine(data, StagingPrefix + stamp + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        string? setAside = null;
        var moved = new List<(string From, string To)>();
        try
        {
            using (ZipArchive zip = ZipFile.OpenRead(Path.GetFullPath(bundlePath)))
            {
                foreach (BackupFileEntry file in manifest.Files)
                {
                    string relative = file.Path[BackupLayout.DataPrefix.Length..];
                    string target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    ZipArchiveEntry entry = zip.GetEntry(file.Path) ?? throw new BackupException($"{file.Path} is listed in the manifest but missing from the bundle.");
                    string hash = await CopyWithHash(entry, target, file.Size, cancellationToken);
                    if (hash != file.Sha256)
                    {
                        throw new BackupException($"{file.Path} changed while it was being extracted (checksum {hash}, expected {file.Sha256}). Nothing was restored.");
                    }
                }
            }

            string stagedDatabase = Path.Combine(staging, BackupLayout.DatabaseFile);
            CheckDatabase(stagedDatabase, manifest);

            string[] topLevel = manifest.Files.Select(f => f.Path[BackupLayout.DataPrefix.Length..].Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray();
            string[] toSetAside = topLevel.Concat(BackupLayout.DatabaseSiblings).Where(name => File.Exists(Path.Combine(data, name)) || Directory.Exists(Path.Combine(data, name))).Distinct(StringComparer.Ordinal).ToArray();
            if (toSetAside.Length > 0)
            {
                setAside = Path.Combine(data, SetAsidePrefix + stamp);
                for (int n = 1; Directory.Exists(setAside); n++)
                {
                    setAside = Path.Combine(data, SetAsidePrefix + stamp + "-" + n.ToString(CultureInfo.InvariantCulture));
                }

                Directory.CreateDirectory(setAside);
                foreach (string name in toSetAside)
                {
                    Move(Path.Combine(data, name), Path.Combine(setAside, name));
                    moved.Add((Path.Combine(data, name), Path.Combine(setAside, name)));
                }
            }

            foreach (string name in topLevel)
            {
                Move(Path.Combine(staging, name), Path.Combine(data, name));
            }

            if (manifest.IncludesSecrets)
            {
                notes.Add("This bundle carried the key ring and AI settings, so they are restored too. A ring protected by Windows DPAPI, the macOS Keychain or a key-encryption key only opens where that protection is available.");
            }
            else
            {
                notes.Add("The bundle has no key ring or AI settings: start the server, then re-enter the AI API key under Admin > AI if one was configured. Sign-in sessions and API tokens, which live in the database, carry over.");
            }

            return new RestoreResult(manifest, data, setAside, notes);
        }
        catch
        {
            // Put back anything already set aside, so a failed swap leaves the instance as it was found.
            foreach ((string from, string to) in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (!Path.Exists(from) && Path.Exists(to))
                    {
                        Move(to, from);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Nothing more can be done here; the set-aside folder still holds it.
                }
            }

            throw;
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The newest schema this build's migrations know; a bundle whose database is past it was made by a newer Draughtsman.</summary>
    private static void CheckSchema(BackupManifest manifest)
    {
        if (string.IsNullOrEmpty(manifest.SchemaMigration))
        {
            return;
        }

        var options = new StorageOptions(StorageProvider.Sqlite, "Data Source=:memory:");
        using DraughtsmanDbContext db = StorageSetup.CreateContext(options);
        HashSet<string> known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if (!known.Contains(manifest.SchemaMigration))
        {
            throw new BackupException(
                $"This backup's database is at schema {manifest.SchemaMigration}, which this Draughtsman ({Endpoints.HealthEndpoints.ProductVersion}) does not know: it was made by a newer version (the bundle says {manifest.DraughtsmanVersion}). "
                + "Restoring it here would put a newer schema under an older build, which is refused. Restore with that version or newer. Nothing was changed.");
        }
    }

    private static async Task CheckEntries(ZipArchive zip, BackupManifest manifest, CancellationToken cancellationToken)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (BackupFileEntry file in manifest.Files)
        {
            if (!BackupLayout.IsSafePayloadPath(file.Path))
            {
                throw new BackupException($"The manifest lists '{file.Path}', which is not a safe path inside the data folder. The bundle is damaged or was tampered with.");
            }

            if (!listed.Add(file.Path))
            {
                throw new BackupException($"The manifest lists {file.Path} twice.");
            }
        }

        if (!listed.Contains(BackupLayout.DataPrefix + BackupLayout.DatabaseFile))
        {
            throw new BackupException("The bundle has no database (data/data.db).");
        }

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName is BackupLayout.ManifestName or BackupLayout.ReadMeName || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            if (!listed.Contains(entry.FullName))
            {
                throw new BackupException($"The bundle contains {entry.FullName}, which its manifest does not list. The bundle was changed after it was made; it will not be restored.");
            }
        }

        foreach (BackupFileEntry file in manifest.Files)
        {
            ZipArchiveEntry entry = zip.GetEntry(file.Path) ?? throw new BackupException($"{file.Path} is listed in the manifest but missing from the bundle.");
            if (entry.Length != file.Size)
            {
                throw new BackupException($"{file.Path} is {entry.Length} bytes in the bundle but the manifest says {file.Size}. The bundle is damaged.");
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream stream = entry.Open();
            byte[] buffer = new byte[CopyBuffer];
            int read;
            long total = 0;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > file.Size)
                {
                    throw new BackupException($"{file.Path} is larger than the manifest says. The bundle is damaged.");
                }

                hash.AppendData(buffer, 0, read);
            }

            string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != file.Sha256)
            {
                throw new BackupException($"{file.Path} fails its checksum (it is {actual}, the manifest says {file.Sha256}). The bundle is damaged; nothing was restored.");
            }
        }
    }

    private static void CheckDatabase(string database, BackupManifest manifest)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            string? result = integrity.ExecuteScalar() as string;
            if (result != "ok")
            {
                throw new BackupException($"The database in the bundle fails SQLite's integrity check ({result}). Nothing was restored.");
            }
        }

        long documents;
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM \"documents\"";
            documents = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (documents != manifest.Counts.Documents)
        {
            throw new BackupException($"The restored database holds {documents} documents but the manifest recorded {manifest.Counts.Documents}. Nothing was restored.");
        }

        // The bin travels with the database: the same documents must still be deleted (not live, not gone) after the copy.
        if (manifest.Counts.BinnedDocuments is { } expectedBinned)
        {
            long binned;
            using (SqliteCommand inBin = connection.CreateCommand())
            {
                inBin.CommandText = "SELECT COUNT(*) FROM \"documents\" WHERE \"deleted_at\" IS NOT NULL";
                binned = Convert.ToInt64(inBin.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            if (binned != expectedBinned)
            {
                throw new BackupException($"The restored database holds {binned} documents in the bin but the manifest recorded {expectedBinned}. Nothing was restored.");
            }
        }
    }

    private static async Task<string> CopyWithHash(ZipArchiveEntry entry, string target, long expectedSize, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using Stream source = entry.Open();
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBuffer, useAsync: true);
        byte[] buffer = new byte[CopyBuffer];
        int read;
        long total = 0;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > expectedSize)
            {
                throw new BackupException($"{entry.FullName} is larger than the manifest says.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }
}
