using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Cli;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Hosting;
using Draughtsman.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Draughtsman.Server.Diagnostics;

/// <summary>One file of a support bundle: its name in the zip and its text.</summary>
public sealed record BundleEntry(string Name, string Content);

/// <summary>What <see cref="SupportBundle.BuildAsync"/> takes beyond the data folder. Every member has a default; tests replace the machine's answers.</summary>
public sealed class SupportBundleOptions
{
    public int LogLines { get; init; } = SupportBundle.DefaultLogLines;

    public DoctorProbes? Probes { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public DateTimeOffset? Now { get; init; }
}

/// <summary>
/// The support bundle (<c>draughtsman support-bundle</c>, docs/troubleshooting.md): one zip a customer can read through and send, built from a data folder
/// whether the server is running or not. What it holds is listed in its own README; the rule behind the list is that it carries NO secret and NO content:
/// never the database, a document, the key ring, a password hash, a session or API token, the setup code, the settings file itself
/// or any Postgres connection string. Everything free-form (logs, security events, listings) goes through <see cref="Redactor"/>, the settings through an
/// allow-list (<see cref="EffectiveSettings"/>), and every value this process can read that is a secret (an environment variable named for one, the
/// connection string's password, the setup code file) is scrubbed from every entry by value as well, so even a secret whose shape no rule knows is gone.
/// </summary>
public static class SupportBundle
{
    public const int DefaultLogLines = 2000;

    public const int SecurityEventCount = 50;

    public static readonly TimeSpan SecurityEventWindow = TimeSpan.FromDays(7);

    /// <summary>The most a bundle reads of one log file, from its end: a log gone wild must not make the command read a gigabyte.</summary>
    private const long MaxLogBytesRead = 32L * 1024 * 1024;

    private const int ListingCap = 500;

    /// <summary>The names of the files in every bundle, in order. The README is generated last and lists the files that were actually written.</summary>
    public static readonly IReadOnlyList<string> FileNames =
    [
        "README.txt",
        "system.txt",
        "doctor.txt",
        "settings-effective.txt",
        "health.txt",
        "log-tail.txt",
        "security-events.txt",
        "proxy-headers.txt",
        "data-folder-listing.txt",
        "migrations.txt",
    ];

    /// <summary>Folders whose contents are listed by name: nothing in them is named for a person or holds a secret in its name.</summary>
    private static readonly string[] s_listedFolders = ["logs", "backups", "diagnostics", "themes"];

    public static async Task<IReadOnlyList<BundleEntry>> BuildAsync(string folder, SupportBundleOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(options);
        DateTimeOffset now = options.Now ?? DateTimeOffset.UtcNow;
        DoctorProbes probes = options.Probes ?? DoctorProbes.Real();
        IReadOnlyDictionary<string, string> environment = options.Environment ?? EnvironmentSnapshot();

        (DraughtsmanConfig config, IReadOnlyList<ConfigWarning> warnings, bool readable) = LoadConfig(folder);
        IReadOnlyList<string> known = KnownSecrets(config, folder, environment);

        DoctorReport doctor = await DoctorCommand.CollectAsync(folder, probes, cancellationToken);
        // The same care as the doctor's: never open the database as root where that would take its -wal and -shm files from the server (DatabaseGuard).
        string? databaseFile = TryDatabaseFile(config, folder);
        bool openDatabase = DatabaseGuard.SafeToOpen(databaseFile, probes.Account().CurrentUid, out string? notOpened);
        string yamlPath = Path.Combine(folder, DraughtsmanConfig.FileName);
        bool yamlExists = File.Exists(yamlPath);
        long yamlBytes = yamlExists ? new FileInfo(yamlPath).Length : 0;

        var entries = new List<BundleEntry>
        {
            Entry("system.txt", SystemInfo(folder, probes.Account(), environment, now), known),
            Entry("doctor.txt", doctor.ToText(verbose: true), known),
            Entry("settings-effective.txt", EffectiveSettings.Describe(config, yamlExists, yamlBytes, warnings, EnvironmentLines(environment)) + (readable ? string.Empty : "\nNOTE: draughtsman.yaml could not be read, so these are the defaults.\n"), known, truncateAddresses: false, redact: false),
            Entry("health.txt", await Health(config, probes, cancellationToken), known),
            Entry("log-tail.txt", LogTail(folder, options.LogLines), known),
            Entry("security-events.txt", openDatabase ? await SecurityEvents(config, folder, now, cancellationToken) : "Security events were not read. " + notOpened + "\n", known),
            Entry("proxy-headers.txt", ProxyHeaders(folder), known),
            Entry("data-folder-listing.txt", Listing(folder), known),
            Entry("migrations.txt", openDatabase ? Migrations(config, folder, doctor) : "The migrations were not read from the database. " + notOpened + "\n", known),
        };

        entries.Insert(0, Entry("README.txt", Readme(config, entries, now, doctor.DataFolder), known, redact: false));
        return entries;
    }

    /// <summary>Writes the entries as a zip. The file is created owner-only on Unix and never replaces one that exists.</summary>
    public static void WriteZip(string path, IEnumerable<BundleEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(entries);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (BundleEntry entry in entries)
        {
            ZipArchiveEntry file = zip.CreateEntry(entry.Name, CompressionLevel.Optimal);
            file.LastWriteTime = now;
            using Stream content = file.Open();
            content.Write(new UTF8Encoding(false).GetBytes(entry.Content));
        }
    }

    // ------------------------------------------------------------------------------------------------------ finishing an entry

    /// <summary>
    /// Scrubs known secret values, redacts, and repeats until nothing changes (the redactor is idempotent, so two passes is the norm). The
    /// line breaks are the same on every platform.
    /// </summary>
    private static BundleEntry Entry(string name, string content, IReadOnlyList<string> knownSecrets, bool truncateAddresses = true, bool redact = true)
    {
        string text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        for (int pass = 0; pass < 4; pass++)
        {
            // A file whose structure this code controls (the README, the settings listing) is cleaned value by value as it is written and is not run through the
            // free-text rules, which would blank its own words ("the setup code", "Bearer"); known secrets are still removed from it by value.
            string next = redact ? Redactor.Redact(Scrub(text, knownSecrets), truncateAddresses) : Scrub(text, knownSecrets);
            if (string.Equals(next, text, StringComparison.Ordinal))
            {
                break;
            }

            text = next;
        }

        return new BundleEntry(name, text.EndsWith('\n') ? text : text + "\n");
    }

    /// <summary>Removes every occurrence of a value this process knows to be a secret, whatever it looks like.</summary>
    public static string Scrub(string text, IReadOnlyList<string> secrets)
    {
        foreach (string secret in secrets)
        {
            text = text.Replace(secret, Redactor.Redacted, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// The secret values this process can read: the value of every environment variable named for a secret or one the settings say holds one, the contents of a
    /// password or key file the settings point at, the storage connection string (and its password), and the setup code file. Short values are
    /// left out (scrubbing "1" would shred the text); longest first, so a value that contains another is removed whole.
    /// </summary>
    public static IReadOnlyList<string> KnownSecrets(DraughtsmanConfig config, string folder, IReadOnlyDictionary<string, string> environment)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 6)
            {
                values.Add(value.Trim());
            }
        }

        string[] namedBySettings = [config.Ai.ApiKeyEnvironmentVariable, config.Email.Smtp.PasswordEnvironmentVariable, config.KeyRing.KeyEnvironmentVariable, config.KeyRing.Certificate.PasswordEnvironmentVariable];
        foreach ((string name, string value) in environment)
        {
            if (namedBySettings.Contains(name, StringComparer.OrdinalIgnoreCase) || LooksSecretName(name))
            {
                Add(value);
            }
        }

        Add(config.Storage.ConnectionString);
        if (config.Storage.ConnectionString.Length > 0)
        {
            try
            {
                Add(new NpgsqlConnectionStringBuilder(config.Storage.ConnectionString).Password);
            }
            catch (ArgumentException)
            {
                // Not a Postgres connection string; the whole of it is already in the list.
            }
        }

        foreach (string file in new[] { config.Email.Smtp.PasswordFile, config.KeyRing.KeyFile })
        {
            if (!string.IsNullOrWhiteSpace(file))
            {
                Add(ReadSmall(file));
            }
        }

        foreach (string line in (ReadSmall(Path.Combine(folder, "setup-code.txt")) ?? string.Empty).Split('\n'))
        {
            // The file says what it is on its first lines; the code is a token on one of them. Every token is removed.
            foreach (string word in line.Split([' ', '\t', ':', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 8 && word.All(char.IsAsciiLetterOrDigit))
                {
                    Add(word);
                }
            }
        }

        return [.. values.OrderByDescending(v => v.Length).ThenBy(v => v, StringComparer.Ordinal)];
    }

    internal static bool LooksSecretName(string name)
    {
        string upper = name.ToUpperInvariant();
        return new[] { "PASSWORD", "PASSWD", "SECRET", "TOKEN", "KEY", "CREDENTIAL", "CONNECTION", "CERTIFICATE", "AUTH" }.Any(upper.Contains);
    }

    private static string? ReadSmall(string path)
    {
        try
        {
            FileInfo info = new(path);
            return info.Exists && info.Length <= 64 * 1024 ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------------ the files

    private static string SystemInfo(string folder, AccountFacts account, IReadOnlyDictionary<string, string> environment, DateTimeOffset now)
    {
        BuildInfo build = BuildInfo.FromAssembly();
        var text = new StringBuilder();
        text.Append("Version: ").Append(build.Version).Append('\n');
        text.Append("Build date: ").Append(build.BuildDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("Platform: ").Append(RuntimeInformation.OSDescription).Append(' ').Append(RuntimeInformation.OSArchitecture).Append('\n');
        text.Append(".NET runtime: ").Append(RuntimeInformation.FrameworkDescription).Append('\n');
        text.Append("Host: ").Append(HostKinds.Name(HostKinds.Detect())).Append('\n');
        text.Append("In a container: ").Append(account.Container ? "yes" : "no").Append('\n');
        text.Append("Bundle made (UTC): ").Append(now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("Data folder: ").Append(folder).Append('\n');
        text.Append("This command runs as: ").Append(account.CurrentName).Append(account.CurrentUid is { } uid ? " (uid " + uid.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty).Append('\n');
        text.Append("Server process found on this machine: ").Append(account.ServerUid is { } server ? "yes, uid " + server.ToString(CultureInfo.InvariantCulture) : "no").Append('\n');
        text.Append("Time zone: ").Append(TimeZoneInfo.Local.Id).Append('\n');
        text.Append("Environment variable names set: ").Append(string.Join(", ", environment.Keys.Where(IsRelevant).Order(StringComparer.Ordinal))).Append('\n');
        return text.ToString();
    }

    /// <summary>The variables a support request cares about: Draughtsman's, the host's address and the container marker.</summary>
    private static bool IsRelevant(string name)
    {
        return name.StartsWith("DRAUGHTSMAN_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> EnvironmentLines(IReadOnlyDictionary<string, string> environment)
    {
        // Only these are shown by value: none can hold a secret.
        string[] shown = ["ASPNETCORE_URLS", "DRAUGHTSMAN_DATA", "DRAUGHTSMAN_KEY_RING_PROTECTION", "DOTNET_RUNNING_IN_CONTAINER"];
        return
        [
            .. environment.Where(kv => IsRelevant(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new KeyValuePair<string, string>(kv.Key, shown.Contains(kv.Key, StringComparer.OrdinalIgnoreCase) ? kv.Value : "(set)")),
        ];
    }

    private static IReadOnlyDictionary<string, string> EnvironmentSnapshot()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in global::System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static (DraughtsmanConfig Config, IReadOnlyList<ConfigWarning> Warnings, bool Readable) LoadConfig(string folder)
    {
        try
        {
            (DraughtsmanConfig config, IReadOnlyList<ConfigWarning> warnings) = ConfigLoader.LoadWithWarnings(folder);
            return (config, warnings, true);
        }
        catch (Exception error) when (error is YamlDotNet.Core.YamlException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (new DraughtsmanConfig(), [], false);
        }
    }

    private static async Task<string> Health(DraughtsmanConfig config, DoctorProbes probes, CancellationToken cancellationToken)
    {
        int port = config.Server.Port;
        string? urls = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(urls) && urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(DoctorCommand.PortOf).FirstOrDefault(p => p > 0) is { } fromUrls and > 0)
        {
            port = fromUrls;
        }

        LoopbackAnswer answer = await probes.Loopback(port);
        cancellationToken.ThrowIfCancellationRequested();
        var text = new StringBuilder();
        text.Append("GET /api/health on 127.0.0.1:").Append(port.ToString(CultureInfo.InvariantCulture)).Append(" (anonymous, from this machine, no sign-in):\n");
        text.Append(!answer.Reachable
            ? "Nothing answered. The server is not running here, or it listens on another address.\n"
            : answer.IsDraughtsman
                ? $"A Draughtsman server answered: status {answer.Health}.\n"
                : $"Something answered HTTP {(answer.Status?.ToString(CultureInfo.InvariantCulture) ?? "?")} that is not a Draughtsman health document.\n");
        text.Append("Only the status word is available without signing in; capabilities and detail need an administrator (Admin > Status).\n");
        return text.ToString();
    }

    private static string? TryDatabaseFile(DraughtsmanConfig config, string folder)
    {
        try
        {
            return SelfCheckService.DatabaseLocation(config, folder).File;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string LogTail(string folder, int lines)
    {
        string logs = Path.Combine(folder, "logs");
        string[] files = Directory.Exists(logs)
            ? [.. Directory.EnumerateFiles(logs, "draughtsman-*.log").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ThenByDescending(f => f, StringComparer.Ordinal)]
            : [];
        if (files.Length == 0)
        {
            return "There is no log file to include: this server writes none (logging.file.enabled is false, the default; it logs to the console or the service manager).\n"
                + "In Docker, the container's own log is the one to send: docker logs <container name> 2>&1 | tail -n " + lines.ToString(CultureInfo.InvariantCulture) + " > server.log\n"
                + "To keep a log file in the data folder from now on, set logging.file.enabled: true in draughtsman.yaml and restart.\n";
        }

        var collected = new List<string>();
        var used = new List<string>();
        foreach (string file in files)
        {
            if (collected.Count >= lines)
            {
                break;
            }

            string[] fileLines = ReadTail(file, lines - collected.Count);
            used.Add(Path.GetFileName(file));
            collected.InsertRange(0, fileLines);
        }

        var text = new StringBuilder();
        text.Append("The last ").Append(collected.Count.ToString(CultureInfo.InvariantCulture)).Append(" lines of ").Append(string.Join(", ", used.AsEnumerable().Reverse())).Append(" (oldest first). Redacted: emails, tokens, passwords, cookies, keys and long random strings are removed or masked, addresses are cut to a /24 or /64.\n\n");
        foreach (string line in collected)
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    private static string[] ReadTail(string path, int lines)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool partial = stream.Length > MaxLogBytesRead;
            if (partial)
            {
                stream.Seek(-MaxLogBytesRead, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var queue = new Queue<string>(lines + 1);
            bool first = true;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (first && partial)
                {
                    first = false;
                    continue; // the first line of a seek is cut in half
                }

                first = false;
                queue.Enqueue(line);
                if (queue.Count > lines)
                {
                    queue.Dequeue();
                }
            }

            return [.. queue];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [$"(could not read {Path.GetFileName(path)}: {error.GetType().Name})"];
        }
    }

    private static async Task<string> SecurityEvents(DraughtsmanConfig config, string folder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.Append("Security events (sign-ins, password and role changes, tokens). Emails are cut to the first character and the domain; addresses are cut to a /24 or /64; no password, token or session value is ever recorded in this table.\n\n");
        try
        {
            StorageOptions storage = StorageOptions.FromConfig(config.Storage.Provider, config.Storage.ConnectionString, folder);
            if (storage.Provider == StorageProvider.Sqlite)
            {
                string file = Path.GetFullPath(new SqliteConnectionStringBuilder(storage.ConnectionString).DataSource);
                if (!File.Exists(file))
                {
                    return text.Append("There is no database file yet, so there are no events.\n").ToString();
                }

                // Read-only, and immutable when no write-ahead log holds newer pages: opening a database never creates a -wal or -shm file beside it.
                bool hasLog = File.Exists(file + "-wal") && new FileInfo(file + "-wal").Length > 0;
                var builder = new SqliteConnectionStringBuilder { DataSource = hasLog ? file : new Uri(file).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false };
                storage = new StorageOptions(StorageProvider.Sqlite, builder.ToString());
            }

            await using DraughtsmanDbContext db = StorageSetup.CreateContext(storage);
            DateTimeOffset since = now - SecurityEventWindow;
            DateTime sinceUtc = DateTime.SpecifyKind(since.UtcDateTime, DateTimeKind.Unspecified);
            var counts = await db.SecurityEvents.AsNoTracking().Where(e => e.At >= sinceUtc).GroupBy(e => e.Type).Select(g => new { Type = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
            text.Append("Counts by type, last 7 days:\n");
            if (counts.Count == 0)
            {
                text.Append("  (no events)\n");
            }

            foreach (var count in counts.OrderByDescending(c => c.Count).ThenBy(c => c.Type, StringComparer.Ordinal))
            {
                text.Append("  ").Append(count.Count.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append("  ").Append(count.Type).Append('\n');
            }

            SecurityEventPage page = await new EfSecurityEventStore(db).List(new SecurityEventQuery(Limit: SecurityEventCount), cancellationToken);
            text.Append("\nThe last ").Append(page.Items.Count.ToString(CultureInfo.InvariantCulture)).Append(" events (newest first), of ").Append(page.Total.ToString(CultureInfo.InvariantCulture)).Append(" in all:\n");
            foreach (SecurityEvent item in page.Items)
            {
                text.Append("  ").Append(item.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                    .Append(' ').Append(item.Type)
                    .Append(" via ").Append(item.Method ?? "-")
                    .Append(" from ").Append(item.SourceIp is { } ip ? Redactor.TruncateAddress(ip) : "-")
                    .Append(" actor ").Append(item.ActorLabel is { } actor ? Redactor.MaskEmail(actor) : "-")
                    .Append(" target ").Append(item.TargetLabel is { } target && !IsAboutADocument(item.Type) ? Redactor.MaskEmail(target) : "-");
                // A document event's target is the document's title and its detail names it: content, not diagnosis.
                if (!string.IsNullOrWhiteSpace(item.Detail) && !IsAboutADocument(item.Type))
                {
                    text.Append(" - ").Append(item.Detail.ReplaceLineEndings(" "));
                }

                text.Append('\n');
            }
        }
        catch (Exception error) when (error is SqliteException or NpgsqlException or InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException or System.Net.Sockets.SocketException or DbUpdateException)
        {
            text.Append("The events could not be read (").Append(error.GetType().Name).Append("). The database may be in use, absent or not yet migrated.\n");
        }

        return text.ToString();
    }

    private static bool IsAboutADocument(string type)
    {
        return type.StartsWith("document.", StringComparison.Ordinal);
    }

    private static string ProxyHeaders(string folder)
    {
        ForwardedObservationsReport? report = ForwardedObservations.Read(folder);
        var text = new StringBuilder();
        text.Append("Proxy headers the server has seen since it started (X-Forwarded-For, X-Forwarded-Proto, X-Forwarded-Host, CF-Connecting-IP), counted per direct peer,\n");
        text.Append("with whether the peer was trusted (server.knownProxies). Addresses are cut to a /24 or /64. No header's content is kept except the scheme word of X-Forwarded-Proto.\n\n");
        if (report is null)
        {
            return text.Append("No observations are recorded yet: the server writes them to the data folder every 30 seconds while requests arrive, so a server that started moments ago, or has not run, or is older than this feature, has none to give.\n").ToString();
        }

        text.Append("Server started (UTC): ").Append(report.Since.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("Recorded (UTC):       ").Append(report.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("Requests from peers beyond the ").Append(ForwardedObservations.MaxPeers.ToString(CultureInfo.InvariantCulture)).Append(" tracked: ").Append(report.OtherPeerRequests.ToString(CultureInfo.InvariantCulture)).Append("\n\n");
        foreach (PeerObservation peer in report.Peers)
        {
            text.Append(Redactor.TruncateAddress(peer.Peer)).Append(": trusted ").Append(peer.Trusted ? "yes" : "no")
                .Append(", requests ").Append(peer.Requests.ToString(CultureInfo.InvariantCulture))
                .Append(", with proxy headers ").Append(peer.ForwardedRequests.ToString(CultureInfo.InvariantCulture))
                .Append(" (proto ").Append(peer.WithProto.ToString(CultureInfo.InvariantCulture))
                .Append(", for ").Append(peer.WithFor.ToString(CultureInfo.InvariantCulture))
                .Append(", host ").Append(peer.WithHost.ToString(CultureInfo.InvariantCulture))
                .Append(", cf-connecting-ip ").Append(peer.WithCfConnectingIp.ToString(CultureInfo.InvariantCulture))
                .Append("), schemes seen [").Append(string.Join(", ", peer.ProtoValues)).Append("], first ")
                .Append(peer.FirstSeen.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(", last ")
                .Append(peer.LastSeen.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        }

        if (report.Peers.Count == 0)
        {
            text.Append("(no peers)\n");
        }

        return text.ToString();
    }

    private static string Listing(string folder)
    {
        var text = new StringBuilder();
        text.Append("The data folder: names, sizes, modes and owners. No file's content. Folders that hold documents' assets, keys or mail are shown as a count.\n\n");
        if (!Directory.Exists(folder))
        {
            return text.Append(folder).Append(" does not exist.\n").ToString();
        }

        text.Append(folder).Append("/\n");
        int written = 0;
        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal))
            {
                if (written++ >= ListingCap)
                {
                    text.Append("  (more entries not listed)\n");
                    break;
                }

                string name = Path.GetFileName(path);
                bool isFolder = Directory.Exists(path);
                text.Append("  ").Append(Line(path, name, isFolder)).Append('\n');
                if (!isFolder)
                {
                    continue;
                }

                if (s_listedFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal).Take(100))
                    {
                        text.Append("    ").Append(Line(child, Path.GetFileName(child), Directory.Exists(child))).Append('\n');
                    }
                }
                else
                {
                    int count = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Take(100_000).Count();
                    text.Append("    (").Append(count.ToString(CultureInfo.InvariantCulture)).Append(count == 1 ? " entry inside, not listed)\n" : " entries inside, not listed)\n");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            text.Append("  (the listing stopped: ").Append(error.GetType().Name).Append(")\n");
        }

        return text.ToString();
    }

    private static string Line(string path, string name, bool isFolder)
    {
        long size = 0;
        if (!isFolder)
        {
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Listed with no size.
            }
        }

        string mode = FileOwners.Mode(path);
        return $"{mode,-9} {FileOwners.Describe(FileOwners.Of(path)),-26} {(isFolder ? "-" : MachineChecks.Size(size)),10}  {name}{(isFolder ? "/" : string.Empty)}";
    }

    private static string Migrations(DraughtsmanConfig config, string folder, DoctorReport doctor)
    {
        var text = new StringBuilder();
        text.Append("Database migrations. [applied] ran on this database; [pending] would run at the next start; [unknown to this build] were applied by a newer version.\n\n");
        try
        {
            StorageOptions storage = StorageOptions.FromConfig(config.Storage.Provider, config.Storage.ConnectionString, folder);
            using DraughtsmanDbContext db = StorageSetup.CreateContext(storage);
            string[] known = [.. db.Database.GetMigrations().Order(StringComparer.Ordinal)];
            text.Append("Provider: ").Append(storage.ProviderName).Append(", ").Append(known.Length.ToString(CultureInfo.InvariantCulture)).Append(" migrations in this build.\n");
            List<string>? applied = null;
            if (storage.Provider == StorageProvider.Sqlite)
            {
                string file = Path.GetFullPath(new SqliteConnectionStringBuilder(storage.ConnectionString).DataSource);
                if (File.Exists(file))
                {
                    bool hasLog = File.Exists(file + "-wal") && new FileInfo(file + "-wal").Length > 0;
                    applied = ConfigCheckCommand.ReadApplied(file, immutable: !hasLog);
                }
            }

            if (applied is null)
            {
                text.Append(storage.Provider == StorageProvider.Sqlite ? "There is no database file yet; nothing is applied.\n\n" : "The applied list is not read for Postgres in a bundle; see the Database lines of doctor.txt.\n\n");
                foreach (string migration in known)
                {
                    text.Append("  ").Append(migration).Append('\n');
                }

                return text.ToString();
            }

            text.Append('\n');
            foreach (string migration in known)
            {
                text.Append("  [").Append(applied.Contains(migration, StringComparer.Ordinal) ? "applied" : "pending").Append("] ").Append(migration).Append('\n');
            }

            foreach (string unknown in applied.Where(m => !known.Contains(m, StringComparer.Ordinal)))
            {
                text.Append("  [unknown to this build] ").Append(unknown).Append('\n');
            }
        }
        catch (Exception error) when (error is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            text.Append("The migrations could not be read (").Append(error.GetType().Name).Append(").\n");
        }

        if (doctor.Items.FirstOrDefault(i => i.Id == "migrations") is { } summary)
        {
            text.Append('\n').Append(summary.Summary).Append('\n');
        }

        return text.ToString();
    }

    private static string Readme(DraughtsmanConfig config, IReadOnlyList<BundleEntry> entries, DateTimeOffset now, string folder)
    {
        BuildInfo build = BuildInfo.FromAssembly();
        string contact = SafeContact(config.About.SupportContact);
        var text = new StringBuilder();
        text.Append("Draughtsman support bundle\n");
        text.Append("==========================\n\n");
        text.Append("Made ").Append(now.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("Z by Draughtsman ").Append(build.Version).Append(" (build ").Append(build.BuildDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(") from the data folder ").Append(folder).Append(".\n");
        text.Append("Please read each file before you send it. Send the whole zip to ").Append(contact).Append(".\n\n");
        text.Append("WHAT IS IN THIS ZIP (")
            .Append((entries.Count + 1).ToString(CultureInfo.InvariantCulture)).Append(" files, all plain text)\n");
        text.Append("  README.txt               this file\n");
        foreach (BundleEntry entry in entries)
        {
            text.Append("  ").Append(entry.Name.PadRight(24)).Append(' ').Append(Describe(entry.Name)).Append(" (").Append(Encoding.UTF8.GetByteCount(entry.Content).ToString(CultureInfo.InvariantCulture)).Append(" bytes)\n");
        }

        text.Append("\nWHAT IS NOT IN THIS ZIP, ever\n");
        text.Append("  - the database, and so no document, diagram, comment or user list\n");
        text.Append("  - password hashes, session cookies, API tokens, password-reset links\n");
        text.Append("  - the key ring (keys/), the stored AI key or SMTP password, any environment variable's value except the few that cannot hold a secret\n");
        text.Append("  - the setup code for first sign-in\n");
        text.Append("  - draughtsman.yaml itself (only an allow-listed summary of its settings)\n");
        text.Append("  - the content of any file in the data folder (the listing has names, sizes, modes and owners only)\n");
        text.Append("  - a Postgres connection string or its password\n");
        text.Append("  - the contents of the server's console log when it is not written to a file (in Docker, see log-tail.txt)\n\n");
        text.Append("HOW IT WAS CLEANED\n");
        text.Append("  - Email addresses are cut to the first character and the domain (e***@example.com).\n");
        text.Append("  - Anything after a word like password, secret, token, api key, session, cookie or authorization, a Bearer or Basic credential, a URL's user and password,\n");
        text.Append("    API-key and token shapes, JWTs, long random-looking strings, long hex strings and the first-run code's shape are removed or masked.\n");
        text.Append("  - Network addresses are cut to the /24 (IPv4) or /64 (IPv6) they belong to. Loopback addresses are left as they are.\n");
        text.Append("  - Every secret this command could read on this machine (environment variables named for one, password and key files the settings point at, the setup code) is removed by value from every file.\n");
        text.Append("  This catches what it knows to look for. A free-form line in log-tail.txt could still hold something no rule can recognise, such as a password inside a sentence: that is why you read it first.\n");
        return text.ToString();
    }

    /// <summary>
    /// The support contact the settings name (<c>about.supportContact</c>, shown to every signed-in person on the About page) is the address the README asks the sender to write to,
    /// so it is left as written, as an address the operator chose for exactly this: only control characters and length are cleaned, and a web address has any user and password taken out.
    /// </summary>
    private static string SafeContact(string? contact)
    {
        string? cleaned = RoundTripChecks.Clean(contact);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "your Draughtsman supplier";
        }

        return cleaned.Contains("://", StringComparison.Ordinal) ? Redactor.Redact(cleaned, truncateAddresses: false) : cleaned;
    }

    private static string Describe(string name)
    {
        return name switch
        {
            "system.txt" => "version, build date, platform, container detection, account",
            "doctor.txt" => "the output of `draughtsman doctor --verbose`",
            "settings-effective.txt" => "settings (allow-listed values; others only set / not set)",
            "health.txt" => "whether the server answers on this machine, and its status word",
            "log-tail.txt" => "the last lines of the log file, redacted",
            "security-events.txt" => "sign-in and account events: counts for 7 days, the last 50, emails and addresses cut",
            "proxy-headers.txt" => "which proxy addresses sent X-Forwarded-* headers, and whether they were trusted",
            "data-folder-listing.txt" => "names, sizes, modes and owners in the data folder",
            "migrations.txt" => "database migrations: applied, pending, unknown",
            _ => string.Empty,
        };
    }
}
