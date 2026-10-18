using System.Text.Json;
using System.Text.Json.Serialization;

namespace Draughtsman.Server.Theming;

/// <summary>
/// Reads and writes the admin's branding overrides under the data folder's <c>branding/</c> directory: a small
/// <c>branding.json</c> naming the product name and any uploaded asset files, and those files themselves. Nothing
/// here validates content; <see cref="ChromeBrandingService"/> owns that and the metadata's shape. A missing,
/// unreadable or malformed metadata file is treated as "no overrides" with a warning, so a stray edit can never stop
/// the instance starting: the shipped theme is always there to fall back to.
/// </summary>
internal sealed class BrandingStore(DataFolder dataFolder, ILogger<BrandingStore> logger)
{
    public const string DirectoryName = "branding";

    private const string MetadataFileName = "branding.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private string Folder => Path.Combine(dataFolder.Path, DirectoryName);

    private string MetadataPath => Path.Combine(Folder, MetadataFileName);

    /// <summary>The stored overrides, or <see cref="BrandingOverrides.Empty"/> when none are saved or the file cannot be read.</summary>
    public BrandingOverrides Load()
    {
        if (!File.Exists(MetadataPath))
        {
            return BrandingOverrides.Empty;
        }

        try
        {
            string json = File.ReadAllText(MetadataPath);
            BrandingMetadata? metadata = JsonSerializer.Deserialize<BrandingMetadata>(json, JsonOptions);
            if (metadata is null)
            {
                return BrandingOverrides.Empty;
            }

            return new BrandingOverrides(metadata.ProductName, metadata.Logo, metadata.LogoLight, metadata.Favicon);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Branding metadata {Path} could not be read; using the chrome theme's own branding.", MetadataPath);
            return BrandingOverrides.Empty;
        }
    }

    /// <summary>Persists the overrides; an empty set deletes the metadata file so a reset leaves no trace.</summary>
    public void Save(BrandingOverrides overrides)
    {
        if (overrides == BrandingOverrides.Empty)
        {
            TryDelete(MetadataPath);
            return;
        }

        Directory.CreateDirectory(Folder);
        var metadata = new BrandingMetadata
        {
            ProductName = overrides.ProductName,
            Logo = overrides.Logo,
            LogoLight = overrides.LogoLight,
            Favicon = overrides.Favicon,
        };

        string json = JsonSerializer.Serialize(metadata, JsonOptions);
        WriteAtomically(MetadataPath, System.Text.Encoding.UTF8.GetBytes(json));
    }

    public byte[]? ReadAsset(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        // Only a bare name is ever written, but strip any directory so a hand-edited metadata file cannot point outside branding/.
        string path = Path.Combine(Folder, Path.GetFileName(fileName));
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void WriteAsset(string fileName, byte[] content)
    {
        Directory.CreateDirectory(Folder);
        WriteAtomically(Path.Combine(Folder, Path.GetFileName(fileName)), content);
    }

    public void DeleteAsset(string? fileName)
    {
        if (!string.IsNullOrEmpty(fileName))
        {
            TryDelete(Path.Combine(Folder, Path.GetFileName(fileName)));
        }
    }

    /// <summary>Removes the metadata file and every stored asset, returning the instance to the theme's own branding.</summary>
    public void DeleteAll()
    {
        TryDelete(MetadataPath);
        if (!Directory.Exists(Folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(Folder))
        {
            TryDelete(file);
        }
    }

    /// <summary>Writes through a temp file and renames into place, so a reader never sees a half-written file.</summary>
    private static void WriteAtomically(string path, byte[] content)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete branding file {Path}.", path);
        }
    }

    private sealed class BrandingMetadata
    {
        public string? ProductName { get; set; }

        public string? Logo { get; set; }

        public string? LogoLight { get; set; }

        public string? Favicon { get; set; }
    }
}
