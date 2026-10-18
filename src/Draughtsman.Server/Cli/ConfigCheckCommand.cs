using System.Globalization;
using System.Text;
using System.Text.Json;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Hosting;
using Draughtsman.Server.KeyRing;
using Draughtsman.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using YamlDotNet.Core;

namespace Draughtsman.Server.Cli;

/// <summary>How serious a line of the report is. <see cref="Refuse"/> is the only one that makes the command exit non-zero.</summary>
public enum CheckLevel
{
    Ok,
    Info,
    Warn,
    Refuse,
}

public sealed record CheckLine(string Section, CheckLevel Level, string Message);

/// <summary>
/// <c>draughtsman config check</c>: what starting THIS binary on a data folder would do, worked out without starting it
/// (docs/upgrade-backup.md, "Before you upgrade"). It reads <c>draughtsman.yaml</c> (renamed, removed and unknown settings),
/// compares the database's applied migrations with this build's (current, N pending, or newer than this build), checks that the
/// pre-migration copy could be written, and looks at the key ring. It changes nothing in the database or the
/// configuration; its only write is one empty probe file, created and deleted in the data folder, to find out whether the server
/// could write there. A line is a refusal only where the server itself would refuse to start (a database a newer build migrated,
/// a database that fails its integrity check, a copy that cannot be made, a configuration that cannot be read).
/// Exit codes: 0 would start (warnings are listed), 1 would refuse, 2 usage, 3 the database is newer than this build.
/// </summary>
public static class ConfigCheckCommand
{
    public const string Usage =
        "Usage: draughtsman config check [--json] [--DRAUGHTSMAN_DATA=<folder>]\n" +
        "  Reports what starting this version on the data folder would do, without starting it: the settings in draughtsman.yaml\n" +
        "  (renamed, removed or unknown ones are warnings, never fatal), the database schema against this build (current, N migrations\n" +
        "  pending, or newer than this build), whether the pre-upgrade copy can be written and the key ring.\n" +
        "  Changes nothing but a probe file it creates and deletes in the data folder. Run it before every upgrade, with the NEW binary.\n" +
        "  Exit 0: would start (warnings are listed). 1: would refuse (the lines marked REFUSE say why). 2: usage. 3: the database is\n" +
        "  newer than this build.";

    private const string DataOption = "--DRAUGHTSMAN_DATA=";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, string? dataFolder = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool json = false;
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "-h" or "--help":
                    await stdout.WriteLineAsync(Usage);
                    return 0;
                case var option when option.StartsWith(DataOption, StringComparison.Ordinal):
                    dataFolder ??= Path.GetFullPath(option[DataOption.Length..]);
                    break;
                default:
                    await stderr.WriteLineAsync($"draughtsman config check: unknown option '{arg}'.\n{Usage}");
                    return 2;
            }
        }

        CheckReport report = await CheckAsync(dataFolder ?? ConfigLoader.ResolveDataFolder(), cancellationToken);
        if (json)
        {
            await stdout.WriteLineAsync(report.ToJson());
        }
        else
        {
            await stdout.WriteAsync(report.ToText());
        }

        return report.ExitCode;
    }

    /// <summary>Runs every check. Public so tests can read the structured report instead of the text.</summary>
    public static async Task<CheckReport> CheckAsync(string folder, CancellationToken cancellationToken = default, bool openDatabase = true)
    {
        var report = new CheckReport(folder);
        if (!Directory.Exists(folder))
        {
            report.Add("Data folder", CheckLevel.Warn, $"{folder} does not exist. The first start creates a new, empty instance there and there is nothing to upgrade. If you expected your data, point DRAUGHTSMAN_DATA (or --DRAUGHTSMAN_DATA=) at the right folder.");
            report.Add("Data folder", CheckLevel.Info, "Nothing else can be checked.");
            return report;
        }

        DraughtsmanConfig? config = CheckConfig(report, folder);
        if (config is null)
        {
            return report;
        }

        CheckWritable(report, folder);
        CheckThemeFetch(report, config);
        await CheckStorageAsync(report, folder, config, cancellationToken, openDatabase);
        CheckKeyRing(report, folder, config);
        CheckStoredSecrets(report, folder, config);
        return report;
    }

    // ------------------------------------------------------------------------------------------------------ settings

    private static DraughtsmanConfig? CheckConfig(CheckReport report, string folder)
    {
        const string section = "Settings";
        string path = Path.Combine(folder, DraughtsmanConfig.FileName);
        if (!File.Exists(path))
        {
            report.Add(section, CheckLevel.Ok, $"No {DraughtsmanConfig.FileName}: every setting takes its default.");
            return new DraughtsmanConfig();
        }

        try
        {
            (DraughtsmanConfig config, IReadOnlyList<ConfigWarning> warnings) = ConfigLoader.LoadWithWarnings(folder);
            if (warnings.Count == 0)
            {
                report.Add(section, CheckLevel.Ok, $"{DraughtsmanConfig.FileName} is read in full; every setting in it is one this version knows.");
            }

            foreach (ConfigWarning warning in warnings)
            {
                report.Add(section, CheckLevel.Warn, warning.Message);
            }

            return config;
        }
        catch (Exception error) when (error is YamlException or InvalidOperationException)
        {
            string detail = error is YamlException yaml ? $"line {yaml.Start.Line}, column {yaml.Start.Column}: {yaml.Message}" : error.Message;
            report.Add(section, CheckLevel.Refuse, $"{DraughtsmanConfig.FileName} cannot be read ({detail}). The server would stop at start-up. Fix or remove the file; nothing has been changed.");
            return null;
        }
    }

    private static void CheckWritable(CheckReport report, string folder)
    {
        string probe = Path.Combine(folder, $".draughtsman-config-check-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            report.Add("Data folder", CheckLevel.Ok, $"{folder} is writable by the account running this command (the server needs that to migrate, take its copy and save).");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report.Add("Data folder", CheckLevel.Warn, $"{folder} is not writable by the account running this command ({error.GetType().Name}). The server's own account may differ; it must be able to write here.");
        }
    }

    // ------------------------------------------------------------------------------------------------------ database

    private static async Task CheckStorageAsync(CheckReport report, string folder, DraughtsmanConfig config, CancellationToken cancellationToken, bool openDatabase = true)
    {
        const string section = "Database";
        StorageOptions storage;
        try
        {
            storage = StorageOptions.FromConfig(config.Storage.Provider, config.Storage.ConnectionString, folder);
        }
        catch (InvalidOperationException error)
        {
            report.Add(section, CheckLevel.Refuse, $"{error.Message} The server would stop at start-up.");
            return;
        }

        await using DraughtsmanDbContext db = StorageSetup.CreateContext(storage);
        List<string> known = [.. db.Database.GetMigrations().Order(StringComparer.Ordinal)];
        report.Database = new CheckDatabase(storage.ProviderName, known.Count);
        if (storage.Provider == StorageProvider.Sqlite && !openDatabase)
        {
            // Only the doctor and the support bundle ask for this (see DatabaseGuard); the database file is named, not opened.
            report.Add(section, CheckLevel.Info, "The database was not opened by this run: see the ownership finding.");
        }
        else if (storage.Provider == StorageProvider.Sqlite)
        {
            CheckSqlite(report, section, storage, config, known);
        }
        else
        {
            await CheckPostgresAsync(report, section, storage, known, cancellationToken);
        }
    }

    private static void CheckSqlite(CheckReport report, string section, StorageOptions storage, DraughtsmanConfig config, List<string> known)
    {
        string file = Path.GetFullPath(new SqliteConnectionStringBuilder(storage.ConnectionString).DataSource);
        if (!File.Exists(file))
        {
            report.Add(section, CheckLevel.Info, $"No database file at {file}: the first start creates it and applies all {known.Count} migrations. Nothing is at risk.");
            report.Database = report.Database! with { Applied = 0, Pending = known };
            return;
        }

        SqliteTableCounts counts;
        List<string> applied;
        try
        {
            // A leftover write-ahead log may hold committed pages, so only a database with none is opened immutable (which creates no -wal or -shm file).
            bool hasLog = File.Exists(file + "-wal") && new FileInfo(file + "-wal").Length > 0;
            counts = SqliteTableCounts.Read($"Data Source={file}", immutable: !hasLog);
            applied = ReadApplied(file, immutable: !hasLog);
        }
        catch (SqliteException error)
        {
            report.Add(section, CheckLevel.Refuse, $"The database {file} could not be read ({error.Message}). The server would stop at start-up; restore a backup (docs/upgrade-backup.md).");
            return;
        }

        if (!counts.IsWhole)
        {
            report.Add(section, CheckLevel.Refuse, $"SQLite's integrity_check on {file} reports: {counts.Integrity}. The upgrade would refuse to run on a damaged database (its safety copy would be damaged too). Restore a backup first.");
            return;
        }

        report.Add(section, CheckLevel.Ok, $"{file} passes SQLite's integrity_check ({counts.Counts.Count} tables, {counts.Counts.GetValueOrDefault("documents")} documents, {counts.Counts.GetValueOrDefault("users")} users).");

        List<string> newer = [.. applied.Where(m => !known.Contains(m, StringComparer.Ordinal))];
        List<string> pending = [.. known.Where(m => !applied.Contains(m, StringComparer.Ordinal))];
        report.Database = report.Database! with { Applied = applied.Count, Pending = pending, Newer = newer };
        if (newer.Count > 0)
        {
            report.Add(section, CheckLevel.Refuse, $"This database was migrated by a NEWER version of Draughtsman (it has {newer.Count} migration(s) this build does not know: {string.Join(", ", newer)}). This version would refuse to open it and exit with code {Maintenance.StorageStartup.SchemaNewerExitCode}. Run the newer version, or go back by restoring the pre-upgrade copy or backup (docs/upgrade-backup.md, 'Downgrade').");
            report.NewerThanBuild = true;
            return;
        }

        if (pending.Count == 0)
        {
            report.Add(section, CheckLevel.Ok, $"The schema is current: all {known.Count} migrations of this version are applied. Starting does not change the database.");
            return;
        }

        report.Add(section, CheckLevel.Info, $"{pending.Count} of {known.Count} migrations are pending and would be applied at start: {string.Join(", ", pending)}.");
        CheckCopyPossible(report, section, file, config);
    }

    internal static List<string> ReadApplied(string file, bool immutable)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = immutable ? new Uri(file).AbsoluteUri + "?immutable=1" : file, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
        if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
        {
            return [];
        }

        using SqliteCommand list = connection.CreateCommand();
        list.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
        var applied = new List<string>();
        using SqliteDataReader reader = list.ExecuteReader();
        while (reader.Read())
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    private static void CheckCopyPossible(CheckReport report, string section, string file, DraughtsmanConfig config)
    {
        long size = new FileInfo(file).Length;
        report.Add(section, CheckLevel.Info, $"Before migrating, the server copies the database to {Path.GetFileName(file)}.pre-migration-<time> beside it, checks the copy opens and passes integrity_check, and keeps the newest {config.Retention.PreMigrationCopies} such copies. If a migration fails or the result loses rows, the copy is put back and the server refuses to start (exit code {Maintenance.StorageStartup.MigrationRolledBackExitCode}).");
        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(file) ?? file).AvailableFreeSpace;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            report.Add(section, CheckLevel.Info, "Free disk space could not be read, so it was not checked.");
            return;
        }

        long needed = size + (size / 5) + (16 * 1024 * 1024);
        if (free >= needed)
        {
            report.Add(section, CheckLevel.Ok, $"There is room for the copy: the database is {Megabytes(size)} MiB and {Megabytes(free)} MiB is free.");
            return;
        }

        string message = $"There is NOT room for the copy: the database is {Megabytes(size)} MiB and only {Megabytes(free)} MiB is free (about {Megabytes(needed)} MiB is needed).";
        if (config.Storage.AllowMigrationWithoutBackup)
        {
            report.Add(section, CheckLevel.Warn, message + " storage.allowMigrationWithoutBackup is true, so the server would migrate WITHOUT a safety copy and without an automatic way back. Take a backup by hand first.");
        }
        else
        {
            report.Add(section, CheckLevel.Refuse, message + " The server would refuse to migrate (exit code " + Maintenance.StorageStartup.PreMigrationCopyFailedExitCode.ToString(CultureInfo.InvariantCulture) + "). Free space, or take your own backup and set storage.allowMigrationWithoutBackup: true to go ahead without a copy.");
        }
    }

    private static async Task CheckPostgresAsync(CheckReport report, string section, StorageOptions storage, List<string> known, CancellationToken cancellationToken)
    {
        List<string> applied;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(storage.ConnectionString) { Timeout = 10, CommandTimeout = 10 };
            await using var connection = new NpgsqlConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand exists = connection.CreateCommand();
            exists.CommandText = "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL";
            applied = [];
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                await using NpgsqlCommand list = connection.CreateCommand();
                list.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
                await using NpgsqlDataReader reader = await list.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    applied.Add(reader.GetString(0));
                }
            }
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException or ArgumentException)
        {
            report.Add(section, CheckLevel.Refuse, $"The Postgres database could not be reached ({error.GetType().Name}). The server would stop at start-up. Check storage.connectionString and that the database is running.");
            return;
        }

        List<string> newer = [.. applied.Where(m => !known.Contains(m, StringComparer.Ordinal))];
        List<string> pending = [.. known.Where(m => !applied.Contains(m, StringComparer.Ordinal))];
        report.Database = report.Database! with { Applied = applied.Count, Pending = pending, Newer = newer };
        report.Add(section, CheckLevel.Ok, "The Postgres database is reachable.");
        if (newer.Count > 0)
        {
            report.Add(section, CheckLevel.Refuse, $"This database was migrated by a NEWER version of Draughtsman (it has {newer.Count} migration(s) this build does not know: {string.Join(", ", newer)}). This version would refuse to open it and exit with code {Maintenance.StorageStartup.SchemaNewerExitCode}. Run the newer version, or restore the Postgres snapshot taken before upgrading (pg_restore).");
            report.NewerThanBuild = true;
            return;
        }

        if (pending.Count == 0)
        {
            report.Add(section, CheckLevel.Ok, $"The schema is current: all {known.Count} migrations of this version are applied. Starting does not change the database.");
            return;
        }

        report.Add(section, CheckLevel.Info, $"{pending.Count} of {known.Count} migrations are pending and would be applied at start: {string.Join(", ", pending)}.");
        if (applied.Count > 0)
        {
            report.Add(section, CheckLevel.Warn, StorageMigrator.PostgresUpgradeNotice(storage, pending));
        }
    }

    private static string Megabytes(long bytes)
    {
        return (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------------------------------------------ themes

    private static void CheckThemeFetch(CheckReport report, DraughtsmanConfig config)
    {
        const string section = "Themes";
        ThemeUrlFetchConfig fetch = config.Themes.FetchFromUrl;
        List<string> invalid = [.. fetch.AllowedHosts.Where(entry => Theming.Extraction.ThemeFetchHosts.NormaliseAllowedHost(entry) is null)];
        foreach (string entry in invalid)
        {
            report.Add(section, CheckLevel.Warn, $"themes.fetchFromUrl.allowedHosts entry '{entry}' is not a plain host name or IP address (no scheme, port, path or wildcard) and is ignored.");
        }

        if (!fetch.Enabled)
        {
            report.Add(section, CheckLevel.Ok, "themes.fetchFromUrl.enabled is false (the default): the Themes page's From a website reads pasted CSS only, and the server fetches no web page." + (fetch.AllowedHosts.Count > 0 ? " The allowedHosts entries do nothing while it is off." : string.Empty));
            return;
        }

        List<string> listed = [.. fetch.AllowedHosts.Select(Theming.Extraction.ThemeFetchHosts.NormaliseAllowedHost).OfType<string>().Distinct(StringComparer.Ordinal)];
        report.Add(section, CheckLevel.Info, "themes.fetchFromUrl.enabled is true: an administrator can make this server fetch a web page and up to 8 of its stylesheets (https only, 2 MiB and 15 seconds in all, no credentials or cookies) from Admin, Themes, From a website. Each fetch is recorded in the security log (host and outcome). Nothing fetched is stored." + (listed.Count == 0 ? " Every loopback, private, link-local and metadata address is refused." : $" Hosts reachable even at an internal address: {string.Join(", ", listed)}."));
        foreach (string host in listed.Where(host => System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? ip) && (ip.ToString() is "169.254.169.254" or "fd00:ec2::254")))
        {
            report.Add(section, CheckLevel.Warn, $"themes.fetchFromUrl.allowedHosts lists {host}, a cloud metadata address: an administrator could make this server read its instance credentials. Remove it unless that is what you want.");
        }
    }

    // ------------------------------------------------------------------------------------------------------ key ring

    private static void CheckKeyRing(CheckReport report, string folder, DraughtsmanConfig config)
    {
        const string section = "Key ring";
        string requested = (Environment.GetEnvironmentVariable(KeyRingSetup.ProtectionOverrideVariable) is { Length: > 0 } overridden ? overridden : config.KeyRing.Protection).Trim().ToLowerInvariant();
        IReadOnlyList<KeyFileInfo> keys = KeyRingFiles.Inspect(KeyRingFiles.KeysFolder(folder));
        int plain = keys.Count(k => k.State == KeyFileState.Plaintext);
        int encrypted = keys.Count(k => k.State == KeyFileState.Encrypted);
        int corrupt = keys.Count(k => k.State == KeyFileState.Corrupt);
        report.KeyRing = $"{requested}; {plain} plain, {encrypted} protected, {corrupt} damaged";
        if (keys.Count == 0)
        {
            report.Add(section, CheckLevel.Info, $"No key ring yet (keys/ is empty or absent); the first start creates one. keyRing.protection is '{requested}'.");
            return;
        }

        report.Add(section, CheckLevel.Info, $"keyRing.protection is '{requested}'; keys/ holds {keys.Count} key file(s): {plain} plain text, {encrypted} protected, {corrupt} damaged.");
        if (corrupt > 0)
        {
            report.Add(section, CheckLevel.Warn, $"{corrupt} key file(s) are not well-formed; at start they are set aside as *.damaged and the stored AI key may need entering again.");
        }

        switch (requested)
        {
            case KeyRingConfig.ProtectionNone:
                if (encrypted > 0)
                {
                    report.Add(section, CheckLevel.Warn, "The ring holds protected keys but protection is 'none': the stored AI API key cannot be read and has to be entered again. Restore the protection that wrote them (the same key-encryption key, certificate or account).");
                }
                else
                {
                    report.Add(section, CheckLevel.Ok, "Protection is switched off on purpose; the ring stays as plain text.");
                }

                break;
            case KeyRingConfig.ProtectionKey:
                CheckKeyEncryptionKey(report, section, config);
                break;
            default:
                if (plain > 0)
                {
                    report.Add(section, CheckLevel.Info, $"{plain} plain-text key(s) would be protected in place at start (after a backup of keys/ under backups/key-ring-<time>/). Nothing is regenerated, so the stored AI key keeps working.");
                }
                else if (encrypted > 0)
                {
                    report.Add(section, CheckLevel.Info, "Protected keys are read with the configured protection at start; if it is unavailable the server still starts and the stored AI key shows as needing re-entry.");
                }

                break;
        }

    }

    // ------------------------------------------------------------------------------------------------------ stored secrets

    /// <summary>
    /// Whether the stored AI API key and the saved SMTP password can be read, and what starting this version does about them: reads them the way the server
    /// does (the pinned application name first, then each install path an earlier build could have bound them to) and writes nothing. A secret an earlier
    /// build tied to its install folder is re-protected at start; one nothing can read is named here, with the reason and what to do, before an upgrade
    /// rather than after it.
    /// </summary>
    private static void CheckStoredSecrets(CheckReport report, string folder, DraughtsmanConfig config)
    {
        const string section = "Stored secrets";
        string ai = Path.Combine(folder, Ai.AiSettingsStore.DirectoryName, "settings.json");
        string email = Path.Combine(folder, Email.EmailSettingsStore.DirectoryName, "settings.json");
        if (!File.Exists(ai) && !File.Exists(email))
        {
            report.Add(section, CheckLevel.Info, "No stored AI API key or saved SMTP password (nothing is saved in Admin > AI or Admin > Email).");
            return;
        }

        KeyRingProtection? protection = ReadOnlyProtection(report, section, folder, config);
        if (protection is null)
        {
            return;
        }

        string contentRoot = (AppLayout.ContentRoot(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        IReadOnlyList<LegacyCandidate> candidates = LegacyDiscriminators.Resolve(folder, contentRoot, config.KeyRing, name => Environment.GetEnvironmentVariable(name));
        using var legacy = new LegacySecretReader(KeyRingFiles.KeysFolder(folder), protection, candidates, () => LegacyDiscriminators.Late(folder, contentRoot));
        IReadOnlyList<SecretInspection> inspections = StoredSecrets.InspectReadOnly(folder, protection, legacy);

        foreach (SecretInspection inspection in inspections.Where(i => i.Stored))
        {
            StoredSecretState state = StoredSecrets.Describe(inspection, legacy);
            report.Secrets.Add($"{state.Name}: {state.State}");
            switch (state.State)
            {
                case "current":
                    report.Add(section, CheckLevel.Ok, state.Message);
                    break;
                case "legacy":
                    report.Add(section, CheckLevel.Info, state.Message + $" The earlier file is first copied to {KeyRingBackup.FolderName}/{SecretsBackup.Prefix}<time>/{inspection.Owner.RelativeFile}.");
                    break;
                default:
                    report.Add(section, CheckLevel.Warn, state.Message);
                    break;
            }
        }
    }

    /// <summary>
    /// The key ring's protection resolved without changing anything: a key-encryption key, a certificate, DPAPI and an existing Keychain item are only read.
    /// Null, with a line in the report, when it cannot be resolved here (a Keychain this account cannot reach, a key-encryption key not in this shell's environment).
    /// </summary>
    private static KeyRingProtection? ReadOnlyProtection(CheckReport report, string section, string folder, DraughtsmanConfig config)
    {
        // A ring that is still plain text needs no protection to be read (the server reads it that way before it protects it), and resolving one that
        // would use the Keychain can create the item and the ring's identity file: a check must not.
        if (!KeyRingFiles.Inspect(KeyRingFiles.KeysFolder(folder)).Any(k => k.State == KeyFileState.Encrypted))
        {
            return new NoKeyRingProtection(UnprotectedReason.Disabled, "The key ring is plain text.");
        }

        bool keychainRecorded = File.Exists(Path.Combine(KeyRingFiles.KeysFolder(folder), KeyRingFiles.IdentityFileName));
        // The environment override wins over the file, as it does at start.
        KeyRingConfig effective = Environment.GetEnvironmentVariable(KeyRingSetup.ProtectionOverrideVariable) is { Length: > 0 } overridden
            ? new KeyRingConfig { Protection = overridden, KeyEnvironmentVariable = config.KeyRing.KeyEnvironmentVariable, KeyFile = config.KeyRing.KeyFile, Certificate = config.KeyRing.Certificate }
            : config.KeyRing;
        bool keyGiven = !string.IsNullOrWhiteSpace(config.KeyRing.KeyFile) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.KeyRing.KeyEnvironmentVariable)) || !string.IsNullOrWhiteSpace(config.KeyRing.Certificate.Path);
        string requested = (effective.Protection ?? KeyRingConfig.ProtectionAuto).Trim().ToLowerInvariant();
        if (!keychainRecorded && OperatingSystem.IsMacOS() && (requested == KeyRingConfig.ProtectionKeychain || (requested == KeyRingConfig.ProtectionAuto && !keyGiven)))
        {
            report.Add(section, CheckLevel.Info, "The stored secrets were not checked: the key ring has no recorded Keychain item to open it with from here.");
            return null;
        }

        KeyRingProtection protection;
        try
        {
            protection = new KeyRingProtectionResolver(effective, folder, KeyRingProtectionResolver.CurrentPlatform, name => Environment.GetEnvironmentVariable(name), account => new MacKeychainKeySource(account, createIfMissing: false)).Resolve();
        }
        catch (KeyRingProtectionRequiredException ex)
        {
            report.Add(section, CheckLevel.Warn, $"The stored secrets could not be checked from here: {ex.Message}");
            return null;
        }

        if (protection is NoKeyRingProtection none && none.Reason == UnprotectedReason.Unavailable)
        {
            report.Add(section, CheckLevel.Warn, $"The stored secrets could not be checked from here, because the key ring's protection cannot be opened in this shell: {none.Description} Run this check with the same environment the service has.");
            return null;
        }

        return protection;
    }

    private static void CheckKeyEncryptionKey(CheckReport report, string section, DraughtsmanConfig config)
    {
        string? value = Environment.GetEnvironmentVariable(config.KeyRing.KeyEnvironmentVariable);
        bool fromFile = !string.IsNullOrWhiteSpace(config.KeyRing.KeyFile);
        if (!fromFile && string.IsNullOrWhiteSpace(value))
        {
            report.Add(section, CheckLevel.Warn, $"keyRing.protection is 'key' but neither the environment variable {config.KeyRing.KeyEnvironmentVariable} nor keyRing.keyFile supplies the key-encryption key in this shell. The server would start without protection and the stored AI key would need entering again. Run this check with the same environment the service has.");
            return;
        }

        if (fromFile && !File.Exists(config.KeyRing.KeyFile))
        {
            report.Add(section, CheckLevel.Warn, $"keyRing.keyFile {config.KeyRing.KeyFile} does not exist.");
            return;
        }

        report.Add(section, CheckLevel.Ok, "A key-encryption key is configured (its value is never shown).");
    }
}

/// <summary>What <see cref="ConfigCheckCommand"/> found: lines for a person, and the facts a test or script reads.</summary>
public sealed class CheckReport(string dataFolder)
{
    private readonly List<CheckLine> _lines = [];

    public string DataFolder { get; } = dataFolder;

    public IReadOnlyList<CheckLine> Lines => _lines;

    public CheckDatabase? Database { get; set; }

    public bool NewerThanBuild { get; set; }

    public string? KeyRing { get; set; }

    /// <summary>One entry per stored secret: its name and whether it is current, still tied to an install folder (<c>legacy</c>) or unreadable.</summary>
    public List<string> Secrets { get; } = [];

    public bool WouldStart => _lines.All(l => l.Level != CheckLevel.Refuse);

    /// <summary>0 would start, 3 the database is newer than this build, 1 any other refusal.</summary>
    public int ExitCode => NewerThanBuild ? Maintenance.StorageStartup.SchemaNewerExitCode : WouldStart ? 0 : 1;

    public void Add(string section, CheckLevel level, string message)
    {
        _lines.Add(new CheckLine(section, level, message));
    }

    public string ToText()
    {
        BuildInfo build = BuildInfo.FromAssembly();
        var text = new StringBuilder();
        text.Append("Draughtsman config check: this is version ").Append(build.Version).Append(" (build ").Append(build.BuildDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(")\n");
        text.Append("Data folder: ").Append(DataFolder).Append('\n');
        string? section = null;
        foreach (CheckLine line in _lines)
        {
            if (line.Section != section)
            {
                section = line.Section;
                text.Append('\n').Append(section).Append('\n');
            }

            string tag = line.Level switch { CheckLevel.Ok => "OK    ", CheckLevel.Info => "INFO  ", CheckLevel.Warn => "WARN  ", _ => "REFUSE" };
            text.Append("  ").Append(tag).Append(' ').Append(line.Message.Replace("\n", "\n          ", StringComparison.Ordinal)).Append('\n');
        }

        int warnings = _lines.Count(l => l.Level == CheckLevel.Warn);
        int refusals = _lines.Count(l => l.Level == CheckLevel.Refuse);
        text.Append('\n');
        text.Append(refusals > 0
            ? $"Result: this version would NOT start on this data folder ({refusals} refusal(s), {warnings} warning(s)). Nothing has been changed.\n"
            : $"Result: this version would start on this data folder ({warnings} warning(s)).\n");
        return text.ToString();
    }

    public string ToJson()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, NewLine = "\n" };
        var shape = new
        {
            wouldStart = WouldStart,
            exitCode = ExitCode,
            dataFolder = DataFolder,
            database = Database,
            keyRing = KeyRing,
            storedSecrets = Secrets,
            lines = _lines.Select(l => new { section = l.Section, level = l.Level.ToString().ToLowerInvariant(), message = l.Message }),
        };
        return JsonSerializer.Serialize(shape, options);
    }
}

/// <summary>The schema facts of the database: how many migrations this build has, how many are applied, which are pending and which are unknown to this build.</summary>
public sealed record CheckDatabase(string Provider, int Known)
{
    public int Applied { get; init; }

    public IReadOnlyList<string> Pending { get; init; } = [];

    public IReadOnlyList<string> Newer { get; init; } = [];
}
