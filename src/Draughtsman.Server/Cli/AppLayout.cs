namespace Draughtsman.Server.Cli;

/// <summary>
/// Where an installed copy of Draughtsman keeps its files. A release archive (scripts/publish.sh) is one folder holding
/// the executable, <c>wwwroot/</c> (the SPA) and <c>sidecar/</c> (the bundle and the Node runtime); that folder, not the
/// current directory, is the anchor for the content root and the default data folder, so a service (working directory
/// <c>/</c> or <c>System32</c>) and a double-click from another folder both behave like a run from inside it.
/// A build tree (<c>dotnet run</c>, tests) has no <c>wwwroot</c> beside the binary and keeps the working-directory rules.
/// </summary>
public static class AppLayout
{
    public const string DefaultDataFolderName = "data";

    /// <summary>True when <paramref name="baseDirectory"/> is an unpacked release archive (or the Docker image's /app).</summary>
    public static bool IsPackaged(string baseDirectory)
    {
        return File.Exists(Path.Combine(baseDirectory, "wwwroot", "index.html"));
    }

    /// <summary>The content root to pass the web host, or null to keep ASP.NET Core's default (the working directory).</summary>
    public static string? ContentRoot(string baseDirectory)
    {
        return IsPackaged(baseDirectory) ? Path.GetFullPath(baseDirectory) : null;
    }

    /// <summary>The data folder used when DRAUGHTSMAN_DATA names none: <c>data/</c> beside a packaged install, otherwise <c>./data</c>.</summary>
    public static string DefaultDataFolder(string baseDirectory)
    {
        return IsPackaged(baseDirectory)
            ? Path.GetFullPath(Path.Combine(baseDirectory, DefaultDataFolderName))
            : Path.GetFullPath(Configuration.DraughtsmanConfig.DefaultDataFolder);
    }
}
