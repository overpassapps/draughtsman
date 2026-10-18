using System.Text;
using System.Text.Json;

namespace Draughtsman.Server.Theming;

/// <summary>
/// Reads and writes organisation themes under the data folder's <c>themes/org/</c> directory, one <see cref="OrgThemeYaml"/>
/// file per theme named <c>&lt;id&gt;.yaml</c>, plus <c>_settings.json</c> naming the default. Plain files, so backing
/// up an instance is still copying a folder and an IT team can version them. A file that cannot be read is skipped with
/// a warning, never a failed start: the shipped theme is always there to fall back to. Nothing here validates content;
/// <see cref="OrgThemeService"/> does, and anything read back is validated again because the folder is also hand-editable.
/// </summary>
internal sealed class OrgThemeStore(DataFolder dataFolder, ILogger<OrgThemeStore> logger)
{
    private const string SettingsFileName = "_settings.json";

    /// <summary>The product name the chrome file format requires; an organisation theme does not own the product name.</summary>
    internal const string FilePlaceholderProductName = "Draughtsman";

    private string Folder => Path.Combine(dataFolder.Path, "themes", "org");

    public IReadOnlyList<OrgTheme> LoadAll()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        List<OrgTheme> themes = [];
        foreach (string path in Directory.EnumerateFiles(Folder, "*.yaml").Order(StringComparer.Ordinal))
        {
            string id = Path.GetFileNameWithoutExtension(path);
            if (!OrgThemeValidator.IsValidId(id))
            {
                logger.LogWarning("Organisation theme file {Path} has a name that is not a valid theme id; skipped.", path);
                continue;
            }

            try
            {
                OrgTheme read = OrgThemeYaml.Read(File.ReadAllText(path)) with { Id = id, UpdatedAt = File.GetLastWriteTimeUtc(path) };
                (OrgTheme theme, List<OrgThemeProblem> problems) = OrgThemeValidator.Validate(read);
                if (problems.Count > 0)
                {
                    logger.LogWarning("Organisation theme {Path} is invalid ({Problem}); skipped.", path, problems[0].Message);
                    continue;
                }

                themes.Add(theme);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Organisation theme {Path} could not be read; skipped.", path);
            }
        }

        return themes;
    }

    public void Save(OrgTheme theme)
    {
        Directory.CreateDirectory(Folder);
        WriteAtomically(Path.Combine(Folder, theme.Id + ".yaml"), Encoding.UTF8.GetBytes(OrgThemeYaml.Write(theme, FilePlaceholderProductName)));
    }

    public void Delete(string id)
    {
        string path = Path.Combine(Folder, Path.GetFileName(id) + ".yaml");
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete organisation theme file {Path}.", path);
            throw;
        }
    }

    public string? LoadDefaultId()
    {
        string path = Path.Combine(Folder, SettingsFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("defaultId", out JsonElement id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Organisation theme settings {Path} could not be read; using the shipped theme.", path);
            return null;
        }
    }

    public void SaveDefaultId(string? id)
    {
        string path = Path.Combine(Folder, SettingsFileName);
        if (id is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(Folder);
        WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(new { defaultId = id }));
    }

    /// <summary>Writes through a temp file and renames into place, so a reader never sees a half-written file.</summary>
    private static void WriteAtomically(string path, byte[] content)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
