namespace Draughtsman.Storage.Tests.MigrationSafety;

/// <summary>The rule itself, separate from the tests that apply it, so one test can feed it a deliberately destructive migration.</summary>
internal static class MigrationSafetyGuard
{
    /// <summary>Findings no allow-list entry covers: what the guard fails on.</summary>
    public static IReadOnlyList<MigrationFinding> Uncovered(IEnumerable<MigrationFinding> findings, IReadOnlyList<DestructiveMigrationAllowance> allowList)
    {
        return findings.Where(f => !allowList.Any(a => DestructiveMigrationAllowList.Covers(a, f))).ToList();
    }

    /// <summary>Problems with the allow-list itself: an entry that covers nothing, says too little, or names no proof.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<MigrationFinding> findings, IReadOnlyList<DestructiveMigrationAllowance> allowList, Func<string, IReadOnlyList<string>?> migrationsOfFixture)
    {
        var problems = new List<string>();
        foreach (DestructiveMigrationAllowance entry in allowList)
        {
            string name = $"{entry.Migration} {entry.Operation} {entry.Target}";
            if (!findings.Any(f => DestructiveMigrationAllowList.Covers(entry, f)))
            {
                problems.Add($"{name}: the allow-list entry covers no operation (stale or mistyped); remove it");
            }

            if (entry.Justification.Trim().Length < DestructiveMigrationAllowList.MinimumJustificationLength)
            {
                problems.Add($"{name}: the justification is shorter than {DestructiveMigrationAllowList.MinimumJustificationLength} characters");
            }

            IReadOnlyList<string>? before = string.IsNullOrWhiteSpace(entry.Fixture) ? null : migrationsOfFixture(entry.Fixture);
            if (before is null)
            {
                problems.Add($"{name}: it names no upgrade fixture that exists (tests/upgrade-fixtures/<label>/manifest.json)");
            }
            else if (before.Contains(entry.Migration, StringComparer.Ordinal))
            {
                problems.Add($"{name}: fixture '{entry.Fixture}' already contains this migration, so upgrading from it never runs it and proves nothing");
            }
        }

        return problems;
    }
}
