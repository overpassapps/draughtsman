using Draughtsman.Server.Cli;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Draughtsman.Server.Configuration;

/// <summary>Resolves the data folder and loads draughtsman.yaml from it with every key defaulted.</summary>
public static class ConfigLoader
{
    /// <summary>
    /// The data folder: DRAUGHTSMAN_DATA if set, otherwise <c>data/</c> beside the executable of a packaged install
    /// (<see cref="AppLayout"/>) or ./data relative to the current directory when running from a build tree. A relative
    /// DRAUGHTSMAN_DATA is relative to the current directory, as a shell user expects.
    /// Always returned as a full path so log lines and error messages are unambiguous.
    /// </summary>
    public static string ResolveDataFolder(string? environmentOverride = null, string? baseDirectory = null)
    {
        string? fromEnvironment = environmentOverride ?? Environment.GetEnvironmentVariable(DraughtsmanConfig.DataFolderEnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? AppLayout.DefaultDataFolder(baseDirectory ?? AppContext.BaseDirectory)
            : Path.GetFullPath(fromEnvironment);
    }

    /// <summary>Loads draughtsman.yaml from the data folder. An absent or empty file yields the defaults.</summary>
    public static DraughtsmanConfig Load(string dataFolder)
    {
        return LoadWithWarnings(dataFolder).Config;
    }

    /// <summary>
    /// Loads draughtsman.yaml and reports what is out of date in it (<see cref="ConfigCompatibility"/>): renamed settings are read
    /// under their new names, removed and unknown ones are ignored, and each is a warning, never a failure.
    /// </summary>
    public static (DraughtsmanConfig Config, IReadOnlyList<ConfigWarning> Warnings) LoadWithWarnings(string dataFolder)
    {
        string path = Path.Combine(dataFolder, DraughtsmanConfig.FileName);
        if (!File.Exists(path))
        {
            return (new DraughtsmanConfig(), []);
        }

        return ParseWithWarnings(File.ReadAllText(path));
    }

    /// <summary>Parses YAML text. Unknown keys are ignored so a newer config file still opens on an older build.</summary>
    public static DraughtsmanConfig Parse(string yaml)
    {
        return ParseWithWarnings(yaml).Config;
    }

    public static (DraughtsmanConfig Config, IReadOnlyList<ConfigWarning> Warnings) ParseWithWarnings(string yaml, IReadOnlyList<ConfigRename>? renames = null, IReadOnlyList<ConfigRemoval>? removals = null)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return (new DraughtsmanConfig(), []);
        }

        (string effective, IReadOnlyList<ConfigWarning> warnings) = ConfigCompatibility.Apply(yaml, renames, removals);
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return (deserializer.Deserialize<DraughtsmanConfig>(effective) ?? new DraughtsmanConfig(), warnings);
    }
}
