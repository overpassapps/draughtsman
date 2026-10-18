using System.Text.Json;
using Draughtsman.Storage;
using Npgsql;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// The Postgres leg of the upgrade rehearsal: each fixture's plain-SQL dump (written by an earlier build running on a throwaway Postgres
/// container) is loaded into a fresh database on <c>DRAUGHTSMAN_TEST_POSTGRES</c> (CI's service container, or the <c>docker run</c> in
/// docs/api.md), the current server is started on it, and the same checks as the SQLite leg run. Reported Skipped where no database is
/// reachable, like the rest of the Postgres provider suite. Postgres has no automatic rollback; what these prove is that the migrations apply to
/// real populated data and lose none of it.
/// </summary>
public sealed class UpgradeFixturePostgresTests
{
    public const string ConnectionVariable = "DRAUGHTSMAN_TEST_POSTGRES";

    public static TheoryData<string> Labels => UpgradeFixture.Labels(UpgradeFixture.Postgres);

    [RequiresPostgresTheory]
    [MemberData(nameof(Labels))]
    public async Task The_current_server_migrates_an_earlier_builds_postgres_database_and_every_part_of_it_is_intact(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        string admin = Environment.GetEnvironmentVariable(ConnectionVariable)!;
        string database = "upgrade_" + Guid.NewGuid().ToString("N");
        string connection = new NpgsqlConnectionStringBuilder(admin) { Database = database }.ToString();
        try
        {
            await Execute(admin, $"CREATE DATABASE {database}");
            await Execute(connection, fixture.PostgresDump());
            Dictionary<string, long> before = await RowCounts(connection);

            string data = fixture.ExtractDataFolder(UpgradeFixture.Postgres, temp);
            string yamlPath = Path.Combine(data, "draughtsman.yaml");
            string yaml = await File.ReadAllTextAsync(yamlPath);
            Assert.Contains("__TEST_POSTGRES__", yaml, StringComparison.Ordinal);
            await File.WriteAllTextAsync(yamlPath, yaml.Replace("__TEST_POSTGRES__", connection.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal), StringComparison.Ordinal));

            Func<string, JsonElement?> snapshots = await UpgradeData.PostgresSnapshots(connection);
            using JsonDocument expectedDocument = fixture.Expected(UpgradeFixture.Postgres);
            var checks = new UpgradeAssertions(fixture, expectedDocument.RootElement, snapshots);

            // The build that wrote this data bound its secrets to the folder IT ran from, which is never the folder a test host runs from: the operator's
            // one-time act of naming it (keyRing.legacyInstallPaths) is part of the upgrade here, and the secrets must then read from this other folder.
            await using var factory = new ExistingFolderServerFactory(data, fixture.KeyRingProtection, fixture.KeyEncryptionKey, legacyInstallPaths: fixture.ContentRoot);
            HttpClient? client = await checks.SignIn(factory, checks.User("admin"));
            Assert.True(client is not null, "the administrator could not sign in:\n" + string.Join("\n", checks.Problems));

            await checks.Users(factory, client);
            await checks.Tokens(factory, client);
            await checks.Documents(client);
            await checks.Bin(client);
            await checks.Assets(client);
            await checks.Branding(client);
            await checks.Themes(client);
            await checks.Ai(client);
            await checks.Email(client);
            await checks.OldLicenceFileIsIgnored(client, data);
            await checks.SecurityEvents(client);
            await checks.Audit(client);

            Dictionary<string, long> after = await RowCounts(connection);
            foreach ((string table, long rows) in before)
            {
                checks.Check($"table {table} does not lose rows", after.TryGetValue(table, out long now) && now >= rows, $"{rows} -> {(after.TryGetValue(table, out long n) ? n : -1)}");
            }

            checks.Check("every migration of this build is applied", await Scalar(connection, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"") >= fixture.Migrations.Count);
            await checks.RestoreFromBinAndWrite(client);

            Assert.True(checks.Problems.Count == 0, $"{label}: {checks.Problems.Count} of {checks.Checks} upgrade checks failed:\n  " + string.Join("\n  ", checks.Problems));
            Assert.True(checks.Checks > 40, $"{label}: only {checks.Checks} checks ran");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            try
            {
                await Execute(admin, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
            }
            catch (NpgsqlException)
            {
                // Best effort: a throwaway database left behind is harmless.
            }

            UpgradeData.Delete(temp);
        }
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<Dictionary<string, long>> RowCounts(string connectionString)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var tables = new List<string>();
        await using (NpgsqlCommand list = connection.CreateCommand())
        {
            list.CommandText = "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename NOT LIKE '\\_\\_EF%' ORDER BY tablename";
            await using NpgsqlDataReader reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (string table in tables)
        {
            await using NpgsqlCommand count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            counts[table] = Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        return counts;
    }

    /// <summary>A theory that is reported Skipped, not Passed or Failed, where DRAUGHTSMAN_TEST_POSTGRES names no database (the pattern of <c>PostgresStoreTests</c>, natively in xunit).</summary>
    private sealed class RequiresPostgresTheoryAttribute : TheoryAttribute
    {
        public RequiresPostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            {
                Skip = $"Postgres unavailable: {ConnectionVariable} is not set (docs/api.md has the docker run for a local one).";
            }
        }
    }
}
