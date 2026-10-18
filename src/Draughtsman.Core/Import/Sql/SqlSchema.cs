namespace Draughtsman.Core.Import.Sql;

/// <summary>A table name as written: an optional schema and the table. A longer <c>db.schema.table</c> keeps its last two parts.</summary>
internal readonly record struct SqlName(string? Schema, string Name)
{
    /// <summary>Case-insensitive identity: the default schemas (<c>dbo</c>, <c>public</c>) and no schema are the same table.</summary>
    public string Key => $"{SchemaKey(Schema)}.{Name.ToLowerInvariant()}";

    /// <summary>The name a diagram shows: the table, prefixed by its schema unless that is a default one.</summary>
    public string Display => string.IsNullOrEmpty(SchemaKey(Schema)) ? Name : $"{Schema}.{Name}";

    private static string SchemaKey(string? schema)
    {
        if (string.IsNullOrEmpty(schema) || schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) || schema.Equals("public", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return schema.ToLowerInvariant();
    }
}

/// <summary>One column: its name, the type text as written (spaces collapsed) and what the source said about NULL.</summary>
internal sealed class SqlColumn(string name, string type)
{
    public string Name { get; } = name;

    public string Type { get; set; } = type;

    /// <summary>True for NOT NULL, false for an explicit NULL, null when the source said neither.</summary>
    public bool? NotNull { get; set; }

    /// <summary>An identity or serial column: never null, whatever the source spelled.</summary>
    public bool Generated { get; set; }

    /// <summary>The first and last source lines of the column's definition, which is how a trailing comment is tied to it.</summary>
    public int Line { get; set; }

    public int EndLine { get; set; }
}

/// <summary>A foreign key as written; the target is resolved against the other tables later.</summary>
internal sealed record SqlForeignKey(IReadOnlyList<string> Columns, SqlName Target, IReadOnlyList<string> TargetColumns, int Line);

internal sealed class SqlTable(SqlName name, int line)
{
    public SqlName Name { get; } = name;

    public int Line { get; } = line;

    /// <summary>The line of the <c>)</c> that closes the column list (or of the last token, when it never closes).</summary>
    public int EndLine { get; set; } = line;

    public List<SqlColumn> Columns { get; } = [];

    /// <summary>The primary key's columns in key order; empty when the table has none.</summary>
    public List<string> PrimaryKey { get; } = [];

    /// <summary>
    /// Every UNIQUE constraint's and CREATE UNIQUE INDEX's columns; each one alone is enough to make a foreign key on
    /// exactly those columns one-to-one.
    /// </summary>
    public List<IReadOnlyList<string>> UniqueKeys { get; } = [];

    public List<SqlForeignKey> ForeignKeys { get; } = [];

    public SqlColumn? Find(string column)
    {
        return Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class SqlSchema
{
    private readonly Dictionary<string, SqlTable> _byKey = new(StringComparer.Ordinal);

    public List<SqlTable> Tables { get; } = [];

    public List<ImportWarning> Warnings { get; } = [];

    /// <summary>The source's comments, line by line, for the readers that want more than the schema proper (implied relationships).</summary>
    public List<SqlComment> Comments { get; } = [];

    /// <summary>Adds a table unless one of the same name (ignoring case and a default schema) is already there.</summary>
    public bool TryAdd(SqlTable table)
    {
        if (!_byKey.TryAdd(table.Name.Key, table))
        {
            return false;
        }

        Tables.Add(table);
        return true;
    }

    public SqlTable? Find(SqlName name)
    {
        return _byKey.GetValueOrDefault(name.Key);
    }
}
