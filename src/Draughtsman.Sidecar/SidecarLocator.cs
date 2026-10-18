namespace Draughtsman.Sidecar;

/// <summary>
/// Finds the Node executable and the sidecar bundle without shelling out, so a missing piece is reported as a
/// clear message rather than a Win32Exception from Process.Start.
/// </summary>
public static class SidecarLocator
{
    /// <summary>Folder next to the server binary holding the bundle (and, in a release archive, the Node runtime).</summary>
    public const string BundleFolder = "sidecar";

    public const string BundleFileName = "sidecar.js";

    public static string ResolveNode(string configured, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string? found = LooksLikeAPath(configured) ? ExistingFile(configured) : FindOnPath(configured);
            return found ?? throw new SidecarUnavailableException(
                $"sidecar.nodePath '{configured}' was not found. Give the full path to a Node 22 executable or leave it empty to use PATH.");
        }

        foreach (string name in NodeExecutableNames())
        {
            string? bundled = ExistingFile(Path.Combine(baseDirectory, BundleFolder, name));
            if (bundled is not null)
            {
                return bundled;
            }
        }

        foreach (string name in NodeExecutableNames())
        {
            string? onPath = FindOnPath(name);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        throw new SidecarUnavailableException(
            $"Node was not found under {Path.Combine(baseDirectory, BundleFolder)} or on PATH. Install Node 22 or set sidecar.nodePath in draughtsman.yaml.");
    }

    public static string ResolveScript(string configured, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return ExistingFile(configured) ?? throw new SidecarUnavailableException($"sidecar.scriptPath '{configured}' was not found.");
        }

        string bundled = Path.Combine(baseDirectory, BundleFolder, BundleFileName);
        return ExistingFile(bundled) ?? throw new SidecarUnavailableException(
            $"The sidecar bundle was not found at {bundled}. Build it with dotnet build (which copies it there) or set sidecar.scriptPath in draughtsman.yaml.");
    }

    /// <summary>Searches PATH for an executable name, honouring PATHEXT on Windows for names given without an extension.</summary>
    public static string? FindOnPath(string name)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidateName in ExecutableNames(name))
            {
                string? found = ExistingFile(Path.Combine(folder.Trim('"'), candidateName));
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> NodeExecutableNames()
    {
        yield return OperatingSystem.IsWindows() ? "node.exe" : "node";
    }

    private static IEnumerable<string> ExecutableNames(string name)
    {
        yield return name;
        if (OperatingSystem.IsWindows() && !Path.HasExtension(name))
        {
            string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
            foreach (string ext in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                yield return name + ext.ToLowerInvariant();
            }
        }
    }

    private static bool LooksLikeAPath(string value)
    {
        return Path.IsPathRooted(value) || value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || value.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string? ExistingFile(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // A malformed configured path is reported by the caller as "not found", which is what the operator needs to hear.
            return null;
        }
    }
}
