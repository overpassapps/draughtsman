using System.Globalization;
using System.Text.Json;
using Draughtsman.Server.Configuration;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.KeyRing;

/// <summary>An application discriminator an earlier build may have bound a stored secret to (an install path), and where this build got the idea from.</summary>
/// <param name="Discriminator">Exactly the string Data Protection was given: the content root, with or without its trailing separator.</param>
/// <param name="Source">Plain words for the admin page and <c>config check</c>: which place the path came from.</param>
internal sealed record LegacyCandidate(string Discriminator, string Source);

/// <summary>
/// The install paths an older build could have been running from when it protected a secret, in the order they are tried. A build
/// before <see cref="DataProtectionIdentity"/> bound every stored secret to the host's content root, so reading one means guessing
/// which path that was. The guesses, most likely first:
/// <list type="number">
/// <item>The content root this process runs from: the program upgraded in place, or moved together with its data folder.</item>
/// <item>Content roots this data folder has been started from since this build first saw it (<see cref="RecordFileName"/>, kept in
/// <c>keys/</c> so it travels with the ring), which is how a later move can still find the path a secret was written under.</item>
/// <item><c>keyRing.legacyInstallPaths</c> and <c>DRAUGHTSMAN_LEGACY_INSTALL_PATHS</c>: the operator naming the old install folder,
/// for the one case nothing else can know (the new program unpacked in a new folder beside an old data folder that was moved).</item>
/// <item>The folder the data folder sits in: the default layout keeps <c>data/</c> inside the install folder.</item>
/// <item>Last, and only when all of those fail (<see cref="Late"/>): content roots an earlier run logged to <c>&lt;data&gt;/logs/</c>, the folders beside this
/// install folder (a new version is normally unpacked next to the old one, and an old Windows archive folder is named after its version), and the
/// install locations <c>docs/install*.md</c> recommend.</item>
/// </list>
/// Nothing can recover a path nothing records: a program moved AND upgraded in one step, the old folder since deleted, with no log, needs it
/// named (<c>keyRing.legacyInstallPaths</c>); this is cryptography, not caution, because the path is part of the key derivation.
/// A wrong guess costs one failed decryption and nothing else: the payload is authenticated against the discriminator, so a path
/// that is not the right one never decrypts to something else. Only ever used to READ; nothing is written under a legacy path
/// except by <see cref="StoredSecretsMigration"/>'s re-protection, which writes under <see cref="DataProtectionIdentity.ApplicationName"/>.
/// </summary>
internal static class LegacyDiscriminators
{
    /// <summary>Non-secret, in <c>keys/</c> beside <see cref="KeyRingFiles.IdentityFileName"/>; not <c>.xml</c>, so the framework's key repository ignores it.</summary>
    public const string RecordFileName = "install-paths.json";

    /// <summary>Environment variable (paths separated like PATH: <c>:</c>, or <c>;</c> on Windows) that names old install folders, for a container that cannot edit the file.</summary>
    public const string HintVariable = "DRAUGHTSMAN_LEGACY_INSTALL_PATHS";

    private const int MaxRecorded = 16;

    private static readonly JsonSerializerOptions RecordOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, NewLine = "\n" };

    /// <summary>Every candidate for this run, in the order they are tried and without repeats (a path that more than one place names is listed once, under the first).</summary>
    public static IReadOnlyList<LegacyCandidate> Resolve(string dataFolder, string contentRoot, KeyRingConfig config, Func<string, string?> environment)
    {
        var candidates = new List<LegacyCandidate>();
        Add(candidates, contentRoot, "this install folder");
        foreach (RecordedRoot recorded in ReadRecord(dataFolder).ContentRoots)
        {
            Add(candidates, recorded.Path, $"the install folder recorded on {recorded.FirstSeen.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} ({KeyRingFiles.DirectoryName}/{RecordFileName})");
        }

        foreach (string path in config.LegacyInstallPaths)
        {
            Add(candidates, path, "keyRing.legacyInstallPaths in draughtsman.yaml");
        }

        if (environment(HintVariable) is { Length: > 0 } hinted)
        {
            foreach (string path in hinted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(candidates, path, $"the environment variable {HintVariable}");
            }
        }

        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder))) is { Length: > 0 } parent)
        {
            Add(candidates, parent, "the folder the data folder sits in");
        }

        return candidates;
    }

    /// <summary>Well-known install locations: the ones the install guides recommend, and the Docker image's.</summary>
    private static readonly string[] WellKnown = ["/opt/draughtsman", "/usr/local/draughtsman", "/app", @"C:\Program Files\Draughtsman"];

    private const int MaxNeighbours = 200;

    /// <summary>
    /// The guesses worth making only when the first-choice paths all failed, because they read the disk: old logs, the folders next to this install
    /// folder, and the recommended install locations. Never throws.
    /// </summary>
    public static IReadOnlyList<LegacyCandidate> Late(string dataFolder, string contentRoot)
    {
        var candidates = new List<LegacyCandidate>();
        foreach (LegacyCandidate fromLog in FromLogs(dataFolder))
        {
            candidates.Add(fromLog);
        }

        foreach (string neighbour in Neighbours(contentRoot))
        {
            Add(candidates, neighbour, "a folder beside this install folder");
        }

        foreach (string known in WellKnown)
        {
            Add(candidates, known, "a recommended install location");
        }

        return candidates;
    }

    /// <summary>The folders next to <paramref name="contentRoot"/>, newest name first: where the old copy of the program is when the new one was unpacked beside it.</summary>
    private static IEnumerable<string> Neighbours(string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            return [];
        }

        try
        {
            string own = Path.TrimEndingDirectorySeparator(contentRoot);
            return Path.GetDirectoryName(own) is { Length: > 0 } parent && Directory.Exists(parent)
                ? [.. Directory.EnumerateDirectories(parent).Where(d => !string.Equals(Path.TrimEndingDirectorySeparator(d), own, StringComparison.Ordinal)).OrderDescending(StringComparer.OrdinalIgnoreCase).Take(MaxNeighbours)]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Content roots an earlier run of this data folder logged. The host announces its content root at Information (<c>Content root path: ...</c>, kept
    /// on by <c>LoggingSetup</c>), so a data folder that wrote a log file (<c>logging.file.enabled</c>) names the folders it ran from. Read only when
    /// every other path has failed, since it reads files; bounded in files, bytes and result.
    /// </summary>
    public static IReadOnlyList<LegacyCandidate> FromLogs(string dataFolder)
    {
        const string marker = "Content root path: ";
        const int maxFiles = 30;
        const int maxFound = 8;
        const long maxBytes = 64L * 1024 * 1024;
        var found = new List<LegacyCandidate>();
        string folder = Path.Combine(dataFolder, "logs");
        try
        {
            if (!Directory.Exists(folder))
            {
                return found;
            }

            long read = 0;
            foreach (string file in Directory.EnumerateFiles(folder, "*.log").OrderDescending(StringComparer.Ordinal).Take(maxFiles))
            {
                if (read >= maxBytes || found.Count >= maxFound)
                {
                    break;
                }

                read += new FileInfo(file).Length;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line && found.Count < maxFound)
                {
                    int at = line.IndexOf(marker, StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        Add(found, line[(at + marker.Length)..].TrimEnd('\r', '\n'), "a \"Content root path\" line in the logs folder");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be read is one guess fewer, nothing more.
        }

        return found;
    }

    /// <summary>Just the content root and its spelling variants: what the key ring's own checks use, where only "the install this was written under" matters.</summary>
    public static IReadOnlyList<LegacyCandidate> ForContentRoot(string contentRoot)
    {
        var candidates = new List<LegacyCandidate>();
        Add(candidates, contentRoot, "this install folder");
        return candidates;
    }

    /// <summary>Adds each of <see cref="Spellings"/> of <paramref name="path"/> that is not a candidate already.</summary>
    private static void Add(List<LegacyCandidate> candidates, string? path, string source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        foreach (string spelling in Spellings(path, OperatingSystem.IsWindows()))
        {
            if (!candidates.Any(c => string.Equals(c.Discriminator, spelling, StringComparison.Ordinal)))
            {
                candidates.Add(new LegacyCandidate(spelling, source));
            }
        }
    }

    /// <summary>
    /// The strings Data Protection could have been given for a folder an operator (or a log, or the record) names as <paramref name="path"/>, as written first. The host hands
    /// Data Protection its content root with a trailing separator, but an operator types a path without one, and a recorded path is whatever string this build was given.
    /// On Windows (<paramref name="windows"/>) either slash names the same folder and the host's own spelling is backslashes, so a path typed with forward slashes (the habit
    /// in YAML) is also tried with backslashes, and the drive letter in either case; elsewhere a backslash is an ordinary character of a name, so only the trailing slash is toggled.
    /// Pure over the flag, so every platform tests the Windows rules.
    /// </summary>
    internal static IReadOnlyList<string> Spellings(string path, bool windows)
    {
        string trimmed = path.Trim();
        var spellings = new List<string>();
        string[] separators = windows ? ["\\", "/"] : ["/"];
        string[] writtenForms = windows && trimmed.Contains('/', StringComparison.Ordinal) ? [trimmed, trimmed.Replace('/', '\\')] : [trimmed];
        foreach (string written in writtenForms)
        {
            string bare = windows ? written.TrimEnd('/', '\\') : written.TrimEnd('/');
            // "/" and "C:\" are roots, not a folder with a separator after it: there is no bare form of them.
            bool root = bare.Length == 0 || (windows && bare.Length == 2 && bare[1] == ':' && bare != written);
            AddOnce(spellings, written);
            if (root)
            {
                continue;
            }

            AddOnce(spellings, bare);
            foreach (string separator in separators)
            {
                AddOnce(spellings, bare + separator);
            }
        }

        if (windows)
        {
            foreach (string spelling in spellings.ToArray())
            {
                if (spelling.Length >= 2 && spelling[1] == ':' && char.IsAsciiLetter(spelling[0]))
                {
                    char flipped = char.IsAsciiLetterUpper(spelling[0]) ? char.ToLowerInvariant(spelling[0]) : char.ToUpperInvariant(spelling[0]);
                    AddOnce(spellings, flipped + spelling[1..]);
                }
            }
        }

        return spellings;
    }

    private static void AddOnce(List<string> spellings, string spelling)
    {
        if (spelling.Length > 0 && !spellings.Contains(spelling, StringComparer.Ordinal))
        {
            spellings.Add(spelling);
        }
    }

    // ------------------------------------------------------------------------------------------------------ the record

    /// <summary>
    /// Writes down that this data folder has been started from <paramref name="contentRoot"/>, once per distinct path. The first entry is the
    /// path an upgrade-in-place's old secrets are bound to. Never fails the start: a data folder that cannot be written is logged and the
    /// server runs on, since the record only matters to a build that has to read an old secret after a move.
    /// </summary>
    public static void Record(string dataFolder, string contentRoot, ILogger logger, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(contentRoot))
        {
            return;
        }

        try
        {
            InstallRecord record = ReadRecord(dataFolder);
            if (record.ContentRoots.Any(r => string.Equals(r.Path, contentRoot, StringComparison.Ordinal)) || record.ContentRoots.Count >= MaxRecorded)
            {
                return;
            }

            record.ContentRoots.Add(new RecordedRoot(contentRoot, (clock ?? TimeProvider.System).GetUtcNow()));
            string folder = KeyRingFiles.KeysFolder(dataFolder);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, RecordFileName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, RecordOptions));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Key ring: the install folder could not be recorded in {Folder}/{File} ({Reason}); a later move of the program could then not find the path an older secret was written under.", KeyRingFiles.DirectoryName, RecordFileName, ex.GetType().Name);
        }
    }

    /// <summary>The recorded roots, oldest first. A missing or damaged file is an empty record: it is a convenience, never a reason to stop.</summary>
    internal static InstallRecord ReadRecord(string dataFolder)
    {
        string path = Path.Combine(KeyRingFiles.KeysFolder(dataFolder), RecordFileName);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(path), RecordOptions) is { ContentRoots: { } roots } record)
            {
                return record with { ContentRoots = [.. roots.Where(r => !string.IsNullOrWhiteSpace(r.Path))] };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Not trusted, not deleted: the next Record() overwrites it with a good one.
        }

        return new InstallRecord(1, []);
    }

    internal sealed record InstallRecord(int FormatVersion, List<RecordedRoot> ContentRoots);

    internal sealed record RecordedRoot(string Path, DateTimeOffset FirstSeen);
}
