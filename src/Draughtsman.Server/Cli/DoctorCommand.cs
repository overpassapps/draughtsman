using System.Globalization;
using System.Text;
using System.Text.Json;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Diagnostics;
using Draughtsman.Server.Hosting;
using YamlDotNet.Core;

namespace Draughtsman.Server.Cli;

/// <summary>What the doctor asks the machine, in one place so a test can answer for it.</summary>
public sealed record DoctorProbes(Func<AccountFacts> Account, Func<int, Task<LoopbackAnswer>> Loopback, Func<DateTimeOffset> Now, Func<string, (long? Free, long? Total)>? Disk = null)
{
    public static DoctorProbes Real(string? hostHeader = null)
    {
        return new DoctorProbes(
            () => new AccountFacts(FileOwners.CurrentUserId(), FileOwners.CurrentUserName(), ServerProcess.FindServerUid(Environment.ProcessId), HostKinds.Detect() == HostKind.Container, OperatingSystem.IsWindows()),
            port => LoopbackProbe.ProbeAsync(port, hostHeader),
            () => DateTimeOffset.UtcNow);
    }
}

/// <summary>What <see cref="DoctorCommand"/> found, as lines for a person and as data.</summary>
public sealed class DoctorReport(string dataFolder, IReadOnlyList<DiagnosticItem> items)
{
    public string DataFolder { get; } = dataFolder;

    public IReadOnlyList<DiagnosticItem> Items { get; } = items;

    public int Failures => Items.Count(i => i.Status == DiagnosticStatus.Fail);

    public int Warnings => Items.Count(i => i.Status == DiagnosticStatus.Warn);

    /// <summary>0 no problems, 1 warnings, 2 failures.</summary>
    public int ExitCode => DoctorChecks.ExitCode(Items);

    public string ToText(bool verbose = false)
    {
        BuildInfo build = BuildInfo.FromAssembly();
        var text = new StringBuilder();
        text.Append("Draughtsman doctor: version ").Append(build.Version).Append(" (build ").Append(build.BuildDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("), ")
            .Append(System.Runtime.InteropServices.RuntimeInformation.OSDescription).Append(' ').Append(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture).Append('\n');
        text.Append("Data folder: ").Append(DataFolder).Append('\n');
        string? title = null;
        foreach (DiagnosticItem item in Items)
        {
            if (item.Title != title)
            {
                title = item.Title;
                text.Append('\n').Append(title).Append('\n');
            }

            string tag = item.Status switch { DiagnosticStatus.Pass => "OK  ", DiagnosticStatus.Info => "INFO", DiagnosticStatus.Warn => "WARN", _ => "FAIL" };
            AppendWrapped(text, "  " + tag + "  ", "        ", item.Summary);
            if (item.Detail is { Count: > 0 })
            {
                foreach (string line in item.Detail)
                {
                    text.Append("        ").Append(line).Append('\n');
                }
            }

            if (item.Fix is { IsEmpty: false } fix && item.Status is DiagnosticStatus.Warn or DiagnosticStatus.Fail)
            {
                AppendFix(text, fix);
            }

            if (verbose && item.Facts is { Count: > 0 })
            {
                foreach ((string key, string value) in item.Facts)
                {
                    text.Append("        . ").Append(key).Append(": ").Append(value).Append('\n');
                }
            }
        }

        text.Append('\n');
        text.Append(Failures > 0
            ? $"Result: {Failures.ToString(CultureInfo.InvariantCulture)} failure(s) and {Warnings.ToString(CultureInfo.InvariantCulture)} warning(s). Exit code 2.\n"
            : Warnings > 0
                ? $"Result: {Warnings.ToString(CultureInfo.InvariantCulture)} warning(s), no failures. Exit code 1.\n"
                : "Result: no problems found. Exit code 0.\n");
        return text.ToString();
    }

    public string ToJson()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, NewLine = "\n", DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.Serialize(new { exitCode = ExitCode, dataFolder = DataFolder, failures = Failures, warnings = Warnings, items = Items }, options);
    }

    private static void AppendFix(StringBuilder text, DiagnosticFix fix)
    {
        foreach ((string label, string? body) in new[] { ("draughtsman.yaml", fix.Yaml), ("command", fix.Shell), ("nginx", fix.Nginx), ("Caddy", fix.Caddy), ("Cloudflare", fix.Cloudflare) })
        {
            if (body is null)
            {
                continue;
            }

            text.Append("        Fix (").Append(label).Append("):\n");
            foreach (string line in body.Split('\n'))
            {
                text.Append("          ").Append(line).Append('\n');
            }
        }
    }

    /// <summary>Wraps at a terminal's width, with a hanging indent. A long path or address is never broken.</summary>
    private static void AppendWrapped(StringBuilder text, string first, string rest, string message)
    {
        const int Width = 100;
        string indent = first;
        int column = indent.Length;
        text.Append(indent);
        bool lineStart = true;
        foreach (string word in message.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!lineStart && column + 1 + word.Length > Width)
            {
                text.Append('\n').Append(rest);
                column = rest.Length;
                lineStart = true;
            }

            if (!lineStart)
            {
                text.Append(' ');
                column++;
            }

            text.Append(word);
            column += word.Length;
            lineStart = false;
        }

        text.Append('\n');
    }
}

/// <summary>
/// <c>draughtsman doctor</c> (docs/troubleshooting.md, "Troubleshooting an install"): every check that needs no network and no sign-in, in one
/// run, readable by a person and by <c>support-bundle</c>. It works with the server stopped, which is exactly when a customer needs it,
/// and inside <c>docker exec</c>. It reads and changes nothing but one empty probe file it creates and deletes in the data folder.
/// <para>
/// What it covers: the settings file and the database's migrations and integrity (the same findings as <c>config check</c>), the key ring,
/// who owns the data folder and what mode it has (against the account the server runs as), whether the database and
/// folder are writable and the disk has room, the listening address and whether anything answers on it, the proxy settings
/// (<c>server.forceHttps</c>, <c>server.knownProxies</c>) and the proxy headers the server last saw, the clock, and the account in use.
/// </para>
/// Exit codes: 0 no problems, 1 warnings, 2 failures, 64 a bad command line.
/// </summary>
public static class DoctorCommand
{
    public const int UsageExitCode = 64;

    public const string Usage =
        "Usage: draughtsman doctor [--verbose] [--json] [--DRAUGHTSMAN_DATA=<folder>]\n" +
        "  Checks this install without starting it and without a network or a sign-in: the settings and database (as config check does),\n" +
        "  who owns the data folder and its mode, whether the folder and database are writable, free disk space, the listening address,\n" +
        "  server.forceHttps and server.knownProxies, the key ring, the clock and the account in use.\n" +
        "  Works when the server is down and inside `docker exec <container> draughtsman doctor`. Changes nothing but one empty probe file\n" +
        "  that it creates and deletes in the data folder.\n" +
        "  --verbose also prints the raw facts behind each line. --json prints the same findings as JSON.\n" +
        "  Exit 0: no problems. 1: warnings. 2: failures. 64: bad command line.";

    private const string DataOption = "--DRAUGHTSMAN_DATA=";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, string? dataFolder = null, DoctorProbes? probes = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool json = false;
        bool verbose = false;
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "--verbose" or "-v":
                    verbose = true;
                    break;
                case "-h" or "--help":
                    await stdout.WriteLineAsync(Usage);
                    return 0;
                case var option when option.StartsWith(DataOption, StringComparison.Ordinal):
                    dataFolder ??= Path.GetFullPath(option[DataOption.Length..]);
                    break;
                default:
                    await stderr.WriteLineAsync($"draughtsman doctor: unknown option '{arg}'.\n{Usage}");
                    return UsageExitCode;
            }
        }

        DoctorReport report = await CollectAsync(dataFolder ?? ConfigLoader.ResolveDataFolder(), probes ?? DoctorProbes.Real(), cancellationToken);
        await (json ? stdout.WriteLineAsync(report.ToJson()) : stdout.WriteAsync(report.ToText(verbose)));
        return report.ExitCode;
    }

    /// <summary>Runs every check. Public so the support bundle and the tests read the structured report.</summary>
    public static async Task<DoctorReport> CollectAsync(string folder, DoctorProbes probes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(probes);
        var items = new List<DiagnosticItem>();
        BuildInfo build = BuildInfo.FromAssembly();
        AccountFacts account = probes.Account();
        DateTimeOffset now = probes.Now();

        (DraughtsmanConfig config, bool configReadable) = LoadConfig(folder);
        // The settings, schema and key ring: the findings `config check` makes, mapped onto the doctor's levels.
        (string provider, string? databaseFile) = DatabaseLocation(config, folder);
        bool openDatabase = DatabaseGuard.SafeToOpen(databaseFile, account.CurrentUid, out string? notOpened);
        CheckReport check = await ConfigCheckCommand.CheckAsync(folder, cancellationToken, openDatabase);
        string host = HostKinds.Name(account.Container ? HostKind.Container : HostKinds.Detect());
        items.Add(BuildChecks.Describe(build.Version, build.BuildDate, host));
        items.Add(DoctorChecks.Account(account));
        foreach (CheckLine line in check.Lines.Where(l => !(l.Section == "Data folder" && l.Level == CheckLevel.Ok)))
        {
            items.Add(new DiagnosticItem("config." + line.Section.ToLowerInvariant().Replace(' ', '-'), line.Section, Map(line.Level), line.Message));
        }

        if (notOpened is not null)
        {
            items.Add(DiagnosticItem.Warn("database-not-opened", "Database", notOpened));
        }

        if (check.Database is { } database)
        {
            // Beside the other Database lines, so the section is one block in the output.
            int lastDatabaseLine = items.FindLastIndex(i => i.Title == "Database");
            items.Insert(lastDatabaseLine < 0 ? items.Count : lastDatabaseLine + 1, DiagnosticItem.Info("migrations", "Database", $"{database.Provider}: {database.Applied.ToString(CultureInfo.InvariantCulture)} of {database.Known.ToString(CultureInfo.InvariantCulture)} migrations applied by this build" + (database.Pending.Count > 0 ? $"; {database.Pending.Count.ToString(CultureInfo.InvariantCulture)} pending" : string.Empty) + (database.Newer.Count > 0 ? $"; {database.Newer.Count.ToString(CultureInfo.InvariantCulture)} unknown to this build (a newer version wrote them)" : string.Empty) + "."));
        }

        if (!configReadable)
        {
            // The file could not be read, so what follows describes the defaults, not the file: say so beside the failure rather than leave a reader trusting it.
            items.Add(DiagnosticItem.Info("defaults", "Settings", "Because draughtsman.yaml could not be read, the listening and proxy findings below describe the defaults, not your file."));
        }

        if (!Directory.Exists(folder))
        {
            return new DoctorReport(folder, items);
        }

        // Writable, owners, disk.
        MachineProbeResult machine = MachineProbe.Run(folder, provider, databaseFile);
        if (probes.Disk is { } disk)
        {
            (long? free, long? total) = disk(folder);
            machine = machine with { FreeBytes = free, TotalBytes = total };
        }

        items.AddRange(MachineChecks.Evaluate(machine, "the account running this command"));
        DataEntry[] entries = ListFolder(folder);
        items.Add(DoctorChecks.Ownership(folder, machine.FolderOwner, FileOwners.Mode(folder), FileOwners.Octal(folder), entries, DoctorChecks.ExpectedServerUid(account), account.Windows));

        // The network side, as configuration: where it listens, how it treats a proxy.
        ListeningFacts listening = await ListeningAsync(config, account.Container, probes, cancellationToken);
        items.Add(DoctorChecks.Listening(listening));
        items.AddRange(DoctorChecks.ProxySettings(config.Server, account.Container, ForwardedObservations.Read(folder)));

        (DateTimeOffset? newest, string? newestName) = NewestFile(folder);
        items.Add(DoctorChecks.Clock(now, build.BuildDate, newest, newestName));

        return new DoctorReport(folder, items);
    }

    private static DiagnosticStatus Map(CheckLevel level)
    {
        return level switch
        {
            CheckLevel.Ok => DiagnosticStatus.Pass,
            CheckLevel.Info => DiagnosticStatus.Info,
            CheckLevel.Warn => DiagnosticStatus.Warn,
            _ => DiagnosticStatus.Fail,
        };
    }

    private static (DraughtsmanConfig Config, bool Readable) LoadConfig(string folder)
    {
        try
        {
            return (ConfigLoader.LoadWithWarnings(folder).Config, true);
        }
        catch (Exception error) when (error is YamlException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (new DraughtsmanConfig(), false);
        }
    }

    private static (string Provider, string? File) DatabaseLocation(DraughtsmanConfig config, string folder)
    {
        try
        {
            return SelfCheckService.DatabaseLocation(config, folder);
        }
        catch (InvalidOperationException)
        {
            return (config.Storage.Provider, null);
        }
    }

    /// <summary>The data folder's own entries (no recursion): names, sizes, modes and owners. Capped, so a folder of thousands of files cannot make the doctor slow.</summary>
    private static DataEntry[] ListFolder(string folder)
    {
        const int Cap = 200;
        var entries = new List<DataEntry>();
        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal).Take(Cap))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith(".draughtsman-probe-", StringComparison.Ordinal))
                {
                    continue;
                }

                bool isFolder = Directory.Exists(path);
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

                entries.Add(new DataEntry(name, isFolder, size, FileOwners.Mode(path), FileOwners.Octal(path), FileOwners.Of(path)));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // What was listed so far.
        }

        return [.. entries];
    }

    private static (DateTimeOffset? Newest, string? Name) NewestFile(string folder)
    {
        DateTimeOffset? newest = null;
        string? name = null;
        try
        {
            IEnumerable<string> paths = Directory.EnumerateFileSystemEntries(folder);
            string logs = Path.Combine(folder, "logs");
            if (Directory.Exists(logs))
            {
                paths = paths.Concat(Directory.EnumerateFiles(logs));
            }

            foreach (string path in paths)
            {
                string leaf = Path.GetFileName(path);
                if (leaf.StartsWith(".draughtsman-probe-", StringComparison.Ordinal))
                {
                    continue;
                }

                DateTimeOffset written = File.GetLastWriteTimeUtc(path);
                if (newest is null || written > newest)
                {
                    newest = written;
                    name = Path.GetRelativePath(folder, path);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The clock check then has no file to compare with.
        }

        return (newest, name);
    }

    private static async Task<ListeningFacts> ListeningAsync(DraughtsmanConfig config, bool container, DoctorProbes probes, CancellationToken cancellationToken)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        string[] urls = string.IsNullOrWhiteSpace(fromEnvironment)
            ? [$"http://{config.Server.Host}:{config.Server.Port.ToString(CultureInfo.InvariantCulture)}"]
            : [.. fromEnvironment.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        bool allLoopback = urls.Length > 0 && urls.All(IsLoopbackUrl);
        int port = urls.Select(PortOf).FirstOrDefault(p => p > 0, config.Server.Port);
        LoopbackAnswer answer = await probes.Loopback(port);
        cancellationToken.ThrowIfCancellationRequested();
        return new ListeningFacts(urls, !string.IsNullOrWhiteSpace(fromEnvironment), allLoopback, container, answer);
    }

    internal static bool IsLoopbackUrl(string url)
    {
        // `+`, `*` and `[::]` mean every interface, which Uri cannot parse as a host: they are not loopback.
        if (!Uri.TryCreate(url.Replace("://+", "://placeholder", StringComparison.Ordinal).Replace("://*", "://placeholder", StringComparison.Ordinal), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string host = uri.Host.Trim('[', ']');
        return System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address) ? System.Net.IPAddress.IsLoopback(address) : string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    internal static int PortOf(string url)
    {
        return Uri.TryCreate(url.Replace("://+", "://placeholder", StringComparison.Ordinal).Replace("://*", "://placeholder", StringComparison.Ordinal), UriKind.Absolute, out Uri? uri) ? uri.Port : 0;
    }
}
