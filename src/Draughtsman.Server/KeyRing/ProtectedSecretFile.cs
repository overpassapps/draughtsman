using System.Text.Json;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.KeyRing;

/// <summary>
/// The one protected field of a settings file (<c>ai/settings.json</c>'s <c>protectedApiKey</c>, <c>email/settings.json</c>'s
/// <c>protectedPassword</c>), read and replaced in place without touching anything else in the file: an unknown member a newer build wrote
/// stays, which re-serialising the store's own record would drop. The replacement goes through a temporary file and one rename, owner-only on
/// Unix, like every write the stores make.
/// </summary>
internal sealed class ProtectedSecretFile(string path, string field)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string Path => path;

    /// <summary>Runs after the new file is fully written and before the rename publishes it: where a crash would leave both the old file and a finished new one. Tests throw here to prove the old file is still whole.</summary>
    internal Action? BeforePublish { get; init; }

    /// <summary>The stored payload, or null when the file is absent, not an object, or holds no such field. Never throws for a bad file.</summary>
    public string? ReadPayload()
    {
        try
        {
            return File.Exists(path) && Parse() is { } root && Find(root) is { } name && root[name] is JsonValue value && value.TryGetValue(out string? payload) && !string.IsNullOrEmpty(payload)
                ? payload
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Replaces the field's value with <paramref name="payload"/> and nothing else. Throws if the file or the field is not there to replace.</summary>
    public void ReplacePayload(string payload)
    {
        JsonObject root = Parse() ?? throw new InvalidOperationException("The settings file is not a JSON object.");
        string name = Find(root) ?? throw new InvalidOperationException("The settings file has no protected value to replace.");
        root[name] = payload;
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, root.ToJsonString(Indented));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            BeforePublish?.Invoke();
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // The old file is untouched until the rename; a half-made replacement must not be left to be mistaken for anything.
            File.Delete(temp);
            throw;
        }
    }

    private JsonObject? Parse()
    {
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    }

    /// <summary>The member's spelling in this file: the stores read case-insensitively, so a hand-edited file may not use the camel case they write.</summary>
    private string? Find(JsonObject root)
    {
        return root.Select(pair => pair.Key).FirstOrDefault(key => string.Equals(key, field, StringComparison.OrdinalIgnoreCase));
    }
}
