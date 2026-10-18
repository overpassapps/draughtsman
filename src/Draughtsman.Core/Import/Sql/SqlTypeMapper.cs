using System.Text.RegularExpressions;

namespace Draughtsman.Core.Import.Sql;

/// <summary>
/// Rewrites the data type of an entity column for the dialect the DDL is exported to. A diagram remembers a type as text
/// only, and that text came from whichever dialect was imported, so the common cross-dialect names are translated
/// (<c>nvarchar(max)</c> to <c>text</c>, <c>bit</c> to <c>boolean</c>, <c>uniqueidentifier</c> to <c>uuid</c>, ...) and
/// anything else is left exactly as written. A type that already suits the target is never touched, which is what keeps a
/// same-dialect import, export and import stable.
/// </summary>
internal static partial class SqlTypeMapper
{
    // Source type name -> (T-SQL, PostgreSQL, MySQL) spellings; null keeps the source text. {a} is the source's argument list,
    // for the few spellings that carry it across. SQLite takes any type name, so only what it cannot parse is rewritten.
    private static readonly Dictionary<string, (string? TSql, string? Postgres, string? MySql)> s_map = new(StringComparer.Ordinal)
    {
        ["nvarchar"] = (null, "varchar{a}", "varchar{a}"),
        ["nchar"] = (null, "char{a}", "char{a}"),
        ["character varying"] = ("varchar{a}", null, "varchar{a}"),
        ["character"] = ("char{a}", null, "char{a}"),
        ["national character varying"] = ("nvarchar{a}", "varchar{a}", "varchar{a}"),
        ["datetime2"] = (null, "timestamp", "datetime(6)"),
        ["datetime"] = (null, "timestamp", null),
        ["smalldatetime"] = (null, "timestamp", "datetime"),
        ["datetimeoffset"] = (null, "timestamptz", "datetime(6)"),
        ["timestamptz"] = ("datetimeoffset", null, "timestamp"),
        ["timestamp with time zone"] = ("datetimeoffset", null, "timestamp"),
        ["timestamp without time zone"] = ("datetime2", null, "datetime"),
        ["bit"] = (null, "boolean", "tinyint(1)"),
        ["boolean"] = ("bit", null, null),
        ["bool"] = ("bit", "boolean", "boolean"),
        ["uniqueidentifier"] = (null, "uuid", "char(36)"),
        ["uuid"] = ("uniqueidentifier", null, "char(36)"),
        ["money"] = (null, "numeric(19, 4)", "decimal(19, 4)"),
        ["smallmoney"] = (null, "numeric(10, 4)", "decimal(10, 4)"),
        ["tinyint"] = (null, "smallint", null),
        ["float"] = (null, "double precision", "double"),
        ["double precision"] = ("float", null, "double"),
        ["double"] = ("float", "double precision", null),
        ["real"] = (null, null, "float"),
        ["text"] = ("nvarchar(max)", null, null),
        ["ntext"] = (null, "text", "text"),
        ["xml"] = (null, null, "text"),
        ["json"] = ("nvarchar(max)", null, null),
        ["jsonb"] = ("nvarchar(max)", null, "json"),
        ["rowversion"] = (null, "bytea", "binary(8)"),
        ["image"] = (null, "bytea", "longblob"),
        ["binary"] = (null, "bytea", null),
        ["bytea"] = ("varbinary(max)", null, "longblob"),
        ["blob"] = ("varbinary(max)", "bytea", null),
        ["serial"] = ("int identity(1,1)", null, "int auto_increment"),
        ["bigserial"] = ("bigint identity(1,1)", null, "bigint auto_increment"),
        ["smallserial"] = ("smallint identity(1,1)", null, "smallint auto_increment"),
        ["enum"] = ("nvarchar(255)", "text", null),
        ["set"] = ("nvarchar(255)", "text", null),
    };

    public static string Map(string type, SqlDialect target)
    {
        string trimmed = type.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        bool array = trimmed.EndsWith(']');
        string bare = array ? trimmed[..trimmed.IndexOf('[', StringComparison.Ordinal)] : trimmed;
        bool unsigned = Modifiers().IsMatch(bare);
        Match parts = TypeParts().Match(Modifiers().Replace(bare, " "));
        string name = Spaces().Replace(parts.Groups["name"].Value.Trim(), " ").ToLowerInvariant();
        string args = parts.Groups["args"].Value;
        string tail = Spaces().Replace(parts.Groups["tail"].Value.Trim(), " ").ToLowerInvariant();
        string key = tail.Length > 0 ? $"{name} {tail}" : name;

        if (array)
        {
            return target switch
            {
                SqlDialect.Postgres => trimmed,
                SqlDialect.MySql => "json",
                SqlDialect.TSql => "nvarchar(max)",
                _ => "text",
            };
        }

        if (name is "nvarchar" or "varchar" && args.Equals("(max)", StringComparison.OrdinalIgnoreCase))
        {
            return target switch
            {
                SqlDialect.Postgres => "text",
                SqlDialect.MySql => "longtext",
                SqlDialect.Sqlite => "text",
                _ => trimmed,
            };
        }

        if (name is "varbinary" && args.Equals("(max)", StringComparison.OrdinalIgnoreCase))
        {
            return target switch
            {
                SqlDialect.Postgres => "bytea",
                SqlDialect.MySql => "longblob",
                SqlDialect.Sqlite => "blob",
                _ => trimmed,
            };
        }

        if (name is "varbinary" && target == SqlDialect.Postgres)
        {
            return "bytea";
        }

        if (target == SqlDialect.Sqlite)
        {
            return name is "enum" or "set" ? "text" : trimmed;
        }

        string? mapped = null;
        if (s_map.TryGetValue(key, out (string? TSql, string? Postgres, string? MySql) entry))
        {
            mapped = target switch
            {
                SqlDialect.TSql => entry.TSql,
                SqlDialect.Postgres => entry.Postgres,
                _ => entry.MySql,
            };
        }

        if (mapped is not null)
        {
            return mapped.Replace("{a}", args, StringComparison.Ordinal) + (unsigned && target == SqlDialect.MySql ? " unsigned" : string.Empty);
        }

        // int unsigned has no meaning outside MySQL: keep the size, drop the modifier.
        return unsigned && target != SqlDialect.MySql ? Spaces().Replace(Modifiers().Replace(trimmed, " "), " ").Trim() : trimmed;
    }

    [GeneratedRegex(@"^(?<name>[^()]*?)\s*(?:(?<args>\([^)]*\))(?<tail>[^()]*))?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex TypeParts();

    [GeneratedRegex(@"\b(unsigned|zerofill|signed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex Modifiers();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex Spaces();
}
