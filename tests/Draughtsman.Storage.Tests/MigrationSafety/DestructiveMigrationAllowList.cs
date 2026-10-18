using System.Text.Json;

namespace Draughtsman.Storage.Tests.MigrationSafety;

/// <summary>
/// One deliberate exception to "a migration only adds" (docs/upgrade-backup.md, "What an upgrade may change"). An entry names
/// the exact operation, says in words why the data it touches is not lost, and names the upgrade fixture (a directory of
/// <c>tests/upgrade-fixtures</c>, written by a release that PRECEDES this migration) whose upgrade test proves it.
/// </summary>
internal sealed record DestructiveMigrationAllowance(string Migration, string Provider, string Operation, string Target, string Justification, string Fixture);

internal static class DestructiveMigrationAllowList
{
    public const int MinimumJustificationLength = 60;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "MigrationSafety", "allowed-destructive-migrations.json");

    public static IReadOnlyList<DestructiveMigrationAllowance> Load()
    {
        using FileStream file = File.OpenRead(FilePath);
        return JsonSerializer.Deserialize<List<DestructiveMigrationAllowance>>(file, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidOperationException($"{FilePath} is empty");
    }

    /// <summary>Whether <paramref name="allowance"/> covers <paramref name="finding"/>: the same migration, provider ("*" for both), operation and target.</summary>
    public static bool Covers(DestructiveMigrationAllowance allowance, MigrationFinding finding)
    {
        return string.Equals(allowance.Migration, finding.MigrationId, StringComparison.Ordinal)
            && (allowance.Provider == "*" || string.Equals(allowance.Provider, finding.Provider, StringComparison.OrdinalIgnoreCase))
            && string.Equals(allowance.Operation, finding.Operation, StringComparison.Ordinal)
            && string.Equals(allowance.Target, finding.Target, StringComparison.Ordinal);
    }
}
