using Draughtsman.Server.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// What gets logged, and where (AU-14). One light editor session used to write about 6,000 lines, most of them EF Core's
/// SQL commands and ASP.NET Core's request lines at Information, and the quieter setting lived in an <c>appsettings.json</c>
/// that is only read from the current directory, so it vanished whenever the process started anywhere else (a service's
/// default). Levels now come from <c>logging</c> in draughtsman.yaml, in the data folder, found from anywhere:
/// the server's own messages at <c>logging.level</c> and the frameworks at <c>logging.frameworkLevel</c> (Warning),
/// with the lines an operator wants kept (the listening address, refused Host headers) held at Information. A level set
/// anywhere <c>Logging:LogLevel</c> is read (an environment variable, appsettings.json beside the binary) still wins for its
/// own category. An optional rolling file goes to <c>&lt;data&gt;/logs/</c>.
/// </summary>
public static class LoggingSetup
{
    /// <summary>Framework categories held to <c>logging.frameworkLevel</c>; everything under each one follows.</summary>
    private static readonly string[] s_frameworkCategories = ["Microsoft", "System"];

    /// <summary>Framework categories that stay at Information whatever the framework level: what tells an operator the server is up and why a request was refused.</summary>
    private static readonly string[] s_keptCategories = ["Microsoft.Hosting.Lifetime", "Microsoft.AspNetCore.HostFiltering"];

    public static WebApplicationBuilder ConfigureDraughtsmanLogging(this WebApplicationBuilder builder, DraughtsmanConfig config, string dataFolder)
    {
        AddAppSettingsBesideTheBinary(builder.Configuration, AppContext.BaseDirectory);

        LogLevel level = ParseLevel(config.Logging.Level, LogLevel.Information);
        LogLevel framework = ParseLevel(config.Logging.FrameworkLevel, LogLevel.Warning);
        builder.Logging.SetMinimumLevel(level);
        foreach (string category in s_frameworkCategories)
        {
            AddDefault(builder, category, framework);
        }

        // EF Core reports a command that failed as an error even when it expected to, and it does on every clean start of
        // Postgres: it reads the migration history table before the table exists, which is how it learns there is nothing
        // applied yet. Two `fail:` lines in an otherwise healthy first start teach an operator to ignore errors, so SQL
        // command failures stay out of the log unless the framework level is lowered to Information or below. Nothing is lost:
        // a query that fails for real throws, and the exception handler logs it with the database's own message.
        AddDefault(builder, "Microsoft.EntityFrameworkCore.Database.Command", framework < LogLevel.Warning ? framework : LogLevel.None);
        AddEventLogDefault(builder, "Microsoft.EntityFrameworkCore.Database.Command", framework < LogLevel.Warning ? LogLevel.Warning : LogLevel.None);

        // The authentication handlers' base class logs "Cookie was challenged" at Information under the handler's own
        // (Draughtsman) category for every 401, which the editor's own polling produces while signed out: framework chatter.
        foreach (string handler in new[] { "Draughtsman.Server.Auth.CookieSessionAuthenticationHandler", "Draughtsman.Server.Auth.ApiTokenAuthenticationHandler" })
        {
            AddDefault(builder, handler, framework);
        }

        foreach (string category in s_keptCategories)
        {
            AddDefault(builder, category, framework < LogLevel.Information ? framework : LogLevel.Information);
        }

        if (config.Logging.File.Enabled)
        {
            builder.Logging.AddProvider(new RollingFileLoggerProvider(Path.Combine(dataFolder, "logs"), config.Logging.File.RetainDays, config.Logging.File.MaxFileBytes));
        }

        return builder;
    }

    /// <summary>A level for a category, unless configuration (an environment variable, an appsettings file) already names one, which is the more deliberate choice.</summary>
    private static void AddDefault(WebApplicationBuilder builder, string category, LogLevel level)
    {
        if (builder.Configuration.GetSection("Logging:LogLevel")[category] is null)
        {
            builder.Logging.AddFilter(category, level);
        }
    }

    /// <summary>
    /// The same, for the Windows Event Log provider. The default host adds it on Windows with its own rule (Warning and above, for
    /// every category), and a rule that names a provider beats any rule that names a category but no provider, whatever the
    /// category's length. So the category rules above never reach the Event Log, and a failed SQL command would still be written
    /// there as an error on a Windows service. A rule naming both the provider (by its alias) and the category is more specific
    /// than the default one and wins. The Event Log keeps its Warning floor: lowering <c>logging.frameworkLevel</c> does not
    /// flood it. Harmless where the provider does not exist (macOS, Linux).
    /// </summary>
    private static void AddEventLogDefault(WebApplicationBuilder builder, string category, LogLevel level)
    {
        if (builder.Configuration.GetSection("Logging:LogLevel")[category] is null)
        {
            builder.Services.Configure<LoggerFilterOptions>(options => options.Rules.Add(new LoggerFilterRule("EventLog", category, level, null)));
        }
    }

    /// <summary>
    /// The default configuration reads appsettings.json from the content root, which is the current directory: start the
    /// server from another folder and the file beside the binary is ignored. Reads that one too, placed straight after the
    /// content-root files so the environment and the command line still override it.
    /// </summary>
    internal static void AddAppSettingsBesideTheBinary(ConfigurationManager configuration, string binaryFolder)
    {
        int insertAt = 0;
        for (int i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is JsonConfigurationSource { Path: { } path } json && path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                if (json.FileProvider is PhysicalFileProvider existing && Path.TrimEndingDirectorySeparator(existing.Root) == Path.TrimEndingDirectorySeparator(binaryFolder))
                {
                    return; // already reading the binary's folder: started from it
                }

                insertAt = i + 1;
            }
        }

        if (insertAt == 0)
        {
            return;
        }

        foreach (string name in new[] { "appsettings.json" })
        {
            if (File.Exists(Path.Combine(binaryFolder, name)))
            {
                var source = new JsonConfigurationSource { Path = name, Optional = true, ReloadOnChange = false, FileProvider = new PhysicalFileProvider(binaryFolder) };
                source.ResolveFileProvider();
                configuration.Sources.Insert(insertAt++, source);
            }
        }

        // Sources inserted into a built manager are only picked up by a reload of its providers.
        ((IConfigurationRoot)configuration).Reload();
    }

    internal static LogLevel ParseLevel(string? text, LogLevel fallback)
    {
        return Enum.TryParse(text?.Trim(), ignoreCase: true, out LogLevel parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
    }
}
