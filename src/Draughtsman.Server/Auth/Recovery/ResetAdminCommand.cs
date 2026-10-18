using Draughtsman.Core.Auth;
using Draughtsman.Server.Configuration;
using Draughtsman.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Auth.Recovery;

/// <summary>
/// <c>draughtsman reset-admin &lt;email&gt; [--generate] [--promote]</c>: the host-shell face of
/// <see cref="AdminRecoveryService"/> (AU-03). Reads the same <c>draughtsman.yaml</c> and data folder the server does, so
/// it needs no network and no running server, and prints nothing but its own result to stdout/stderr.
/// <para>
/// The new password is never taken from the command line (it would sit in shell history and the process list): it is
/// generated (<c>--generate</c>, printed once), read from <c>DRAUGHTSMAN_NEW_PASSWORD</c>, read from piped standard
/// input, or typed at a hidden prompt, in that order of preference. Exit codes: 0 done, 1 refused (see the message),
/// 2 usage.
/// </para>
/// <para>
/// Registered as the <c>reset-admin</c> verb in <c>Cli/ServerCli.cs</c> (<c>CliVerb.ResetAdmin</c>), so it runs from the
/// same binary as <c>serve</c> and <c>mcp --stdio</c> and never starts the web host. A <c>--DRAUGHTSMAN_DATA=&lt;folder&gt;</c>
/// option picks the data folder, as it does for the other verbs.
/// </para>
/// </summary>
public static class ResetAdminCommand
{
    private const string DataOption = "--DRAUGHTSMAN_DATA=";

    public const string PasswordEnvironmentVariable = "DRAUGHTSMAN_NEW_PASSWORD";

    public const string Usage = "Usage: draughtsman reset-admin <email> [--generate] [--promote]\n" +
        "  Sets a new password for an administrator account, reactivating it if needed, and revokes its sessions and API tokens.\n" +
        "  --generate  choose a random password and print it once\n" +
        "  --promote   also make an Editor account an Admin (refused without this)\n" +
        "  --DRAUGHTSMAN_DATA=<folder>  the data folder (default: DRAUGHTSMAN_DATA, else ./data)\n" +
        "  The password is read from DRAUGHTSMAN_NEW_PASSWORD, piped standard input, or a hidden prompt; never from an argument.\n" +
        "  Run it on the host, as a user that can write the data folder.";

    /// <param name="args">Everything after the verb.</param>
    /// <param name="dataFolder">Null means the usual resolution (<c>DRAUGHTSMAN_DATA</c>, else <c>./data</c>); tests pass a temp folder.</param>
    /// <param name="promptPassword">Reads a password without echo; null means the real console prompt.</param>
    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, string? dataFolder = null, Func<string, string?>? promptPassword = null, Func<string, string?>? getEnvironment = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? email = null;
        bool generate = false;
        bool promote = false;
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--generate":
                    generate = true;
                    break;
                case "--promote":
                    promote = true;
                    break;
                case "-h" or "--help":
                    await output.WriteLineAsync(Usage);
                    return 0;
                case var option when option.StartsWith(DataOption, StringComparison.Ordinal):
                    dataFolder ??= Path.GetFullPath(option[DataOption.Length..]);
                    break;
                default:
                    if (arg.StartsWith('-') || email is not null)
                    {
                        await error.WriteLineAsync($"Unexpected argument '{arg}'.\n{Usage}");
                        return 2;
                    }

                    email = arg;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }

        string? password = generate ? AdminRecoveryService.GeneratePassword() : await ReadPassword(input, error, promptPassword, getEnvironment ?? Environment.GetEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
        {
            await error.WriteLineAsync($"No password given. Use --generate, set {PasswordEnvironmentVariable}, pipe one in, or type it at the prompt.");
            return 2;
        }

        string folder = dataFolder ?? ConfigLoader.ResolveDataFolder();
        if (!Directory.Exists(folder))
        {
            await error.WriteLineAsync($"The data folder '{folder}' does not exist. Set {DraughtsmanConfig.DataFolderEnvironmentVariable} to the server's data folder.");
            return 1;
        }

        await using ServiceProvider services = BuildServices(folder, out StorageOptions storage, error);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Draughtsman.Server.Recovery");
        try
        {
            await StorageMigrator.Migrate(scope.ServiceProvider.GetRequiredService<DraughtsmanDbContext>(), storage, logger, cancellationToken);
        }
        catch (SchemaNewerThanBuildException refusal)
        {
            // This build is older than the database (OP-03): say so and what to do, rather than an unhandled exception.
            await error.WriteLineAsync(refusal.Message);
            return 1;
        }
        catch (Exception undone) when (undone is MigrationRolledBackException or PreMigrationCopyException or StorageOpenException)
        {
            // The upgrade this command would have had to run first was undone, or refused before it began: the message says which and what to do.
            await error.WriteLineAsync(undone.Message);
            return 1;
        }

        AdminRecoveryResult result = await scope.ServiceProvider.GetRequiredService<AdminRecoveryService>()
            .Reset(email, password, promote, Environment.UserName, cancellationToken);
        switch (result.Outcome)
        {
            case AdminRecoveryOutcome.Reset:
                await output.WriteLineAsync($"Password reset for {result.User!.Email}. Their sessions and API tokens were revoked." +
                    (result.Promoted ? " They were promoted to Admin." : string.Empty) + (result.Reactivated ? " The account was reactivated." : string.Empty));
                if (generate)
                {
                    await output.WriteLineAsync($"New password (shown once): {password}");
                }

                return 0;
            case AdminRecoveryOutcome.NoUsers:
                await error.WriteLineAsync("This instance has no users yet, so there is nothing to recover: open the server's first-run page instead.");
                return 1;
            case AdminRecoveryOutcome.NotAnAdmin:
                await error.WriteLineAsync($"{result.User!.Email} is not an administrator. Re-run with --promote to make them one.");
                return 1;
            case AdminRecoveryOutcome.PasswordTooShort:
                await error.WriteLineAsync($"The password must be at least {PasswordHasher.MinimumLength} characters.");
                return 1;
            default:
                await error.WriteLineAsync($"No account with the email '{email}'.");
                return 1;
        }
    }

    /// <summary>The service graph the verb (and its tests) run against: storage, the security event log and the recovery service. No HTTP, no sidecar.</summary>
    internal static ServiceProvider BuildServices(string dataFolder, out StorageOptions storage, TextWriter? logTo = null)
    {
        DraughtsmanConfig config = ConfigLoader.Load(dataFolder);
        storage = StorageOptions.FromConfig(config.Storage.Provider, config.Storage.ConnectionString, dataFolder);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            if (logTo is not null)
            {
                builder.AddSimpleConsole(options => options.SingleLine = true);
                builder.SetMinimumLevel(LogLevel.Warning);
            }
        });
        services.AddSingleton(TimeProvider.System);
        services.AddDraughtsmanStorage(storage);
        services.AddScoped<SecurityEventLog>();
        services.AddScoped<AdminRecoveryService>();
        return services.BuildServiceProvider();
    }

    private static async Task<string?> ReadPassword(TextReader input, TextWriter error, Func<string, string?>? promptPassword, Func<string, string?> getEnvironment)
    {
        string? fromEnvironment = getEnvironment(PasswordEnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        if (promptPassword is not null)
        {
            return promptPassword("New password: ");
        }

        // Piped input (or a reader the caller supplied, as the verb dispatcher's tests do) is read as one line; only a real
        // terminal gets the hidden prompt.
        if (!ReferenceEquals(input, Console.In) || Console.IsInputRedirected)
        {
            return (await input.ReadLineAsync())?.TrimEnd('\r', '\n');
        }

        string? first = ReadHidden("New password: ");
        string? second = ReadHidden("Repeat it: ");
        if (first != second)
        {
            await error.WriteLineAsync("The two passwords did not match.");
            return null;
        }

        return first;
    }

    private static string? ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }
}
