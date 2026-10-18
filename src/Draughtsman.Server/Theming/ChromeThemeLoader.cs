using System.Reflection;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Draughtsman.Server.Theming;

/// <summary>
/// Resolves and parses the configured chrome theme at startup. A relative <c>theme.chrome</c> resolves against the
/// data folder's <c>themes/</c> directory first, then the data folder itself, then the themes embedded in this
/// assembly (the shipped Overpass default and the example client theme). A missing or invalid file logs a warning and
/// falls back to the shipped default: a bad theme is a support conversation, never a failed start (spec section 8).
/// </summary>
internal static class ChromeThemeLoader
{
    /// <summary>Shipped default chrome theme; the fallback when nothing else can be loaded.</summary>
    internal const string DefaultFile = "chrome.yaml";

    /// <summary>The shipped Overpass default theme file's text, from the assembly (never the data folder), or null if it is missing.</summary>
    public static string? ShippedDefaultYaml()
    {
        byte[]? embedded = ReadEmbedded("themes/" + DefaultFile);
        return embedded is null ? null : Encoding.UTF8.GetString(embedded);
    }

    public static LoadedChromeTheme Load(string dataFolder, string configured, ILogger logger)
    {
        LoadedChromeTheme? loaded = TryLoad(dataFolder, configured, logger);
        if (loaded is not null)
        {
            return loaded;
        }

        LoadedChromeTheme? fallback = TryLoad(dataFolder, DefaultFile, logger);
        if (fallback is null)
        {
            throw new InvalidOperationException($"The shipped chrome theme (themes/{DefaultFile}) is not embedded in the server assembly.");
        }

        logger.LogWarning("Using the shipped chrome theme {Name} instead.", fallback.Theme.Name);
        return fallback;
    }

    private static LoadedChromeTheme? TryLoad(string dataFolder, string configured, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            logger.LogWarning("theme.chrome is empty; no chrome theme file named.");
            return null;
        }

        string? path = ResolveDiskPath(dataFolder, configured);
        if (path is not null)
        {
            try
            {
                ChromeTheme theme = Parse(File.ReadAllText(path), path);
                string baseDirectory = Path.GetDirectoryName(path) ?? dataFolder;
                return new LoadedChromeTheme
                {
                    Theme = theme,
                    Source = path,
                    Logo = ResolveAsset(baseDirectory, theme.Logo),
                    LogoLight = ResolveAsset(baseDirectory, theme.LogoLight),
                    LogoCompact = ResolveAsset(baseDirectory, theme.LogoCompact),
                    LogoCompactLight = ResolveAsset(baseDirectory, theme.LogoCompactLight),
                    Favicon = ResolveAsset(baseDirectory, theme.Favicon ?? theme.Logo),
                };
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or YamlException)
            {
                logger.LogWarning(ex, "Chrome theme {Path} could not be loaded; it must be a readable YAML map.", path);
                return null;
            }
        }

        string normalized = configured.Replace('\\', '/').TrimStart('/');
        foreach (string candidate in new[] { $"themes/{normalized}", $"themes/{Path.GetFileName(normalized)}" })
        {
            byte[]? embedded = ReadEmbedded(candidate);
            if (embedded is null)
            {
                continue;
            }

            try
            {
                ChromeTheme theme = Parse(Encoding.UTF8.GetString(embedded), candidate);
                return new LoadedChromeTheme
                {
                    Theme = theme,
                    Source = $"embedded {candidate}",
                    Logo = ResolveAsset(null, theme.Logo),
                    LogoLight = ResolveAsset(null, theme.LogoLight),
                    LogoCompact = ResolveAsset(null, theme.LogoCompact),
                    LogoCompactLight = ResolveAsset(null, theme.LogoCompactLight),
                    Favicon = ResolveAsset(null, theme.Favicon ?? theme.Logo),
                };
            }
            catch (Exception ex) when (ex is FormatException or YamlException)
            {
                logger.LogWarning(ex, "Embedded chrome theme {Resource} could not be loaded.", candidate);
                return null;
            }
        }

        logger.LogWarning("Chrome theme {Configured} was not found under {DataFolder}/themes, {DataFolder}, or the shipped themes.", configured, dataFolder, dataFolder);
        return null;
    }

    private static string? ResolveDiskPath(string dataFolder, string configured)
    {
        if (Path.IsPathRooted(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        string inThemes = Path.Combine(dataFolder, "themes", configured);
        if (File.Exists(inThemes))
        {
            return inThemes;
        }

        string inData = Path.Combine(dataFolder, configured);
        return File.Exists(inData) ? inData : null;
    }

    private static ChromeThemeAsset? ResolveAsset(string? baseDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        if (baseDirectory is not null)
        {
            string candidate = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
            if (File.Exists(candidate))
            {
                return new ChromeThemeAsset(Path.GetFileName(candidate), File.ReadAllBytes(candidate), ContentTypeFor(candidate));
            }
        }

        string resource = "themes/" + relativePath.Replace('\\', '/').TrimStart('/');
        byte[]? embedded = ReadEmbedded(resource) ?? ReadEmbedded("themes/assets/" + Path.GetFileName(relativePath));
        return embedded is null
            ? null
            : new ChromeThemeAsset(Path.GetFileName(relativePath), embedded, ContentTypeFor(relativePath));
    }

    private static byte[]? ReadEmbedded(string resourceName)
    {
        using Stream? stream = typeof(ChromeThemeLoader).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using MemoryStream memory = new();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };
    }

    private static ChromeTheme Parse(string yaml, string source)
    {
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        ChromeThemeYaml? raw;
        try
        {
            raw = deserializer.Deserialize<ChromeThemeYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FormatException($"{source}: could not be parsed as YAML", ex);
        }

        if (raw is null)
        {
            throw new FormatException($"{source}: expected a map at the top level");
        }

        return raw.ToTheme(source);
    }

    private sealed class ChromeThemeYaml
    {
        public string? Name { get; set; }

        public string? ProductName { get; set; }

        public string? Logo { get; set; }

        public string? LogoLight { get; set; }

        public string? LogoCompact { get; set; }

        public string? LogoCompactLight { get; set; }

        public string? Favicon { get; set; }

        public string? Mode { get; set; }

        public Dictionary<string, string>? Colors { get; set; }

        public ChromeCompanionYaml? Light { get; set; }

        public ChromeCompanionYaml? Dark { get; set; }

        public double? Radius { get; set; }

        public Dictionary<string, string>? Font { get; set; }

        public ChromeTheme ToTheme(string source)
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new FormatException($"{source}: \"name\" is required");
            }

            if (string.IsNullOrWhiteSpace(ProductName))
            {
                throw new FormatException($"{source}: \"productName\" is required");
            }

            if (Mode is not ("dark" or "light" or "system"))
            {
                throw new FormatException($"{source}: \"mode\" must be dark, light or system");
            }

            if (Colors is null || Colors.Count == 0)
            {
                throw new FormatException($"{source}: \"colors\" must be a non-empty map");
            }

            if (Font is null || Font.Count == 0)
            {
                throw new FormatException($"{source}: \"font\" must be a non-empty map");
            }

            if (Radius is not { } radius || !double.IsFinite(radius))
            {
                throw new FormatException($"{source}: \"radius\" must be a number");
            }

            return new ChromeTheme
            {
                Name = Name,
                ProductName = ProductName,
                Logo = string.IsNullOrWhiteSpace(Logo) ? null : Logo,
                LogoLight = string.IsNullOrWhiteSpace(LogoLight) ? null : LogoLight,
                LogoCompact = string.IsNullOrWhiteSpace(LogoCompact) ? null : LogoCompact,
                LogoCompactLight = string.IsNullOrWhiteSpace(LogoCompactLight) ? null : LogoCompactLight,
                Favicon = string.IsNullOrWhiteSpace(Favicon) ? null : Favicon,
                Mode = Mode,
                Colors = Colors,
                Light = Light?.Colors,
                Dark = Dark?.Colors,
                Radius = radius,
                Font = Font,
            };
        }
    }

    private sealed class ChromeCompanionYaml
    {
        public Dictionary<string, string>? Colors { get; set; }
    }
}
