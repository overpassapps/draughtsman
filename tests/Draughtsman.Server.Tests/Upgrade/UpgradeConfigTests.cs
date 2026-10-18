using System.Security.Cryptography;
using System.Text.Json;
using Draughtsman.Server.Cli;
using Draughtsman.Server.Configuration;
using Draughtsman.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Core;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// Item 5 of the upgrade-safety work: an old <c>draughtsman.yaml</c> keeps working (a renamed key is carried over, a removed or
/// unknown key is a warning that names the fix, none is ever fatal) and <c>draughtsman config check</c> reports what an upgrade
/// would do without starting the server or changing the data folder.
/// </summary>
public sealed class UpgradeConfigTests
{
    public static TheoryData<string> Labels => UpgradeFixture.Labels(UpgradeFixture.Sqlite);

    // ---------------------------------------------------------------------------------------------------------------- settings

    [Theory]
    [MemberData(nameof(Labels))]
    public void The_draughtsman_yaml_an_earlier_build_wrote_loads_without_a_warning(string label)
    {
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = UpgradeFixture.Get(label).ExtractDataFolder(UpgradeFixture.Sqlite, temp);

            (DraughtsmanConfig config, IReadOnlyList<ConfigWarning> warnings) = ConfigLoader.LoadWithWarnings(data);

            Assert.Empty(warnings);
            Assert.Equal(3, config.Retention.PreMigrationCopies);
            Assert.Equal("sqlite", config.Storage.Provider);
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Fact]
    public void An_unknown_key_is_a_warning_that_names_it_and_a_likely_fix_and_the_rest_of_the_file_still_applies()
    {
        const string yaml = """
            server:
              port: 8123
              prot: 9
            retention:
              preMigrationCopys: 7
            somethingNew:
              enabled: true
            """;

        (DraughtsmanConfig config, IReadOnlyList<ConfigWarning> warnings) = ConfigLoader.ParseWithWarnings(yaml);

        Assert.Equal(8123, config.Server.Port);
        Assert.Equal(3, config.Retention.PreMigrationCopies);
        Assert.Equal(["retention.preMigrationCopys", "server.prot", "somethingNew"], warnings.Select(w => w.Key).Order(StringComparer.Ordinal));
        Assert.All(warnings, w => Assert.Equal(ConfigWarningKind.Unknown, w.Kind));
        Assert.Contains("did you mean 'server.port'", warnings.Single(w => w.Key == "server.prot").Message, StringComparison.Ordinal);
        Assert.Contains("did you mean 'retention.preMigrationCopies'", warnings.Single(w => w.Key == "retention.preMigrationCopys").Message, StringComparison.Ordinal);
        Assert.DoesNotContain("did you mean", warnings.Single(w => w.Key == "somethingNew").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_in_the_wrong_case_is_reported_because_it_is_in_fact_ignored()
    {
        // YamlDotNet matches names exactly, so "Server:" is not read; the warning must agree with what really happens.
        (DraughtsmanConfig config, IReadOnlyList<ConfigWarning> warnings) = ConfigLoader.ParseWithWarnings("Server:\n  port: 8124\n");

        Assert.Equal(5180, config.Server.Port);
        Assert.Equal("Server", Assert.Single(warnings).Key);
        Assert.Contains("did you mean 'server'", warnings[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_renamed_key_is_read_under_its_new_name_with_a_warning_naming_the_replacement()
    {
        // No key has been renamed yet (ConfigCompatibility.Renames is empty); the rule is exercised with a synthetic one so it is proven before it is needed.
        ConfigRename[] renames = [new ConfigRename("storage.connection", "storage.connectionString", "9.9.9"), new ConfigRename("oldSection.max", "limits.maxDocumentBytes", "9.9.9")];
        const string yaml = "storage:\n  provider: postgres\n  connection: \"Host=old\"\noldSection:\n  max: 1234\n";

        (DraughtsmanConfig config, IReadOnlyList<ConfigWarning> warnings) = ConfigLoader.ParseWithWarnings(yaml, renames);

        Assert.Equal("Host=old", config.Storage.ConnectionString);
        Assert.Equal("postgres", config.Storage.Provider);
        Assert.Equal(1234, config.Limits.MaxDocumentBytes);
        Assert.Equal(2, warnings.Count(w => w.Kind == ConfigWarningKind.Renamed));
        Assert.Contains("'storage.connection' was renamed 'storage.connectionString' in 9.9.9", warnings.First(w => w.Key == "storage.connection").Message, StringComparison.Ordinal);
        Assert.DoesNotContain(warnings, w => w.Kind == ConfigWarningKind.Unknown);
    }

    [Fact]
    public void When_both_the_old_and_the_new_name_are_set_the_new_one_wins_and_the_old_is_flagged()
    {
        ConfigRename[] renames = [new ConfigRename("storage.connection", "storage.connectionString", "9.9.9")];

        (DraughtsmanConfig config, IReadOnlyList<ConfigWarning> warnings) = ConfigLoader.ParseWithWarnings("storage:\n  connection: old\n  connectionString: new\n", renames);

        Assert.Equal("new", config.Storage.ConnectionString);
        Assert.Contains("both are set", Assert.Single(warnings).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_removed_key_is_ignored_with_a_warning_that_says_what_to_do()
    {
        ConfigRemoval[] removals = [new ConfigRemoval("export.legacyPdf", "9.9.9", "Delete it; PDF export is controlled by export.chromium.enabled.")];

        (DraughtsmanConfig config, IReadOnlyList<ConfigWarning> warnings) = ConfigLoader.ParseWithWarnings("export:\n  legacyPdf: true\n  chromium:\n    enabled: true\n", renames: null, removals);

        Assert.True(config.Export.Chromium.Enabled);
        ConfigWarning warning = Assert.Single(warnings);
        Assert.Equal(ConfigWarningKind.Removed, warning.Kind);
        Assert.Contains("was removed in 9.9.9", warning.Message, StringComparison.Ordinal);
        Assert.Contains("export.chromium.enabled", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_licence_setting_an_earlier_build_read_is_ignored_with_a_warning_and_the_server_config_loads()
    {
        (DraughtsmanConfig config, IReadOnlyList<ConfigWarning> warnings) = ConfigLoader.ParseWithWarnings("licence:\n  path: my-licence.key\nretention:\n  binDays: 7\n");

        Assert.Equal(7, config.Retention.BinDays);
        ConfigWarning warning = Assert.Single(warnings);
        Assert.Equal("licence.path", warning.Key);
        Assert.Equal(ConfigWarningKind.Removed, warning.Kind);
    }

    [Fact]
    public async Task Config_check_on_a_folder_with_the_old_licence_setting_and_file_would_start_and_says_nothing_about_a_licence_state()
    {
        string temp = UpgradeData.NewTempFolder();
        try
        {
            File.WriteAllText(Path.Combine(temp, "draughtsman.yaml"), "licence:\n  path: custom.licence\n");
            File.WriteAllText(Path.Combine(temp, "custom.licence"), "{ \"format\": \"draughtsman-licence/1\" }");

            (int exit, string text, _) = await Check(temp);

            Assert.Equal(0, exit);
            Assert.Contains("'licence.path' was removed", text, StringComparison.Ordinal);
            Assert.DoesNotContain("evaluation", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unlicensed", text, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(temp, "custom.licence")));
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Fact]
    public void The_compatibility_tables_never_contradict_the_settings_this_build_reads()
    {
        // A renamed key's new name must be a real setting and its old name must not be; a removed key must not be one. Both tables are empty today.
        foreach (ConfigRename rename in ConfigCompatibility.Renames)
        {
            Assert.True(IsKnown(rename.New), $"'{rename.New}' is the target of a rename but is not a setting");
            Assert.False(IsKnown(rename.Old), $"'{rename.Old}' is renamed but is still a setting");
        }

        foreach (ConfigRemoval removal in ConfigCompatibility.Removals)
        {
            Assert.False(IsKnown(removal.Key), $"'{removal.Key}' is listed as removed but is still a setting");
        }

        // And the checker is not vacuous: a real setting is known, a made-up one is not.
        Assert.True(IsKnown("storage.allowMigrationWithoutBackup"));
        Assert.False(IsKnown("storage.noSuchThing"));
    }

    private static bool IsKnown(string dottedPath)
    {
        (_, IReadOnlyList<ConfigWarning> warnings) = ConfigCompatibility.Apply(ToYaml(dottedPath), [], []);
        return warnings.Count == 0;
    }

    private static string ToYaml(string dottedPath)
    {
        string[] parts = dottedPath.Split('.');
        string yaml = string.Empty;
        for (int i = 0; i < parts.Length; i++)
        {
            yaml += new string(' ', i * 2) + parts[i] + (i == parts.Length - 1 ? ": 1\n" : ":\n");
        }

        return yaml;
    }

    [Fact]
    public void Text_that_is_not_yaml_is_still_an_error_with_a_line_number_not_a_silent_default()
    {
        Assert.ThrowsAny<YamlException>(() => ConfigLoader.ParseWithWarnings("server: [unterminated\n"));
    }

    // ---------------------------------------------------------------------------------------------------------------- config check

    private static async Task<(int Exit, string Out, string Err)> Check(string data, params string[] extra)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        CliOutcome outcome = await ServerCli.ExecuteAsync(["config", "check", .. extra, $"--DRAUGHTSMAN_DATA={data}"], stdout, stderr);
        Assert.False(outcome.Serve);
        return (outcome.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static Dictionary<string, string> Snapshot(string folder)
    {
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(folder, f),
            f =>
            {
                using FileStream stream = File.OpenRead(f);
                return Convert.ToHexString(SHA256.HashData(stream));
            });
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public async Task Config_check_reports_the_pending_migrations_of_each_fixture_and_changes_nothing(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            Dictionary<string, string> before = Snapshot(data);
            int known = await KnownMigrations(data);

            CheckReport report = await ConfigCheckCommand.CheckAsync(data);
            (int exit, string text, string error) = await Check(data);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, error);
            Assert.True(report.WouldStart);
            Assert.Equal(known - fixture.Migrations.Count, report.Database!.Pending.Count);
            Assert.Equal(fixture.Migrations.Count, report.Database.Applied);
            Assert.Empty(report.Database.Newer);
            Assert.Contains("would start on this data folder", text, StringComparison.Ordinal);
            Assert.DoesNotContain("icence", text, StringComparison.Ordinal);
            Assert.Contains("passes SQLite's integrity_check", text, StringComparison.Ordinal);
            if (report.Database.Pending.Count == 0)
            {
                Assert.Contains("The schema is current", text, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains($"{report.Database.Pending.Count} of {known} migrations are pending", text, StringComparison.Ordinal);
                Assert.Contains("pre-migration-<time>", text, StringComparison.Ordinal);
            }

            // Not a byte of the data folder changed, and no file was left behind (not even a write-ahead log or the writability probe).
            Assert.Equal(before, Snapshot(data));
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    private static async Task<int> KnownMigrations(string data)
    {
        await using DraughtsmanDbContext db = StorageSetup.CreateContext(new StorageOptions(StorageProvider.Sqlite, StorageOptions.DefaultSqliteConnectionString(data)));
        return db.Database.GetMigrations().Count();
    }

    [Fact]
    public async Task Config_check_refuses_a_database_a_newer_build_migrated_and_exits_with_the_servers_own_code()
    {
        UpgradeFixture fixture = UpgradeFixture.All.Last();
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(data, "data.db")};Pooling=False"))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29990101000000_FromTheFuture', '99.0.0')";
                command.ExecuteNonQuery();
            }

            Dictionary<string, string> before = Snapshot(data);

            (int exit, string text, _) = await Check(data);

            Assert.Equal(Draughtsman.Server.Maintenance.StorageStartup.SchemaNewerExitCode, exit);
            Assert.Contains("REFUSE", text, StringComparison.Ordinal);
            Assert.Contains("29990101000000_FromTheFuture", text, StringComparison.Ordinal);
            Assert.Contains("would NOT start", text, StringComparison.Ordinal);
            Assert.Equal(before, Snapshot(data));
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Fact]
    public async Task Config_check_refuses_a_yaml_that_cannot_be_read_and_an_unknown_provider_and_names_what_to_fix()
    {
        string folder = UpgradeData.NewTempFolder();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "draughtsman.yaml"), "server: [unterminated\n");
            (int yamlExit, string yamlText, _) = await Check(folder);
            Assert.Equal(1, yamlExit);
            Assert.Contains("draughtsman.yaml cannot be read (line", yamlText, StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(folder, "draughtsman.yaml"), "storage:\n  provider: oracle\n");
            (int providerExit, string providerText, _) = await Check(folder);
            Assert.Equal(1, providerExit);
            Assert.Contains("Unknown storage.provider 'oracle'", providerText, StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(folder, "draughtsman.yaml"), "storage:\n  provider: postgres\n  connectionString: \"Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2\"\n");
            (int pgExit, string pgText, _) = await Check(folder);
            Assert.Equal(1, pgExit);
            Assert.Contains("The Postgres database could not be reached", pgText, StringComparison.Ordinal);
        }
        finally
        {
            UpgradeData.Delete(folder);
        }
    }

    [Fact]
    public async Task Config_check_warns_about_unknown_settings_without_refusing_and_lists_them_in_json()
    {
        string folder = UpgradeData.NewTempFolder();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "draughtsman.yaml"), "server:\n  prot: 1\n");

            (int exit, string json, _) = await Check(folder, "--json");

            Assert.Equal(0, exit);
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.True(document.RootElement.GetProperty("wouldStart").GetBoolean());
            Assert.Contains(document.RootElement.GetProperty("lines").EnumerateArray(), l => l.GetProperty("level").GetString() == "warn" && l.GetProperty("message").GetString()!.Contains("'server.prot' is not a setting", StringComparison.Ordinal));
        }
        finally
        {
            UpgradeData.Delete(folder);
        }
    }

    [Fact]
    public async Task Config_check_on_a_folder_that_does_not_exist_warns_and_a_bad_command_line_is_a_usage_error()
    {
        string missing = Path.Combine(Path.GetTempPath(), "draughtsman-no-such-" + Guid.NewGuid().ToString("N"));

        (int exit, string text, _) = await Check(missing);
        Assert.Equal(0, exit);
        Assert.Contains("does not exist", text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing), "a check must not create the folder it inspects");

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.Equal(2, (await ServerCli.ExecuteAsync(["config"], stdout, stderr)).ExitCode);
        Assert.Contains("config check", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, (await ServerCli.ExecuteAsync(["config", "check", "--nonsense"], stdout, stderr)).ExitCode);
        Assert.Equal(2, (await ServerCli.ExecuteAsync(["config", "frobnicate"], stdout, stderr)).ExitCode);
    }
}
