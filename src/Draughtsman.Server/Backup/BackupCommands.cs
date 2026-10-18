using System.Globalization;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Endpoints;
using Draughtsman.Storage;

namespace Draughtsman.Server.Backup;

/// <summary>
/// <c>draughtsman backup</c> and <c>draughtsman restore</c> (OP-01), registered as verbs of the dispatcher in
/// <c>Cli/ServerCli.cs</c>. They read the same <c>draughtsman.yaml</c> and data folder as the server and never start the
/// web host. Exit codes: 0 done, 1 refused or failed (the message says why and nothing was changed), 2 usage.
/// </summary>
public static class BackupCommands
{
    private const string DataOption = "--DRAUGHTSMAN_DATA=";

    /// <summary>" (2 in the bin)" when the database holds documents that were deleted and not yet purged: they are in the backup.</summary>
    private static string InBin(BackupCounts counts)
    {
        return counts.BinnedDocuments is > 0 ? $" ({counts.BinnedDocuments} in the bin)" : string.Empty;
    }

    public const string BackupUsage =
        "Usage: draughtsman backup [--out <file.zip|folder>] [--include-secrets] [--DRAUGHTSMAN_DATA=<folder>]\n" +
        "  Writes one zip with a consistent snapshot of the database (SQLite VACUUM INTO, safe while the server runs), the uploaded\n" +
        "  images, draughtsman.yaml, themes and branding, a manifest of checksums, and a <file>.sha256 beside it.\n" +
        "  --out              where to write it (default: draughtsman-backup-<time>.zip in the current folder); never overwrites\n" +
        "  --include-secrets  also carry the key ring (keys/), the stored AI settings (ai/) and the key ring's backups; the bundle is\n" +
        "                     then marked SENSITIVE in its name, manifest and README. Without it they are left out.\n" +
        "  Postgres instances: this prints the exact pg_dump command instead and exits 1; it does not pretend to back up.";

    public const string RestoreUsage =
        "Usage: draughtsman restore <bundle.zip> [--overwrite] [--sha256 <hex>] [--DRAUGHTSMAN_DATA=<folder>]\n" +
        "  Verifies the bundle (checksum, manifest, every file, the schema, the database's integrity), then restores it into the data folder.\n" +
        "  Stop the server first. Refuses a data folder that is not empty unless --overwrite is given, which first moves the current\n" +
        "  database and files into <data>/pre-restore-<time>/ (never deletes them).\n" +
        "  --sha256    the bundle's checksum as you were given it (default: the <bundle>.sha256 file beside it, if any)";

    public static async Task<int> BackupAsync(string[] args, TextWriter stdout, TextWriter stderr, string? dataFolder = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? outPath = null;
        bool includeSecrets = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--include-secrets":
                    includeSecrets = true;
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "-h" or "--help":
                    await stdout.WriteLineAsync(BackupUsage);
                    return 0;
                case var option when option.StartsWith(DataOption, StringComparison.Ordinal):
                    dataFolder ??= Path.GetFullPath(option[DataOption.Length..]);
                    break;
                case var option when option.StartsWith("--out=", StringComparison.Ordinal):
                    outPath = option["--out=".Length..];
                    break;
                default:
                    await stderr.WriteLineAsync($"Unexpected argument '{arg}'.\n{BackupUsage}");
                    return 2;
            }
        }

        string folder = dataFolder ?? ConfigLoader.ResolveDataFolder();
        if (!Directory.Exists(folder))
        {
            await stderr.WriteLineAsync($"The data folder '{folder}' does not exist. Set {DraughtsmanConfig.DataFolderEnvironmentVariable} (or --DRAUGHTSMAN_DATA=) to the server's data folder.");
            return 1;
        }

        DraughtsmanConfig config = ConfigLoader.Load(folder);
        StorageOptions storage = StorageOptions.FromConfig(config.Storage.Provider, config.Storage.ConnectionString, folder);
        if (storage.Provider == StorageProvider.Postgres)
        {
            await WritePostgresGuidance(storage, stdout, stderr);
            return 1;
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string defaultName = $"draughtsman-backup-{stamp}{(includeSecrets ? "-SENSITIVE" : string.Empty)}.zip";
        string output = outPath is null
            ? Path.Combine(Directory.GetCurrentDirectory(), defaultName)
            : Directory.Exists(outPath) || outPath.EndsWith(Path.DirectorySeparatorChar) || outPath.EndsWith(Path.AltDirectorySeparatorChar)
                ? Path.Combine(outPath, defaultName)
                : outPath;

        try
        {
            BackupResult result = await BackupWriter.CreateAsync(new BackupRequest(folder, config, storage, output, includeSecrets, HealthEndpoints.ProductVersion), cancellationToken);
            foreach (string warning in result.Warnings)
            {
                await stderr.WriteLineAsync("warning: " + warning);
            }

            BackupManifest manifest = result.Manifest;
            long bytes = new FileInfo(result.Path).Length;
            await stdout.WriteLineAsync($"Backup written: {result.Path}");
            await stdout.WriteLineAsync($"  {manifest.Files.Count} files, {bytes / 1024.0 / 1024.0:F1} MiB; {manifest.Counts.Documents} documents{InBin(manifest.Counts)}, {manifest.Counts.Users} users, {manifest.Counts.Assets} assets; schema {manifest.SchemaMigration}");
            await stdout.WriteLineAsync($"  sha256 {result.Sha256} (also in {result.Path}.sha256)");
            if (manifest.IncludesSecrets)
            {
                await stdout.WriteLineAsync("  SENSITIVE: this bundle includes the key ring and the stored AI settings. Store it like a password vault.");
            }
            else
            {
                await stdout.WriteLineAsync("  The key ring (keys/) and AI settings (ai/) are not in it; add --include-secrets if they must survive a restore.");
            }

            return 0;
        }
        catch (BackupException error)
        {
            await stderr.WriteLineAsync("Backup failed: " + error.Message);
            return 1;
        }
    }

    public static async Task<int> RestoreAsync(string[] args, TextWriter stdout, TextWriter stderr, string? dataFolder = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? bundle = null;
        string? sha256 = null;
        bool overwrite = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--sha256" when i + 1 < args.Length:
                    sha256 = args[++i];
                    break;
                case "-h" or "--help":
                    await stdout.WriteLineAsync(RestoreUsage);
                    return 0;
                case var option when option.StartsWith(DataOption, StringComparison.Ordinal):
                    dataFolder ??= Path.GetFullPath(option[DataOption.Length..]);
                    break;
                default:
                    if (arg.StartsWith('-') || bundle is not null)
                    {
                        await stderr.WriteLineAsync($"Unexpected argument '{arg}'.\n{RestoreUsage}");
                        return 2;
                    }

                    bundle = arg;
                    break;
            }
        }

        if (bundle is null)
        {
            await stderr.WriteLineAsync(RestoreUsage);
            return 2;
        }

        string folder = dataFolder ?? ConfigLoader.ResolveDataFolder();
        try
        {
            RestoreResult result = await BackupRestorer.RestoreAsync(bundle, folder, overwrite, sha256, cancellationToken);
            await stdout.WriteLineAsync($"Restored {result.Manifest.Counts.Documents} documents{InBin(result.Manifest.Counts)}, {result.Manifest.Counts.Users} users and {result.Manifest.Counts.Assets} assets into {result.DataFolder} (backup made {result.Manifest.CreatedAt:u} by Draughtsman {result.Manifest.DraughtsmanVersion}).");
            if (result.SetAsideFolder is not null)
            {
                await stdout.WriteLineAsync($"  The data that was there is in {result.SetAsideFolder}; delete it once you are happy with the restore.");
            }

            foreach (string note in result.Notes)
            {
                await stdout.WriteLineAsync("  " + note);
            }

            await stdout.WriteLineAsync("  Start the server against this folder. A backup from an older version is migrated on that first start, with its usual pre-migration copy.");
            return 0;
        }
        catch (BackupException error)
        {
            await stderr.WriteLineAsync("Restore refused: " + error.Message);
            return 1;
        }
    }

    /// <summary>Postgres is backed up with Postgres's own tool. Saying so, with the exact command, is the only honest answer a backup verb can give.</summary>
    private static async Task WritePostgresGuidance(StorageOptions storage, TextWriter stdout, TextWriter stderr)
    {
        await stderr.WriteLineAsync(
            "This instance stores its documents in Postgres, which Draughtsman cannot back up for you (a consistent dump needs pg_dump and your credentials).\n" +
            "No backup was made. Run this, with the password in PGPASSWORD or ~/.pgpass (it is not printed here):\n");
        await stdout.WriteLineAsync(PostgresBackupAdvice.DumpCommand(storage.ConnectionString));
        await stderr.WriteLineAsync(
            "\nThen copy the rest of the data folder (assets/, branding/, themes/, draughtsman.yaml; keys/ only if you accept a sensitive copy).\n" +
            "To restore into an empty schema (pg_restore --clean cannot drop a table a newer migration added):\n  " + PostgresBackupAdvice.EmptySchemaCommand(storage.ConnectionString) + "\n  " + PostgresBackupAdvice.RestoreCommand(storage.ConnectionString, "<dump file>") + "\n" +
            "See docs/upgrade-backup.md.");
    }
}
