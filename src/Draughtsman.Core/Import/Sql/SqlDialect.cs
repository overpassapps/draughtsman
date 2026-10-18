using System.Text.RegularExpressions;

namespace Draughtsman.Core.Import.Sql;

/// <summary>The SQL dialects the schema importer and exporter know. <see cref="Auto"/> is an import hint only.</summary>
public enum SqlDialect
{
    /// <summary>Import only: decide from the text itself (<see cref="SqlDialects.Detect"/>).</summary>
    Auto,
    TSql,
    Postgres,
    MySql,
    Sqlite,
}

/// <summary>Parsing, naming and sniffing for <see cref="SqlDialect"/>.</summary>
public static class SqlDialects
{
    private static readonly Regex s_tsql = new(
        @"^[ \t]*go[ \t]*\r?$|\bidentity\s*\(|\bnvarchar\b|\bdatetime2\b|\buniqueidentifier\b|\bclustered\b|\bset\s+(ansi_nulls|quoted_identifier|nocount)\b|\[dbo\]|\bdbo\.",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexLimits.Timeout);

    private static readonly Regex s_postgres = new(
        @"::|\bserial\b|\bbigserial\b|\bbytea\b|\bjsonb\b|\btimestamptz\b|\bcharacter\s+varying\b|\$\$|\bcreate\s+extension\b|\bpublic\.|\bgenerated\s+(always|by\s+default)\s+as\s+identity\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimits.Timeout);

    private static readonly Regex s_mysql = new(
        @"`|\bengine\s*=|\bauto_increment\b|\bunsigned\b|\bcharset\b|\bdelimiter\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimits.Timeout);

    private static readonly Regex s_sqlite = new(
        @"\bautoincrement\b|\bwithout\s+rowid\b|\bpragma\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimits.Timeout);

    /// <summary>
    /// Reads a caller's dialect name: <c>tsql</c>, <c>postgres</c>, <c>mysql</c>, <c>sqlite</c> (plus the usual aliases),
    /// or <c>auto</c>. A null or blank value is <see cref="SqlDialect.Auto"/>; anything else is not a dialect.
    /// </summary>
    public static bool TryParse(string? name, out SqlDialect dialect)
    {
        dialect = SqlDialect.Auto;
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        switch (name.Trim().ToLowerInvariant())
        {
            case "auto":
                return true;
            case "tsql" or "t-sql" or "sqlserver" or "sql-server" or "mssql":
                dialect = SqlDialect.TSql;
                return true;
            case "postgres" or "postgresql" or "pg" or "pgsql":
                dialect = SqlDialect.Postgres;
                return true;
            case "mysql" or "mariadb":
                dialect = SqlDialect.MySql;
                return true;
            case "sqlite" or "sqlite3":
                dialect = SqlDialect.Sqlite;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The wire name of a dialect: <c>tsql</c>, <c>postgres</c>, <c>mysql</c>, <c>sqlite</c> or <c>auto</c>.</summary>
    public static string Name(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.TSql => "tsql",
            SqlDialect.Postgres => "postgres",
            SqlDialect.MySql => "mysql",
            SqlDialect.Sqlite => "sqlite",
            _ => "auto",
        };
    }

    /// <summary>
    /// Guesses the dialect of pasted SQL by counting its tell-tale constructs; T-SQL when nothing points anywhere,
    /// because that is the first stack the importer serves. The answer only steers the tokenizer's quirks (a
    /// <c>[bracketed]</c> identifier against a Postgres array type, <c>#</c> comments, backslash escapes), so a wrong
    /// guess on portable SQL costs nothing.
    /// </summary>
    public static SqlDialect Detect(string source)
    {
        int tsql = s_tsql.Matches(source).Count;
        int postgres = s_postgres.Matches(source).Count;
        int mysql = s_mysql.Matches(source).Count;
        int sqlite = s_sqlite.Matches(source).Count;
        int best = Math.Max(Math.Max(tsql, postgres), Math.Max(mysql, sqlite));
        if (best == 0 || tsql == best)
        {
            return SqlDialect.TSql;
        }

        if (postgres == best)
        {
            return SqlDialect.Postgres;
        }

        return mysql == best ? SqlDialect.MySql : SqlDialect.Sqlite;
    }
}
