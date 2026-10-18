using System.Runtime.InteropServices;

namespace Draughtsman.Server.Export;

/// <summary>What state the Playwright driver (the <c>.playwright/</c> folder: a Node runtime and the playwright package) is in.</summary>
public enum DriverState
{
    /// <summary>Node and the package are both there and Node can be executed.</summary>
    Ready,

    /// <summary>No <c>.playwright</c> folder was found in any place Playwright or this server looks.</summary>
    NotFound,

    /// <summary>A <c>.playwright</c> folder was found but the Node runtime for this platform is not in it.</summary>
    NodeMissing,

    /// <summary>The Node runtime is there but the package (<c>package/cli.js</c>) is not: the two halves never met (QA B1).</summary>
    PackageMissing,

    /// <summary>The Node runtime is there but cannot be executed and the server could not make it so.</summary>
    NotExecutable,
}

/// <param name="State">What was found.</param>
/// <param name="Root">The folder holding <c>.playwright/</c> (the one the server points Playwright at), or the first place looked when nothing was found.</param>
/// <param name="Detail">One sentence naming the missing or unusable file, for the log and the admin's hint; null when ready.</param>
public sealed record DriverCheck(DriverState State, string Root, string? Detail)
{
    public bool Ready => State == DriverState.Ready;
}

/// <summary>
/// Finds and prepares Playwright's driver for server-side PDF export. The driver is a bundled Node runtime plus the playwright
/// package; Playwright launches it as a child process and talks to it over stdio, and the Chromium launch comes after that. In a
/// release archive the folder sits beside the executable (<c>.playwright/</c>, kept out of the single-file bundle by
/// Draughtsman.Server.csproj and checked by scripts/lib/archive-layout.sh), but Playwright's own search starts from the
/// assembly's location, which for a single-file executable is not that folder (QA B1: "Driver not found: &lt;wrong folder&gt;"). So
/// the server looks for itself, makes sure the Node file can run, and hands Playwright the answer through
/// <c>PLAYWRIGHT_DRIVER_SEARCH_PATH</c> before Playwright starts. A missing driver is reported as exactly that, never as a missing
/// browser: installing Chromium cannot fix it.
/// </summary>
public static class PlaywrightDriver
{
    /// <summary>The variable Playwright consults, after its own search, for the folder that holds <c>.playwright/</c>.</summary>
    public const string SearchPathVariable = "PLAYWRIGHT_DRIVER_SEARCH_PATH";

    /// <summary>The platform's folder under <c>.playwright/node/</c>, spelt as Microsoft.Playwright's build targets copy it.</summary>
    public static string PlatformFolder()
    {
        bool arm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
        {
            return "win32_x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm64 ? "darwin-arm64" : "darwin-x64";
        }

        return arm64 ? "linux-arm64" : "linux-x64";
    }

    /// <summary>The Node file name inside the platform folder.</summary>
    public static string NodeFileName => OperatingSystem.IsWindows() ? "node.exe" : "node";

    /// <summary>The places that may hold <c>.playwright/</c>, in the order they are tried: an operator's own override, the executable's folder, the base directory (the assembly's folder in a build tree).</summary>
    public static IReadOnlyList<string> CandidateRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !roots.Contains(path, StringComparer.Ordinal))
            {
                roots.Add(path);
            }
        }

        Add(Environment.GetEnvironmentVariable(SearchPathVariable));
        Add(Path.GetDirectoryName(Environment.ProcessPath));
        Add(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return roots;
    }

    /// <summary>Inspects one candidate folder without changing anything, other than recording it in the result.</summary>
    public static DriverCheck Inspect(string root)
    {
        string playwright = Path.Combine(root, ".playwright");
        string node = Path.Combine(playwright, "node", PlatformFolder(), NodeFileName);
        string cli = Path.Combine(playwright, "package", "cli.js");
        if (!Directory.Exists(playwright))
        {
            return new DriverCheck(DriverState.NotFound, root, $"there is no .playwright folder in {root}");
        }

        if (!File.Exists(node))
        {
            return new DriverCheck(DriverState.NodeMissing, root, $"the Node runtime {node} is missing");
        }

        if (!File.Exists(cli))
        {
            return new DriverCheck(DriverState.PackageMissing, root, $"the playwright package {cli} is missing");
        }

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(node) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
        {
            return new DriverCheck(DriverState.NotExecutable, root, $"the Node runtime {node} is not executable");
        }

        return new DriverCheck(DriverState.Ready, root, null);
    }

    /// <summary>
    /// Finds the driver, repairs a lost execute bit where the server may (an archive unpacked with a tool that drops modes),
    /// and points <c>PLAYWRIGHT_DRIVER_SEARCH_PATH</c> at the folder that has it. Cheap file checks only, safe to call before every
    /// launch. When nothing is complete, returns the most informative failure (a folder that has some of it beats none).
    /// </summary>
    public static DriverCheck Prepare() => Prepare(CandidateRoots(), setEnvironment: true);

    /// <summary><see cref="Prepare()"/> over an explicit list of folders; a test passes its own and leaves the process environment alone.</summary>
    public static DriverCheck Prepare(IReadOnlyList<string> roots, bool setEnvironment)
    {
        DriverCheck? best = null;
        foreach (string root in roots)
        {
            DriverCheck check = Inspect(root);
            if (check.State == DriverState.NotExecutable && !OperatingSystem.IsWindows() && TryMakeExecutable(root))
            {
                check = Inspect(root);
            }

            if (check.Ready)
            {
                if (setEnvironment)
                {
                    Environment.SetEnvironmentVariable(SearchPathVariable, root);
                }

                return check;
            }

            if (best is null || (check.State != DriverState.NotFound && best.State == DriverState.NotFound))
            {
                best = check;
            }
        }

        return best ?? new DriverCheck(DriverState.NotFound, AppContext.BaseDirectory, "no folder to look in");
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool TryMakeExecutable(string root)
    {
        try
        {
            string node = Path.Combine(root, ".playwright", "node", PlatformFolder(), NodeFileName);
            File.SetUnixFileMode(node, File.GetUnixFileMode(node) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Not ours to change (read-only install, another owner): reported as not executable, with the file named.
            return false;
        }
    }
}
