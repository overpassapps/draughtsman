namespace Draughtsman.Server.Cli;

public enum ServiceTarget
{
    Systemd,
    Launchd,
    WindowsService,
}

public enum ServiceAction
{
    Install,
    Uninstall,
    Status,
}

/// <summary>One thing a service command does to the machine, in order. A dry run prints these; a real run performs them.</summary>
public abstract record ServiceStep
{
    /// <summary>Creates a folder. <see cref="OwnerUser"/> is set when a service account that is not the installer must own it.</summary>
    public sealed record EnsureDirectory(string Path, string? OwnerUser = null) : ServiceStep;

    public sealed record WriteFile(string Path, string Content) : ServiceStep;

    public sealed record DeleteFile(string Path) : ServiceStep;

    /// <summary>Registers a Windows Event Log source in the Application log (needs the elevation <c>service install</c> already requires).</summary>
    public sealed record RegisterEventSource(string Source) : ServiceStep;

    /// <summary>
    /// Runs a program with an explicit argument list (never a shell string). <see cref="IgnoreFailure"/> for best-effort steps
    /// such as stopping a service that may not be running. <see cref="QuietExitCodes"/> are exit codes that mean "nothing to
    /// do" for that program (sc.exe stop answers 1062 for a service that is not running): the step succeeds and says nothing.
    /// </summary>
    public sealed record Run(string FileName, IReadOnlyList<string> Arguments, bool IgnoreFailure = false, IReadOnlyList<int>? QuietExitCodes = null) : ServiceStep;
}

/// <summary>What a <c>service</c> command will do. Built by <see cref="ServicePlanner"/> without touching the machine.</summary>
public sealed record ServicePlan(ServiceTarget Target, ServiceAction Action, string Name, IReadOnlyList<ServiceStep> Steps, IReadOnlyList<string> Notes);

/// <summary>The parsed options of <c>draughtsman service &lt;install|uninstall|status&gt;</c>.</summary>
public sealed record ServiceOptions
{
    public const string DefaultName = "draughtsman";

    public ServiceAction Action { get; init; }

    /// <summary>The service's name: the systemd unit, the launchd label suffix and the Windows service name.</summary>
    public string Name { get; init; } = DefaultName;

    /// <summary>The instance data folder. Null means the platform default (<see cref="ServicePlanner.DefaultDataFolder"/>).</summary>
    public string? DataFolder { get; init; }

    /// <summary>The account the service runs as. Null means the platform default (see <see cref="ServicePlanner"/>).</summary>
    public string? User { get; init; }

    /// <summary>The draughtsman executable to run. Null means this process's own.</summary>
    public string? Executable { get; init; }

    public bool DryRun { get; init; }

    /// <summary>Plans for another platform than this one. Only honoured with <see cref="DryRun"/>, so nothing ever installs the wrong kind of service.</summary>
    public ServiceTarget? Platform { get; init; }

    /// <summary>Parses the arguments after <c>service</c>; an error is a message for stderr, never an exception.</summary>
    public static (ServiceOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return (null, "service needs an action: install, uninstall or status");
        }

        ServiceAction action;
        switch (args[0])
        {
            case "install":
                action = ServiceAction.Install;
                break;
            case "uninstall":
                action = ServiceAction.Uninstall;
                break;
            case "status":
                action = ServiceAction.Status;
                break;
            default:
                return (null, $"unknown service action '{args[0]}' (expected install, uninstall or status)");
        }

        var options = new ServiceOptions { Action = action };
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--dry-run":
                    options = options with { DryRun = true };
                    continue;
                case "--name" or "--data" or "--user" or "--exe" or "--platform":
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return (null, $"{arg} needs a value");
                    }

                    string value = args[++i];
                    switch (arg)
                    {
                        case "--name":
                            options = options with { Name = value };
                            break;
                        case "--data":
                            options = options with { DataFolder = value };
                            break;
                        case "--user":
                            options = options with { User = value };
                            break;
                        case "--exe":
                            options = options with { Executable = value };
                            break;
                        default:
                            ServiceTarget? platform = value.ToLowerInvariant() switch
                            {
                                "systemd" or "linux" => ServiceTarget.Systemd,
                                "launchd" or "macos" or "osx" => ServiceTarget.Launchd,
                                "windows" or "win" => ServiceTarget.WindowsService,
                                _ => null,
                            };
                            if (platform is null)
                            {
                                return (null, $"--platform '{value}' is not systemd, launchd or windows");
                            }

                            options = options with { Platform = platform };
                            break;
                    }

                    continue;
                default:
                    return (null, $"unknown option '{arg}' for service {args[0]}");
            }
        }

        if (options.Platform is not null && !options.DryRun)
        {
            return (null, "--platform only works with --dry-run (it previews another platform's service; it never installs one)");
        }

        if (!IsValidName(options.Name))
        {
            return (null, $"--name '{options.Name}' must be letters, digits, '-' and '_' only");
        }

        return (options, null);
    }

    private static bool IsValidName(string name)
    {
        return name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }
}
