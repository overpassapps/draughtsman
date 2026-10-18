using Draughtsman.Core.Auth;
using Draughtsman.Server.Cli;
using Draughtsman.Server.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Auth.Recovery;

/// <summary>
/// <c>draughtsman first-run-code</c>: prints the first-run setup code of a Windows service from its restricted file
/// (<see cref="SetupCodeFile"/>), for an Administrator at an elevated prompt. Every other host prints the code to its own
/// log, so this is the supported way to read one where no log can hold it. It needs no network and no running server.
/// Exit codes: 0 the code was printed, 1 refused (the instance already has an administrator, there is no code file, or the
/// file cannot be read by this account; the message says which and what to do), 2 usage.
/// <para>
/// Registered as the <c>first-run-code</c> verb in <c>Cli/ServerCli.cs</c> (<c>CliVerb.FirstRunCode</c>). A
/// <c>--DRAUGHTSMAN_DATA=&lt;folder&gt;</c> option picks the data folder like every other verb; with none, a Windows host also
/// tries the folder <c>service install</c> uses by default, since that is where a service started with no option keeps it.
/// </para>
/// </summary>
public static class FirstRunCodeCommand
{
    private const string DataOptionName = "--DRAUGHTSMAN_DATA";
    private const string DataOption = DataOptionName + "=";

    public const string Usage = "Usage: draughtsman first-run-code [--DRAUGHTSMAN_DATA <folder>]\n" +
        "  Prints the first-run setup code of a Windows service, read from setup-code.txt in its data folder.\n" +
        "  Run it from an elevated (Administrator) prompt. It refuses once the instance has an administrator.\n" +
        "  --DRAUGHTSMAN_DATA <folder>  the service's data folder (also --DRAUGHTSMAN_DATA=<folder>) (default: DRAUGHTSMAN_DATA, else data/ beside the executable,\n" +
        "                               else the service default %ProgramData%\\Draughtsman)";

    /// <param name="args">Everything after the verb.</param>
    /// <param name="dataFolder">Null means the usual resolution; tests pass a temp folder.</param>
    /// <param name="serviceDefaultFolder">The folder tried when the usual resolution has no code file; null means the platform's service default (Windows only).</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string? dataFolder = null, string? serviceDefaultFolder = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool explicitFolder = dataFolder is not null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    await output.WriteLineAsync(Usage);
                    return 0;
                case DataOptionName when i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    // The spelling the service's own command line uses (`--DRAUGHTSMAN_DATA "C:\ProgramData\Draughtsman"`), so the line in the log works as printed.
                    dataFolder ??= Path.GetFullPath(args[++i]);
                    explicitFolder = true;
                    break;
                case var option when option.StartsWith(DataOption, StringComparison.Ordinal):
                    dataFolder ??= Path.GetFullPath(option[DataOption.Length..]);
                    explicitFolder = true;
                    break;
                default:
                    await error.WriteLineAsync($"Unexpected argument '{arg}'.\n{Usage}");
                    return 2;
            }
        }

        explicitFolder |= !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DraughtsmanConfig.DataFolderEnvironmentVariable));
        string folder = dataFolder ?? ConfigLoader.ResolveDataFolder();
        if (!explicitFolder && !File.Exists(Path.Combine(folder, SetupCodeFile.FileName)))
        {
            // Nothing named: the service default is where a service installed with no --data keeps it. A code file there wins; failing that,
            // an existing folder there is a better answer ("already has an administrator") than "data/ beside the executable does not exist".
            string? fallback = serviceDefaultFolder ?? (OperatingSystem.IsWindows() ? ServicePlanner.DefaultDataFolder(ServiceTarget.WindowsService, ServiceOptions.DefaultName) : null);
            if (fallback is not null && (File.Exists(Path.Combine(fallback, SetupCodeFile.FileName)) || (!Directory.Exists(folder) && Directory.Exists(fallback))))
            {
                folder = fallback;
            }
        }

        if (!Directory.Exists(folder))
        {
            await error.WriteLineAsync($"The data folder '{folder}' does not exist. Pass --DRAUGHTSMAN_DATA=<the service's data folder> (service install prints it).");
            return 1;
        }

        if (await HasAdministrator(folder, cancellationToken))
        {
            await error.WriteLineAsync("This instance already has an administrator, so there is no setup code. Sign in, or run 'draughtsman reset-admin <email> --generate' if the password is lost.");
            return 1;
        }

        var file = new SetupCodeFile(folder);
        string? code;
        try
        {
            code = file.Read();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            await error.WriteLineAsync($"The setup code file {file.Path} cannot be read by this account ({ex.GetType().Name}). Only Administrators and SYSTEM may read it: run this from an elevated (Administrator) prompt.");
            return 1;
        }

        if (string.IsNullOrEmpty(code))
        {
            await error.WriteLineAsync($"There is no setup code at {file.Path}. The server writes it when it starts with no administrator, and only when it runs as a Windows service (any other host prints the code to its own log). Start or restart the service, then run this again.");
            return 1;
        }

        await output.WriteLineAsync($"First-run setup code: {code}");
        return 0;
    }

    /// <summary>True when the instance's database already has a user. A database that cannot be opened (the server has never run on this folder) counts as no administrator: the file, if any, decides.</summary>
    private static async Task<bool> HasAdministrator(string folder, CancellationToken cancellationToken)
    {
        try
        {
            await using ServiceProvider services = ResetAdminCommand.BuildServices(folder, out _);
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IUserStore>().HasAnyUsers(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return false;
        }
    }
}
