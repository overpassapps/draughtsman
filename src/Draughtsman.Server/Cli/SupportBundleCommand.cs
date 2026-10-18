using System.Globalization;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Diagnostics;

namespace Draughtsman.Server.Cli;

/// <summary>
/// <c>draughtsman support-bundle [--output &lt;file&gt;] [--lines &lt;n&gt;]</c>: writes the zip described by <see cref="SupportBundle"/> and says what it holds.
/// It reads the data folder and never starts the server, so it works with the server stopped and inside <c>docker exec</c>
/// (<c>docker exec &lt;container&gt; draughtsman support-bundle --output /data/support-bundle.zip</c>, then <c>docker cp</c>). It never overwrites a file.
/// Exit codes: 0 written, 1 could not be written (the file exists, or the folder is not writable), 64 a bad command line.
/// </summary>
public static class SupportBundleCommand
{
    public const int UsageExitCode = 64;

    public const string Usage =
        "Usage: draughtsman support-bundle [--output <file>] [--lines <n>] [--DRAUGHTSMAN_DATA=<folder>]\n" +
        "  Writes one zip with redacted diagnostics that you can read through and send to support (default file:\n" +
        "  draughtsman-support-bundle-<time>.zip in the current folder). It holds the doctor output, an allow-listed summary of the settings, the\n" +
        "  last <n> log lines (default 2000), security-event counts, the proxy headers seen, a listing of the data folder and the migrations.\n" +
        "  It never holds the database, documents, key ring, password hashes, tokens or the setup code; its\n" +
        "  README.txt lists exactly what is inside and what is not. Works with the server stopped and inside docker exec:\n" +
        "    docker exec <container> draughtsman support-bundle --output /data/support-bundle.zip\n" +
        "    docker cp <container>:/data/support-bundle.zip .\n" +
        "  On a read-only container docker cp cannot read from /tmp: write under /data as above, or stream it out with\n" +
        "    docker exec <container> cat /data/support-bundle.zip > support-bundle.zip\n" +
        "  Exit 0: written. 1: could not be written. 64: bad command line.";

    private const string DataOption = "--DRAUGHTSMAN_DATA=";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, string? dataFolder = null, SupportBundleOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? output = null;
        int lines = options?.LogLines ?? SupportBundle.DefaultLogLines;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    await stdout.WriteLineAsync(Usage);
                    return 0;
                case "--output" or "-o":
                    if (i + 1 >= args.Length)
                    {
                        await stderr.WriteLineAsync($"draughtsman support-bundle: {arg} needs a file name.\n{Usage}");
                        return UsageExitCode;
                    }

                    output = args[++i];
                    break;
                case var option when option.StartsWith("--output=", StringComparison.Ordinal):
                    output = option["--output=".Length..];
                    break;
                case "--lines":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out lines) || lines < 0 || lines > 100_000)
                    {
                        await stderr.WriteLineAsync($"draughtsman support-bundle: --lines needs a number from 0 to 100000.\n{Usage}");
                        return UsageExitCode;
                    }

                    break;
                case var option when option.StartsWith(DataOption, StringComparison.Ordinal):
                    dataFolder ??= Path.GetFullPath(option[DataOption.Length..]);
                    break;
                default:
                    await stderr.WriteLineAsync($"draughtsman support-bundle: unknown option '{arg}'.\n{Usage}");
                    return UsageExitCode;
            }
        }

        string folder = dataFolder ?? ConfigLoader.ResolveDataFolder();
        DateTimeOffset now = options?.Now ?? DateTimeOffset.UtcNow;
        string path = Path.GetFullPath(output ?? Path.Combine(Directory.GetCurrentDirectory(), $"draughtsman-support-bundle-{now:yyyyMMdd-HHmmss}.zip"));
        if (File.Exists(path) || Directory.Exists(path))
        {
            await stderr.WriteLineAsync($"draughtsman support-bundle: {path} already exists; this command never overwrites a file. Choose another name with --output.");
            return 1;
        }

        IReadOnlyList<BundleEntry> entries = await SupportBundle.BuildAsync(
            folder,
            new SupportBundleOptions { LogLines = lines, Probes = options?.Probes, Environment = options?.Environment, Now = now },
            cancellationToken);
        try
        {
            SupportBundle.WriteZip(path, entries, now);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync($"draughtsman support-bundle: could not write {path} ({error.GetType().Name}). Choose a folder this account can write with --output.");
            return 1;
        }

        await stdout.WriteLineAsync($"Wrote {path} ({new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture)} bytes, {entries.Count.ToString(CultureInfo.InvariantCulture)} files):");
        foreach (BundleEntry entry in entries)
        {
            await stdout.WriteLineAsync("  " + entry.Name);
        }

        await stdout.WriteLineAsync("Read README.txt inside it, and look through the files, before you send it: it lists what is and is not in the bundle.");
        return 0;
    }
}
