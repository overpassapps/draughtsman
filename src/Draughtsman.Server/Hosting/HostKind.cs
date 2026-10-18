using Microsoft.Extensions.Hosting.WindowsServices;

namespace Draughtsman.Server.Hosting;

/// <summary>How this process was started, as far as the environment shows it (PR-02): it decides which "where is the log" hint the first-run page opens.</summary>
public enum HostKind
{
    /// <summary>Started from a terminal or by something this cannot recognise: the output is in that window.</summary>
    Console,
    Container,
    Systemd,
    Launchd,
    WindowsService,
}

/// <summary>
/// Detects the <see cref="HostKind"/> and says where that kind of install writes its log. A hint only: nothing about
/// behaviour depends on it, so a wrong guess shows the wrong section opened, never a wrong result. The environment is
/// a parameter so the detection is testable on any machine.
/// </summary>
public static class HostKinds
{
    public static HostKind Detect()
    {
        return Detect(Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService(), OperatingSystem.IsMacOS());
    }

    public static HostKind Detect(Func<string, string?> environment, Func<string, bool> fileExists, bool windowsService, bool macOs)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (windowsService)
        {
            return HostKind.WindowsService;
        }

        // Set by the official .NET images; /.dockerenv covers a hand-made image that does not set it.
        if (string.Equals(environment("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) || fileExists("/.dockerenv"))
        {
            return HostKind.Container;
        }

        // systemd sets INVOCATION_ID for every unit it starts (and JOURNAL_STREAM when stdout goes to the journal).
        if (!string.IsNullOrEmpty(environment("INVOCATION_ID")) || !string.IsNullOrEmpty(environment("JOURNAL_STREAM")))
        {
            return HostKind.Systemd;
        }

        // launchd gives a job its Label; a process started from a shell inherits "0" and a GUI app "application.<id>".
        string? job = environment("XPC_SERVICE_NAME");
        if (macOs && !string.IsNullOrEmpty(job) && job != "0" && !job.StartsWith("application.", StringComparison.Ordinal))
        {
            return HostKind.Launchd;
        }

        return HostKind.Console;
    }

    /// <summary>The wire name of a kind: camelCase, stable, used by the SPA.</summary>
    public static string Name(HostKind kind)
    {
        return kind switch
        {
            HostKind.Container => "container",
            HostKind.Systemd => "systemd",
            HostKind.Launchd => "launchd",
            HostKind.WindowsService => "windowsService",
            _ => "console",
        };
    }

    /// <summary>Where a running instance of this kind writes its log, for the admin status page.</summary>
    public static string LogHint(HostKind kind, string dataFolder)
    {
        return kind switch
        {
            HostKind.Container => "The container's output: docker logs <container name>.",
            HostKind.Systemd => "The journal: journalctl -u draughtsman (use the unit name the service was installed under).",
            HostKind.Launchd => "The log file " + Path.Combine(dataFolder, "logs", "draughtsman.log").Replace('\\', '/') + " (the service's standard output).",
            HostKind.WindowsService => "The Windows Application event log (Event Viewer, Windows Logs, Application), source draughtsman. The first-run setup code is not in it: it is in " + Path.Combine(dataFolder, "setup-code.txt").Replace('/', '\\') + ", readable by Administrators only (draughtsman first-run-code prints it).",
            _ => "The terminal window this server was started from.",
        };
    }
}
