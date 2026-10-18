using System.Text.Json;
using System.Text.Json.Serialization;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.DataProtection;

namespace Draughtsman.Server.Email;

/// <summary>What an administrator has saved for outgoing email. Every member null means "not set here": the layer beneath (the default) applies.</summary>
public sealed record EmailOverrides(string? Provider = null, string? From = null, string? ReplyTo = null, string? Host = null, int? Port = null, string? Tls = null, string? User = null, string? PublicUrl = null)
{
    public static readonly EmailOverrides None = new();

    public bool IsEmpty => this == None;
}

/// <summary>
/// What <see cref="EmailSettingsStore.Load"/> found: the saved fields, the decrypted password if there is one, whether a saved password could not be
/// decrypted, and whether it opened only under an install path an earlier build bound it to (<see cref="StoredSecretsMigration"/> then re-protects it).
/// </summary>
public sealed record StoredEmailSettings(EmailOverrides Overrides, string? Password, bool PasswordUnreadable, bool ReadUnderLegacy = false)
{
    public static readonly StoredEmailSettings Empty = new(EmailOverrides.None, null, false);
}

/// <summary>
/// Reads and writes the admin's email settings as <c>email/settings.json</c> under the data folder, the way
/// <c>AiSettingsStore</c> keeps the AI settings: plain text for the fields, the SMTP password only as an ASP.NET Core Data Protection
/// payload (the key ring lives in <c>keys/</c> beside it), the file owner-only on Unix, written through a temp file so a crash cannot
/// leave half of it. A missing or unreadable file is "nothing saved" with a warning, so a stray edit or a lost key ring can never stop
/// the instance starting. Nothing logged or thrown from here ever contains the password.
/// <para>
/// The password is bound to <see cref="DataProtectionIdentity.ApplicationName"/> by the host's provider, not to where the program is installed; one
/// written by a build that predates that opens through <paramref name="legacy"/>, read-only (see <c>AiSettingsStore</c>).
/// </para>
/// </summary>
internal sealed class EmailSettingsStore(DataFolder dataFolder, IDataProtectionProvider protection, ILogger<EmailSettingsStore> logger, LegacySecretReader? legacy = null) : IProtectedSecretOwner
{
    public const string DirectoryName = "email";

    private const string FileName = "settings.json";

    private const string ProtectorPurpose = "Draughtsman.Server.Email.SmtpPassword.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly SecretProtector _secret = new(protection, ProtectorPurpose, legacy);

    public string SecretName => "SMTP password";

    public string RelativeFile => DirectoryName + "/" + FileName;

    public string WhereToEnter => "Admin > Email";

    public SecretProtector Protector => _secret;

    public ProtectedSecretFile SecretFile => new(SettingsPath, "protectedPassword");

    private string Folder => Path.Combine(dataFolder.Path, DirectoryName);

    private string SettingsPath => Path.Combine(Folder, FileName);

    /// <param name="allowLegacy">False reads only what is stored the way this build stores it (re-protection's own read-back); the default also opens a password an earlier build bound to its install path.</param>
    public StoredEmailSettings Load(bool allowLegacy = true)
    {
        if (!File.Exists(SettingsPath))
        {
            return StoredEmailSettings.Empty;
        }

        EmailSettingsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<EmailSettingsFile>(File.ReadAllText(SettingsPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Email settings {Path} could not be read ({Reason}); using draughtsman.yaml.", SettingsPath, ex.GetType().Name);
            return StoredEmailSettings.Empty;
        }

        if (file is null)
        {
            return StoredEmailSettings.Empty;
        }

        string? password = null;
        bool unreadable = false;
        bool underLegacy = false;
        if (!string.IsNullOrEmpty(file.ProtectedPassword))
        {
            SecretOutcome outcome = _secret.Unprotect(file.ProtectedPassword, allowLegacy);
            password = outcome.Plaintext;
            underLegacy = outcome.Reading == SecretReading.Legacy;
            if (outcome.Reading == SecretReading.Unreadable)
            {
                // The key ring changed or was lost, or an earlier build bound it to a path nobody has named. Say so (why, never the password) and treat the password as unset; never guess.
                unreadable = true;
                logger.LogWarning("The saved SMTP password could not be decrypted ({Reason}); enter it again in the email settings.", outcome.Why);
            }
        }

        return new StoredEmailSettings(
            new EmailOverrides(file.Provider, file.From, file.ReplyTo, file.Host, file.Port, file.Tls, file.User, file.PublicUrl),
            password,
            unreadable,
            underLegacy);
    }

    /// <summary>Persists the fields and the (plain text, encrypted here) password; nothing left to store deletes the file so a reset leaves no trace.</summary>
    public void Save(EmailOverrides overrides, string? password)
    {
        if (overrides.IsEmpty && password is null)
        {
            // Not swallowed: a clear that leaves the encrypted password on disk would bring it back on restart.
            if (File.Exists(SettingsPath))
            {
                File.Delete(SettingsPath);
            }

            return;
        }

        Directory.CreateDirectory(Folder);
        var file = new EmailSettingsFile
        {
            Provider = overrides.Provider,
            From = overrides.From,
            ReplyTo = overrides.ReplyTo,
            Host = overrides.Host,
            Port = overrides.Port,
            Tls = overrides.Tls,
            User = overrides.User,
            PublicUrl = overrides.PublicUrl,
            ProtectedPassword = password is null ? null : _secret.Protect(password),
        };

        string temp = SettingsPath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, SettingsPath, overwrite: true);
    }

    private sealed class EmailSettingsFile
    {
        public string? Provider { get; set; }

        public string? From { get; set; }

        public string? ReplyTo { get; set; }

        public string? Host { get; set; }

        public int? Port { get; set; }

        public string? Tls { get; set; }

        public string? User { get; set; }

        public string? PublicUrl { get; set; }

        public string? ProtectedPassword { get; set; }
    }
}
