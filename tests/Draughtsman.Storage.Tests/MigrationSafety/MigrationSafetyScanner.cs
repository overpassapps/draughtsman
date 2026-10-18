using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Draughtsman.Storage.Tests.MigrationSafety;

/// <summary>One thing a migration's <c>Up</c> does that can lose or strand a customer's data (docs/upgrade-backup.md, "What an upgrade may change").</summary>
internal sealed record MigrationFinding(string MigrationId, string Provider, string Operation, string Target, string Why)
{
    public override string ToString()
    {
        return $"{MigrationId} ({Provider}): {Operation} {Target} - {Why}";
    }
}

/// <summary>
/// Reads a migration the way EF will run it, from its <c>UpOperations</c>, and reports every operation that can lose data
/// or leave an upgrade unable to go back: a dropped table, column or schema, a column narrowed or changed to another type
/// or made NOT NULL, a rename (an older binary, and the pre-migration copy's schema, no longer match), a NOT NULL column
/// added with no default (the migration fails on a populated table), and raw SQL that deletes, truncates or drops.
/// The rule behind it is expand, then contract: a release only adds; what an older release no longer reads is removed in a
/// LATER release, by a migration that carries an allow-list entry naming the fixture that proves the data survives.
/// <c>Down</c> is not scanned: this server never runs it.
/// </summary>
internal static partial class MigrationSafetyScanner
{
    /// <summary>The migrations a provider's context ships, in id order, each with the provider it belongs to.</summary>
    public static IReadOnlyList<(string Id, string Provider, Type Type)> Shipped(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Type: t, Id: t.GetCustomAttribute<MigrationAttribute>()?.Id, Context: t.GetCustomAttribute<DbContextAttribute>()?.ContextType))
            .Where(m => m.Id is not null && m.Context is not null)
            .Select(m => (m.Id!, ProviderOf(m.Context!), m.Type))
            .OrderBy(m => m.Item2, StringComparer.Ordinal)
            .ThenBy(m => m.Item1, StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<MigrationFinding> Scan(string id, string provider, Type migrationType)
    {
        var migration = (Migration)Activator.CreateInstance(migrationType)!;
        var findings = new List<MigrationFinding>();
        foreach (MigrationOperation operation in migration.UpOperations)
        {
            findings.AddRange(Inspect(id, provider, operation));
        }

        return findings;
    }

    private static string ProviderOf(Type context)
    {
        return context.Name.Replace("DraughtsmanDbContext", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private static IEnumerable<MigrationFinding> Inspect(string id, string provider, MigrationOperation operation)
    {
        switch (operation)
        {
            case DropTableOperation drop:
                yield return new MigrationFinding(id, provider, "DropTable", drop.Name, "the table and every row in it are gone");
                break;
            case DropColumnOperation drop:
                yield return new MigrationFinding(id, provider, "DropColumn", $"{drop.Table}.{drop.Name}", "the column's values are gone");
                break;
            case DropSchemaOperation drop:
                yield return new MigrationFinding(id, provider, "DropSchema", drop.Name, "everything in the schema is gone");
                break;
            case RenameTableOperation rename:
                yield return new MigrationFinding(id, provider, "RenameTable", $"{rename.Name} -> {rename.NewName}", "a rename is not a copy: the old name stops existing for the previous release");
                break;
            case RenameColumnOperation rename:
                yield return new MigrationFinding(id, provider, "RenameColumn", $"{rename.Table}.{rename.Name} -> {rename.NewName}", "a rename is not a copy: the old name stops existing for the previous release");
                break;
            case DeleteDataOperation delete:
                yield return new MigrationFinding(id, provider, "DeleteData", delete.Table, "rows are deleted");
                break;
            case AlterColumnOperation alter:
                foreach (string why in Narrowing(alter))
                {
                    yield return new MigrationFinding(id, provider, "AlterColumn", $"{alter.Table}.{alter.Name}", why);
                }

                break;
            case AddColumnOperation { IsNullable: false, DefaultValue: null, DefaultValueSql: null, ComputedColumnSql: null } add:
                yield return new MigrationFinding(id, provider, "AddColumn", $"{add.Table}.{add.Name}", "NOT NULL with no default: the migration fails on a table that already has rows");
                break;
            case SqlOperation sql:
                foreach (string why in DestructiveSql(sql.Sql))
                {
                    yield return new MigrationFinding(id, provider, "Sql", Summarise(sql.Sql), why);
                }

                break;
        }
    }

    /// <summary>The reasons an AlterColumn can lose or reject data; empty for a change that only widens.</summary>
    internal static IEnumerable<string> Narrowing(AlterColumnOperation alter)
    {
        ColumnOperation before = alter.OldColumn;
        if (before.ClrType != alter.ClrType)
        {
            yield return $"the type changes from {before.ClrType.Name} to {alter.ClrType.Name}";
        }
        else if (before.ColumnType is not null && alter.ColumnType is not null && !string.Equals(before.ColumnType, alter.ColumnType, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"the column type changes from {before.ColumnType} to {alter.ColumnType}";
        }

        if (alter.MaxLength is { } now && (before.MaxLength is not { } was || now < was))
        {
            yield return $"the maximum length is cut to {now} (was {before.MaxLength?.ToString() ?? "unlimited"})";
        }

        if (alter.Precision is { } precision && before.Precision is { } oldPrecision && precision < oldPrecision)
        {
            yield return $"the precision is cut to {precision} (was {oldPrecision})";
        }

        if (alter.Scale is { } scale && before.Scale is { } oldScale && scale < oldScale)
        {
            yield return $"the scale is cut to {scale} (was {oldScale})";
        }

        if (before.IsNullable && !alter.IsNullable)
        {
            yield return "a nullable column becomes NOT NULL: rows holding null make the migration fail or be rewritten";
        }

        if (before.IsUnicode != false && alter.IsUnicode == false)
        {
            yield return "the column stops being Unicode";
        }
    }

    internal static IEnumerable<string> DestructiveSql(string sql)
    {
        string text = BlockComment().Replace(LineComment().Replace(sql, string.Empty), string.Empty);
        if (DeleteStatement().IsMatch(text))
        {
            yield return "raw SQL deletes rows";
        }

        if (TruncateStatement().IsMatch(text))
        {
            yield return "raw SQL truncates a table";
        }

        if (DropStatement().IsMatch(text))
        {
            yield return "raw SQL drops a table, column, schema or view";
        }

        if (AlterDropOrRename().IsMatch(text))
        {
            yield return "raw SQL drops or renames a column or table";
        }
    }

    private static string Summarise(string sql)
    {
        string flat = Whitespace().Replace(sql, " ").Trim();
        return flat.Length <= 80 ? flat : flat[..77] + "...";
    }

    [GeneratedRegex(@"--[^\r\n]*", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, matchTimeoutMilliseconds: 5000)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"\bDELETE\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 5000)]
    private static partial Regex DeleteStatement();

    [GeneratedRegex(@"\bTRUNCATE\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 5000)]
    private static partial Regex TruncateStatement();

    [GeneratedRegex(@"\bDROP\s+(TABLE|COLUMN|SCHEMA|VIEW|DATABASE)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 5000)]
    private static partial Regex DropStatement();

    [GeneratedRegex(@"\bALTER\s+TABLE\b[^;]*?\b(DROP|RENAME)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 5000)]
    private static partial Regex AlterDropOrRename();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Whitespace();
}
