using System.Runtime.Versioning;
using Draughtsman.Server.Auth.Recovery;
using Draughtsman.Server.Backup;
using Draughtsman.Server.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.EventLog;

namespace Draughtsman.Server.Cli;

/// <summary>What <see cref="ServerCli.ExecuteAsync"/> decided: start the web host, or the command is done with this exit code.</summary>
public readonly record struct CliOutcome(bool Serve, int ExitCode);

/// <summary>
/// The verb dispatcher behind Program.cs (PK-05): <c>serve</c> (the default), <c>mcp --stdio</c>, <c>service ...</c>,
/// <c>--version</c> and <c>--help</c>. Program.cs makes one call, <see cref="StartAsync"/>; it returns the web host
/// builder to run, or null after a command has already run and set the process exit code. Nothing but <c>serve</c> (and a
/// bare run, which is <c>serve</c>) ever creates the web host, so <c>mcp --stdio</c> cannot log to stdout and corrupt its
/// own JSON-RPC stream and <c>--version</c> cannot bind a port.
/// </summary>
public static class ServerCli
{
    private const string UrlsEnvironmentVariable = "ASPNETCORE_URLS";

    public static async Task<WebApplicationBuilder?> StartAsync(string[] args)
    {
        CliOutcome outcome = await ExecuteAsync(args, Console.Out, Console.Error);
        if (outcome.Serve)
        {
            return CreateServerBuilder(args);
        }

        Environment.ExitCode = outcome.ExitCode;
        return null;
    }

    /// <summary>Runs every command except <c>serve</c>, whose host the caller builds. Output goes to the given writers.</summary>
    public static async Task<CliOutcome> ExecuteAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default, TextReader? stdin = null)
    {
        CliCommand command = CliCommand.Parse(args);
        switch (command.Verb)
        {
            case CliVerb.Serve:
                return new CliOutcome(true, 0);
            case CliVerb.Version:
                BuildInfo build = BuildInfo.FromAssembly();
                stdout.WriteLine($"Draughtsman {build.Version} (build {build.BuildDate:yyyy-MM-dd})");
                return new CliOutcome(false, 0);
            case CliVerb.Help:
                stdout.Write(HelpText);
                return new CliOutcome(false, 0);
            case CliVerb.McpStdio:
                // Every host option after --stdio (for example --DRAUGHTSMAN_DATA=...) is configuration for this host.
                await McpStdioHost.RunAsync(args, cancellationToken);
                return new CliOutcome(false, 0);
            case CliVerb.ResetAdmin:
                return new CliOutcome(false, await ResetAdminCommand.RunAsync([.. command.Arguments], stdin ?? Console.In, stdout, stderr, cancellationToken: cancellationToken));
            case CliVerb.FirstRunCode:
                return new CliOutcome(false, await FirstRunCodeCommand.RunAsync([.. command.Arguments], stdout, stderr, cancellationToken: cancellationToken));
            case CliVerb.Backup:
                return new CliOutcome(false, await BackupCommands.BackupAsync([.. command.Arguments], stdout, stderr, cancellationToken: cancellationToken));
            case CliVerb.Restore:
                return new CliOutcome(false, await BackupCommands.RestoreAsync([.. command.Arguments], stdout, stderr, cancellationToken: cancellationToken));
            case CliVerb.Doctor:
                return new CliOutcome(false, await DoctorCommand.RunAsync([.. command.Arguments], stdout, stderr, cancellationToken: cancellationToken));
            case CliVerb.SupportBundle:
                return new CliOutcome(false, await SupportBundleCommand.RunAsync([.. command.Arguments], stdout, stderr, cancellationToken: cancellationToken));
            case CliVerb.Diff:
                return new CliOutcome(false, await DiagramFileCommands.DiffAsync([.. command.Arguments], stdout, stderr, cancellationToken));
            case CliVerb.Check:
                return new CliOutcome(false, await DiagramFileCommands.CheckAsync([.. command.Arguments], stdout, stderr, cancellationToken));
            case CliVerb.Config:
                if (command.Arguments.Count > 0 && command.Arguments[0] == "check")
                {
                    return new CliOutcome(false, await ConfigCheckCommand.RunAsync([.. command.Arguments.Skip(1)], stdout, stderr, cancellationToken: cancellationToken));
                }

                stderr.WriteLine("draughtsman: 'config' needs a subcommand: 'config check'. Run 'draughtsman --help' for the commands.");
                return new CliOutcome(false, 2);
            case CliVerb.Service:
                return new CliOutcome(false, await RunServiceAsync(command.Arguments, stdout, stderr, cancellationToken));
            default:
                stderr.WriteLine($"draughtsman: {command.Error}. Run 'draughtsman --help' for the commands.");
                return new CliOutcome(false, 2);
        }
    }

    /// <summary>
    /// The web host builder. A packaged install anchors the content root on the application folder so appsettings.json
    /// and the SPA are found whatever the working directory is (<see cref="AppLayout"/>); the compression middleware and,
    /// when the process was started by the Windows service manager, the service lifetime are added here too.
    /// </summary>
    public static WebApplicationBuilder CreateServerBuilder(string[] args)
    {
        string? urls = CliCommand.UrlsOption(args);
        if (urls is not null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(UrlsEnvironmentVariable)))
        {
            Environment.SetEnvironmentVariable(UrlsEnvironmentVariable, urls);
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppLayout.ContentRoot(AppContext.BaseDirectory),
        });
        ResponseCompressionStartupFilter.AddServices(builder.Services);
        // A no-op unless the Windows service control manager started this process.
        builder.Host.UseWindowsService();
        // The Event Log source `service install` registers: named here, not left to the host's default (the application name), so the two cannot drift apart.
        if (OperatingSystem.IsWindows())
        {
            NameEventSource(builder.Services);
        }
        return builder;
    }

    [SupportedOSPlatform("windows")]
    private static void NameEventSource(IServiceCollection services)
    {
        services.PostConfigure<EventLogSettings>(settings => settings.SourceName = WindowsEventSource.Name);
    }

    private static async Task<int> RunServiceAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        (ServiceOptions? options, string? error) = ServiceOptions.Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"draughtsman: {error}. Run 'draughtsman --help' for the commands.");
            return 2;
        }

        string? executable = options.Executable ?? Environment.ProcessPath;
        if (executable is null || IsDotnetHost(executable))
        {
            stderr.WriteLine("draughtsman: service needs the published draughtsman executable (this run is under the dotnet host). Use the release archive, or pass --exe <path>.");
            return 2;
        }

        try
        {
            ServicePlan plan = ServicePlanner.Plan(options, executable);
            return await ServiceExecutor.RunAsync(plan, options.DryRun, stdout, stderr, cancellationToken);
        }
        catch (ServicePlanException ex)
        {
            stderr.WriteLine($"draughtsman: {ex.Message}");
            return 2;
        }
    }

    private static bool IsDotnetHost(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    public const string HelpText = """
        Draughtsman: self-hosted, agent-native diagramming.

        Usage:
          draughtsman [serve] [--option=value ...]   Run the server (the default when no command is given).
          draughtsman mcp --stdio                    Serve the MCP tools over stdio for an agent on this machine.
          draughtsman reset-admin <email> [--generate] [--promote]
                                                     Recover a lost administrator password on this host (see docs/api.md).
          draughtsman first-run-code [--DRAUGHTSMAN_DATA=<folder>]
                                                     Print a Windows service's first-run setup code (run it elevated; see docs/install-windows.md).
          draughtsman backup [--out <file|folder>] [--include-secrets]
                                                     Write a consistent, checksummed backup bundle of the data folder (SQLite; Postgres
                                                     prints the pg_dump command). Safe while the server runs. See docs/upgrade-backup.md.
          draughtsman restore <bundle.zip> [--overwrite]
                                                     Verify a bundle and restore it into an empty data folder (stop the server first).
          draughtsman config check [--json]          Report what starting this version on the data folder would do (settings, database
                                                     schema against this build, the pre-upgrade copy, key ring) without starting it.
                                                     Exit 0 would start, 1 would refuse, 3 database newer than this build. Run it before every upgrade.
          draughtsman doctor [--verbose] [--json]    Check this install without a network or a sign-in: settings, database and migrations, who owns the
                                                     data folder, whether it is writable and has room, the listening address, forceHttps and
                                                     knownProxies, key ring, clock. Works with the server down and inside docker exec.
                                                     Exit 0 no problems, 1 warnings, 2 failures. See docs/troubleshooting.md.
          draughtsman support-bundle [--output <file>] [--lines <n>]
                                                     Write one zip with redacted diagnostics a person can read through and send to support: the
                                                     doctor output, effective settings (secrets shown only as set or not set), the log tail,
                                                     security-event counts, the proxy headers seen and a listing of the data folder. Never the
                                                     database, documents, key ring, hashes, tokens or setup code. Its README lists what is in it.
          draughtsman diff <before.dsl> <after.dsl> [--format markdown|json] [--ignore-layout]
                                                     Say what changed between two diagrams in sentences that use labels, not ids, for a
                                                     pull request (see docs/git-sync.md). Reads only the two files. Exit 0 no semantic
                                                     change, 1 changed, 2 a file could not be read or parsed.
          draughtsman check <file.dsl> [--format text|json]
                                                     Parse a diagram and report its warnings and critique findings. Reads only the file.
                                                     Exit 0 no error-severity finding, 1 an error, 2 the file could not be read or parsed.
          draughtsman service install   [options]    Install the server as a background service (systemd, launchd, Windows).
          draughtsman service uninstall [options]    Remove the service; the data folder is left alone.
          draughtsman service status    [options]    Show whether the service is running.
          draughtsman --version                      Print the version and build date.
          draughtsman --help                         Print this help.

        Server options: --urls=http://0.0.0.0:5180 (overrides server.host and server.port; same as ASPNETCORE_URLS) and any
        host configuration key, for example --DRAUGHTSMAN_DATA=/srv/draughtsman.
        Settings live in draughtsman.yaml in the data folder (see draughtsman.example.yaml); the data folder is
        DRAUGHTSMAN_DATA, or data/ beside the executable of a release archive.

        Service options:
          --name <name>    Service name (default draughtsman).
          --data <folder>  Data folder (default /var/lib/<name>, /Library/Application Support/Draughtsman, %ProgramData%\Draughtsman).
          --user <acct>    Account to run as (default: a systemd dynamic user, root on macOS, LocalService on Windows).
          --exe <path>     The draughtsman executable to run (default: this one).
          --dry-run        Print the files and commands without changing anything.
          --platform <p>   With --dry-run, preview systemd, launchd or windows instead of this machine's.

        """;
}
