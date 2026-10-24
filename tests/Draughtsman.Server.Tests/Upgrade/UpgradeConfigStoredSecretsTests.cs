using System.Security.Cryptography;
using Draughtsman.Server.Cli;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// <c>draughtsman config check</c> on a data folder an earlier build wrote says, before an upgrade, whether the stored AI API key and the saved SMTP
/// password can be read, what starting this version does about one that is tied to an install folder, and (for one nothing can read) which secret,
/// why and what to do. It writes nothing: the data folder is the same bytes after.
/// </summary>
public sealed class UpgradeConfigStoredSecretsTests
{
    public static TheoryData<string> Labels => UpgradeFixture.Labels(UpgradeFixture.Sqlite);

    /// <summary>The fixtures whose build bound its secrets to its install path, the case a secret can be unreadable after a move.</summary>
    public static TheoryData<string> PathBoundLabels => UpgradeFixture.PathBoundLabels(UpgradeFixture.Sqlite);

    private static Dictionary<string, string> Files(string folder)
    {
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(folder, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
    }

    /// <summary>Points draughtsman.yaml at the fixture's key-encryption key through a key file outside the data folder (a shell's environment is process-wide, and a check must be told the same way a service is), and at the old folders named.</summary>
    private static void Configure(UpgradeFixture fixture, string data, string temp, params string[] legacyInstallPaths)
    {
        string yaml = File.ReadAllText(Path.Combine(data, "draughtsman.yaml"));
        yaml += "keyRing:\n";
        if (fixture.KeyRingProtection == "key")
        {
            string keyFile = Path.Combine(temp, "kek.txt");
            File.WriteAllText(keyFile, fixture.KeyEncryptionKey!);
            yaml += $"  protection: key\n  keyFile: \"{keyFile.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n";
        }
        else
        {
            yaml += "  protection: none\n";
        }

        if (legacyInstallPaths.Length > 0)
        {
            yaml += "  legacyInstallPaths:\n" + string.Concat(legacyInstallPaths.Select(p => $"    - \"{p.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n"));
        }

        File.WriteAllText(Path.Combine(data, "draughtsman.yaml"), yaml);
    }

    [Theory]
    [MemberData(nameof(PathBoundLabels))]
    public async Task The_check_names_a_secret_it_cannot_read_why_and_what_to_do_and_writes_nothing(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            Configure(fixture, data, temp);
            Dictionary<string, string> before = Files(data);

            CheckReport report = await ConfigCheckCommand.CheckAsync(data);

            Assert.Contains("AI API key: unreadable", report.Secrets);
            CheckLine ai = Assert.Single(report.Lines, l => l.Section == "Stored secrets" && l.Message.Contains("AI API key", StringComparison.Ordinal));
            Assert.Equal(CheckLevel.Warn, ai.Level);
            Assert.Contains("keyRing.legacyInstallPaths", ai.Message, StringComparison.Ordinal);
            Assert.Contains("Admin > AI", ai.Message, StringComparison.Ordinal);
            // A warning is not a refusal: the server would start (and report the same on the admin page).
            Assert.True(report.WouldStart, string.Join('\n', report.Lines.Select(l => $"{l.Level} {l.Message}")));
            Assert.Equal(before, Files(data));
            Assert.Contains("Stored secrets", report.ToText(), StringComparison.Ordinal);
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(PathBoundLabels))]
    public async Task With_the_old_folder_named_the_check_says_the_secret_is_readable_and_will_be_re_protected_at_start(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            bool hasEmail = File.Exists(Path.Combine(data, "email", "settings.json"));
            Configure(fixture, data, temp, fixture.ContentRoot);
            Dictionary<string, string> before = Files(data);

            CheckReport report = await ConfigCheckCommand.CheckAsync(data);

            Assert.Contains("AI API key: legacy", report.Secrets);
            Assert.Equal(hasEmail, report.Secrets.Contains("SMTP password: legacy"));
            CheckLine ai = Assert.Single(report.Lines, l => l.Section == "Stored secrets" && l.Message.Contains("AI API key", StringComparison.Ordinal));
            Assert.Equal(CheckLevel.Info, ai.Level);
            Assert.Contains("re-protects", ai.Message, StringComparison.Ordinal);
            Assert.Contains("backups/secrets-", ai.Message, StringComparison.Ordinal);
            Assert.Equal(before, Files(data));
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public async Task After_the_start_that_re_protects_them_the_check_needs_no_folder_named_at_all(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            Configure(fixture, data, temp);
            bool kek = fixture.KeyRingProtection == "key";
            var config = new KeyRingConfig { Protection = kek ? "key" : "none" };
            Func<string, string?> environment = name => kek && name == "DRAUGHTSMAN_KEY_ENCRYPTION_KEY" ? fixture.KeyEncryptionKey : null;
            using (StoredSecretsRig.Start(data, fixture.ContentRoot, config, environment))
            {
            }

            CheckReport report = await ConfigCheckCommand.CheckAsync(data);

            Assert.Contains("AI API key: current", report.Secrets);
            Assert.DoesNotContain(report.Secrets, s => s.EndsWith("unreadable", StringComparison.Ordinal) || s.EndsWith("legacy", StringComparison.Ordinal));
            Assert.All(report.Lines.Where(l => l.Section == "Stored secrets"), l => Assert.Equal(CheckLevel.Ok, l.Level));
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Fact]
    public async Task A_data_folder_with_no_stored_secret_says_so()
    {
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = Path.Combine(temp, "data");
            Directory.CreateDirectory(data);

            CheckReport report = await ConfigCheckCommand.CheckAsync(data);

            Assert.Contains(report.Lines, l => l.Section == "Stored secrets" && l.Message.Contains("No stored AI API key", StringComparison.Ordinal));
            Assert.Empty(report.Secrets);
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }
}
