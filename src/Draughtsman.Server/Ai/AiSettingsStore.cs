using System.Text.Json;
using System.Text.Json.Serialization;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.DataProtection;

namespace Draughtsman.Server.Ai;

/// <summary>
/// What <see cref="AiSettingsStore.Load"/> found: the overrides, the decrypted key if there is one, whether a stored key could not be decrypted, and
/// whether the key opened only under an install path an earlier build bound it to (<see cref="StoredSecretsMigration"/> then re-protects it).
/// </summary>
internal sealed record StoredAiSettings(AiOverrides Overrides, string? ApiKey, bool KeyUnreadable, bool ReadUnderLegacy = false)
{
    public static readonly StoredAiSettings Empty = new(AiOverrides.None, null, false);
}

/// <summary>
/// Reads and writes the admin's AI settings as <c>ai/settings.json</c> under the data folder. The provider, endpoint,
/// model and API version are plain text; the API key is written only as an ASP.NET Core Data Protection payload
/// (the key ring already lives in <c>keys/</c> beside it), never in plain text, and the file is owner-read/write on
/// Unix. A missing or unreadable file is "no overrides" with a warning, so a stray edit or a lost key ring can never
/// stop the instance starting: draughtsman.yaml is always there to fall back to. Nothing logged or thrown from here
/// ever contains the key.
/// <para>
/// The key is bound to <see cref="DataProtectionIdentity.ApplicationName"/> by the host's provider, not to where the program is installed.
/// One written by a build that predates that is bound to its install path and opens through <paramref name="legacy"/>, read-only; saving
/// writes under the current name, and <see cref="StoredSecretsMigration"/> moves an old one across at startup.
/// </para>
/// </summary>
internal sealed class AiSettingsStore(DataFolder dataFolder, IDataProtectionProvider protection, ILogger<AiSettingsStore> logger, LegacySecretReader? legacy = null) : IProtectedSecretOwner
{
    public const string DirectoryName = "ai";

    private const string FileName = "settings.json";

    private const string ProtectorPurpose = "Draughtsman.Server.Ai.ApiKey.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly SecretProtector _secret = new(protection, ProtectorPurpose, legacy);

    public string SecretName => "AI API key";

    public string RelativeFile => DirectoryName + "/" + FileName;

    public string WhereToEnter => "Admin > AI";

    public SecretProtector Protector => _secret;

    public ProtectedSecretFile SecretFile => new(SettingsPath, "protectedApiKey");

    private string Folder => Path.Combine(dataFolder.Path, DirectoryName);

    private string SettingsPath => Path.Combine(Folder, FileName);

    /// <param name="allowLegacy">False reads only what is stored the way this build stores it (re-protection's own read-back); the default also opens a key an earlier build bound to its install path.</param>
    public StoredAiSettings Load(bool allowLegacy = true)
    {
        if (!File.Exists(SettingsPath))
        {
            return StoredAiSettings.Empty;
        }

        AiSettingsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<AiSettingsFile>(File.ReadAllText(SettingsPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("AI settings {Path} could not be read ({Reason}); using draughtsman.yaml.", SettingsPath, ex.GetType().Name);
            return StoredAiSettings.Empty;
        }

        if (file is null)
        {
            return StoredAiSettings.Empty;
        }

        string? apiKey = null;
        bool unreadable = false;
        bool underLegacy = false;
        if (!string.IsNullOrEmpty(file.ProtectedApiKey))
        {
            SecretOutcome outcome = _secret.Unprotect(file.ProtectedApiKey, allowLegacy);
            apiKey = outcome.Plaintext;
            underLegacy = outcome.Reading == SecretReading.Legacy;
            if (outcome.Reading == SecretReading.Unreadable)
            {
                // The key ring changed or was lost, or an earlier build bound it to a path nobody has named. Say so (why, never the key) and treat the key as unset; never guess.
                unreadable = true;
                logger.LogWarning("The stored AI API key could not be decrypted ({Reason}); paste it again in the AI settings.", outcome.Why);
            }
        }

        return new StoredAiSettings(new AiOverrides(file.Provider, file.Model, file.Endpoint, file.ApiVersion), apiKey, unreadable, underLegacy);
    }

    /// <summary>Persists the overrides and the (plain text, encrypted here) key; nothing left to store deletes the file so a reset leaves no trace.</summary>
    public void Save(AiOverrides overrides, string? apiKey)
    {
        if (overrides.IsEmpty && apiKey is null)
        {
            // Not swallowed: a clear that leaves the encrypted key on disk would bring it back on restart.
            File.Delete(SettingsPath);
            return;
        }

        Directory.CreateDirectory(Folder);
        var file = new AiSettingsFile
        {
            Provider = overrides.Provider,
            Model = overrides.Model,
            Endpoint = overrides.Endpoint,
            ApiVersion = overrides.ApiVersion,
            ProtectedApiKey = apiKey is null ? null : _secret.Protect(apiKey),
        };

        string temp = SettingsPath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, SettingsPath, overwrite: true);
    }

    private sealed class AiSettingsFile
    {
        public string? Provider { get; set; }

        public string? Model { get; set; }

        public string? Endpoint { get; set; }

        public string? ApiVersion { get; set; }

        public string? ProtectedApiKey { get; set; }
    }
}
