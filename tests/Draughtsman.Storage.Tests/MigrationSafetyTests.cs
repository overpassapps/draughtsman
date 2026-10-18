using System.Text.Json;
using Draughtsman.Storage.Tests.MigrationSafety;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// The destructive-migration guard (docs/upgrade-backup.md, "What an upgrade may change"). A migration that can lose a
/// customer's data fails here, for both providers, unless an allow-list entry says why the data is safe and names the
/// fixture whose upgrade test proves it. The first two tests apply the rule to what ships; the others prove the rule can
/// fail, by feeding it a deliberately destructive migration and a purely additive control.
/// </summary>
public sealed class MigrationSafetyTests
{
    private static IReadOnlyList<MigrationFinding> ShippedFindings()
    {
        return MigrationSafetyScanner.Shipped(typeof(StorageMigrator).Assembly)
            .SelectMany(m => MigrationSafetyScanner.Scan(m.Id, m.Provider, m.Type))
            .ToList();
    }

    [Fact]
    public void Both_providers_ship_the_same_migration_ids_in_the_same_order()
    {
        var shipped = MigrationSafetyScanner.Shipped(typeof(StorageMigrator).Assembly);
        List<string> sqlite = shipped.Where(m => m.Provider == "sqlite").Select(m => m.Id[15..]).ToList();
        List<string> postgres = shipped.Where(m => m.Provider == "postgres").Select(m => m.Id[15..]).ToList();

        Assert.NotEmpty(sqlite);
        Assert.Equal(sqlite, postgres);
    }

    [Fact]
    public void Every_shipped_migration_only_adds_unless_an_allow_list_entry_says_otherwise()
    {
        IReadOnlyList<MigrationFinding> uncovered = MigrationSafetyGuard.Uncovered(ShippedFindings(), DestructiveMigrationAllowList.Load());

        Assert.True(
            uncovered.Count == 0,
            "These migration operations can lose or strand customer data. Expand now and contract in a LATER release, or add an entry to "
            + "tests/Draughtsman.Storage.Tests/MigrationSafety/allowed-destructive-migrations.json with a justification and the fixture that proves the data survives:\n  "
            + string.Join("\n  ", uncovered));
    }

    [Fact]
    public void The_allow_list_has_no_stale_unjustified_or_unproven_entry()
    {
        IReadOnlyList<string> problems = MigrationSafetyGuard.Problems(ShippedFindings(), DestructiveMigrationAllowList.Load(), FixtureMigrations);

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void A_table_dropped_on_purpose_is_in_the_allow_list_and_in_the_migrators_own_list_and_nowhere_else()
    {
        // The runtime row-count check treats a vanished table as damage unless the migrator was told it is meant to go (IntentionallyDroppedTables);
        // the allow-list is where that decision is justified and proven. The two must name exactly the same tables.
        string[] allowed = [.. DestructiveMigrationAllowList.Load().Where(a => a.Operation == "DropTable").Select(a => a.Target).Distinct().Order(StringComparer.Ordinal)];

        Assert.Equal(allowed, StorageMigrator.IntentionallyDroppedTables.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_scanner_flags_every_kind_of_destructive_operation_in_a_deliberately_destructive_migration()
    {
        IReadOnlyList<MigrationFinding> findings = MigrationSafetyScanner.Scan(DeliberatelyDestructiveMigration.Id, "sqlite", typeof(DeliberatelyDestructiveMigration));
        string[] operations = [.. findings.Select(f => f.Operation + " " + f.Target)];

        Assert.Contains("DropTable audit", operations);
        Assert.Contains("DropColumn documents.title", operations);
        Assert.Contains("RenameColumn api_tokens.name -> label", operations);
        Assert.Contains("RenameTable assets -> images", operations);
        Assert.Contains(findings, f => f.Operation == "AlterColumn" && f.Target == "users.email" && f.Why.Contains("maximum length is cut to 8", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Operation == "AlterColumn" && f.Target == "users.role" && f.Why.Contains("type changes", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Operation == "AlterColumn" && f.Target == "api_tokens.scope" && f.Why.Contains("NOT NULL", StringComparison.Ordinal));
        Assert.Contains("AddColumn documents.must_have", operations);
        Assert.Contains(findings, f => f.Operation == "Sql" && f.Why.Contains("deletes rows", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Operation == "Sql" && f.Why.Contains("truncates", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Operation == "Sql" && f.Why.Contains("drops or renames a column", StringComparison.Ordinal));
        Assert.Contains("DeleteData users", operations);
    }

    [Fact]
    public void The_scanner_passes_a_purely_additive_migration_including_a_widened_column_and_a_commented_delete()
    {
        IReadOnlyList<MigrationFinding> findings = MigrationSafetyScanner.Scan(PurelyAdditiveMigration.Id, "sqlite", typeof(PurelyAdditiveMigration));

        Assert.Empty(findings);
    }

    /// <summary>
    /// "Prove the guard can fail": the same evaluation the shipped-migration test runs, over the shipped set PLUS a destructive
    /// migration, fails and names the culprit; the identical set with the allow-list entries a person would have to write passes.
    /// </summary>
    [Fact]
    public void Adding_a_destructive_migration_to_the_shipped_set_fails_the_guard_until_it_is_allowed_with_proof()
    {
        IReadOnlyList<MigrationFinding> shipped = ShippedFindings();
        IReadOnlyList<MigrationFinding> destructive = MigrationSafetyScanner.Scan(DeliberatelyDestructiveMigration.Id, "sqlite", typeof(DeliberatelyDestructiveMigration));
        List<MigrationFinding> all = [.. shipped, .. destructive];

        IReadOnlyList<MigrationFinding> uncovered = MigrationSafetyGuard.Uncovered(all, DestructiveMigrationAllowList.Load());
        Assert.NotEmpty(uncovered);
        Assert.All(uncovered, f => Assert.Equal(DeliberatelyDestructiveMigration.Id, f.MigrationId));
        Assert.Equal(destructive.Count, uncovered.Count);

        // Allowing each operation, with the justification and a fixture that predates the migration, is what makes it pass.
        string fixture = AnyFixtureLabel();
        List<DestructiveMigrationAllowance> allowances = [.. destructive.Select(f => new DestructiveMigrationAllowance(
            f.MigrationId,
            "*",
            f.Operation,
            f.Target,
            "A deliberately destructive synthetic migration, used only to prove that the guard accepts an operation once it is justified and proven.",
            fixture))];
        Assert.Empty(MigrationSafetyGuard.Uncovered(all, allowances));
        Assert.Empty(MigrationSafetyGuard.Problems(destructive, allowances, FixtureMigrations));
    }

    [Fact]
    public void An_allow_list_entry_that_is_stale_short_or_names_a_missing_or_later_fixture_is_a_problem()
    {
        IReadOnlyList<MigrationFinding> destructive = MigrationSafetyScanner.Scan(DeliberatelyDestructiveMigration.Id, "sqlite", typeof(DeliberatelyDestructiveMigration));
        string fixture = AnyFixtureLabel();
        string longEnough = new('x', DestructiveMigrationAllowList.MinimumJustificationLength);
        var good = new DestructiveMigrationAllowance(DeliberatelyDestructiveMigration.Id, "sqlite", "DropTable", "audit", longEnough, fixture);

        Assert.Empty(MigrationSafetyGuard.Problems(destructive, [good], FixtureMigrations));
        Assert.Contains("covers no operation", Assert.Single(MigrationSafetyGuard.Problems(destructive, [good with { Target = "no_such_table" }], FixtureMigrations)));
        Assert.Contains("shorter than", Assert.Single(MigrationSafetyGuard.Problems(destructive, [good with { Justification = "short" }], FixtureMigrations)));
        Assert.Contains("names no upgrade fixture that exists", Assert.Single(MigrationSafetyGuard.Problems(destructive, [good with { Fixture = "no-such-fixture" }], FixtureMigrations)));
        Assert.Contains("names no upgrade fixture that exists", Assert.Single(MigrationSafetyGuard.Problems(destructive, [good with { Fixture = string.Empty }], FixtureMigrations)));

        // A fixture that already holds the migration never runs it on upgrade, so it proves nothing.
        IReadOnlyList<string> Later(string _) => [DeliberatelyDestructiveMigration.Id];
        Assert.Contains("proves nothing", Assert.Single(MigrationSafetyGuard.Problems(destructive, [good], Later)));
    }

    [Theory]
    [InlineData("DELETE FROM x", true)]
    [InlineData("delete\nfrom x", true)]
    [InlineData("TRUNCATE TABLE x", true)]
    [InlineData("DROP TABLE x", true)]
    [InlineData("ALTER TABLE x DROP COLUMN y", true)]
    [InlineData("ALTER TABLE x RENAME TO y", true)]
    [InlineData("UPDATE x SET y = 1 WHERE y IS NULL", false)]
    [InlineData("INSERT INTO x (a) VALUES ('DELETED')", false)]
    [InlineData("CREATE INDEX ix ON x (y) -- DROP TABLE x", false)]
    [InlineData("/* DELETE FROM x */ UPDATE x SET y = 2", false)]
    public void Raw_sql_is_judged_by_what_it_does_not_by_a_comment_or_a_substring(string sql, bool destructive)
    {
        Assert.Equal(destructive, MigrationSafetyScanner.DestructiveSql(sql).Any());
    }

    private static IReadOnlyList<string>? FixtureMigrations(string label)
    {
        string manifest = Path.Combine(UpgradeFixturePaths.Root, label, "manifest.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
        return [.. document.RootElement.GetProperty("build").GetProperty("migrations").EnumerateArray().Select(e => e.GetString()!)];
    }

    private static string AnyFixtureLabel()
    {
        return Directory.EnumerateDirectories(UpgradeFixturePaths.Root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).First()!;
    }
}
