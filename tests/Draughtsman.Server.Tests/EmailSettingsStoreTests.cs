using Draughtsman.Server.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The disk half of the email settings: the SMTP password is written encrypted with Data Protection, never as plain text, survives a
/// restart (a second store over the same folder and key ring), reads as "unset and flagged" when the key ring is lost, and the file is
/// owner-only. The HTTP tests never restart the host, so at-rest encryption and persistence are proved here.
/// </summary>
public class EmailSettingsStoreTests : IDisposable
{
    private const string Password = "smtp-store-secret-1234567890";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-email-store-tests", Guid.NewGuid().ToString("N"));

    public EmailSettingsStoreTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private string SettingsFile => Path.Combine(_folder, "email", "settings.json");

    private EmailSettingsStore Store(string ring = "ring")
    {
        return new EmailSettingsStore(new DataFolder(_folder), DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_folder, ring))), NullLogger<EmailSettingsStore>.Instance);
    }

    [Fact]
    public void The_password_is_encrypted_on_disk_and_readable_after_a_restart()
    {
        Store().Save(new EmailOverrides(Provider: "smtp", From: "noreply@draughtsman.test", Host: "smtp.draughtsman.test", Port: 2525, User: "mailer"), Password);

        string onDisk = File.ReadAllText(SettingsFile);
        // The half that must fail if someone writes the password as plain text.
        Assert.DoesNotContain(Password, onDisk, StringComparison.Ordinal);
        Assert.Contains("protectedPassword", onDisk, StringComparison.Ordinal);
        Assert.Contains("smtp.draughtsman.test", onDisk, StringComparison.Ordinal);

        StoredEmailSettings reloaded = Store().Load();
        Assert.Equal(Password, reloaded.Password);
        Assert.Equal("smtp.draughtsman.test", reloaded.Overrides.Host);
        Assert.Equal(2525, reloaded.Overrides.Port);
        Assert.False(reloaded.PasswordUnreadable);
    }

    [Fact]
    public void A_password_the_key_ring_cannot_decrypt_reads_as_unset_and_flagged_not_as_a_crash()
    {
        Store().Save(new EmailOverrides(Host: "smtp.draughtsman.test"), Password);

        StoredEmailSettings loaded = Store("a-different-ring").Load();

        Assert.Null(loaded.Password);
        Assert.True(loaded.PasswordUnreadable);
        Assert.Equal("smtp.draughtsman.test", loaded.Overrides.Host);
    }

    [Fact]
    public void A_file_that_is_not_json_is_treated_as_nothing_saved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, "{ this is not json");

        Assert.Equal(StoredEmailSettings.Empty, Store().Load());
    }

    [Fact]
    public void Saving_nothing_removes_the_file_so_a_reset_leaves_no_encrypted_password_behind()
    {
        EmailSettingsStore store = Store();
        store.Save(EmailOverrides.None, Password);
        Assert.True(File.Exists(SettingsFile));

        store.Save(EmailOverrides.None, null);

        Assert.False(File.Exists(SettingsFile));
        store.Save(EmailOverrides.None, null);
        Assert.Equal(StoredEmailSettings.Empty, Store().Load());
    }

    [Fact]
    public void The_file_is_readable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Store().Save(new EmailOverrides(Host: "smtp.draughtsman.test"), Password);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsFile));
    }
}
