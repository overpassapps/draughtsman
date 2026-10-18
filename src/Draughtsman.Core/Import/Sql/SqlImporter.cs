using System.Text;
using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Import.Sql;

/// <summary>
/// Imports the DDL of a database into a <see cref="Document"/> of ER entities (docs/er-entities.md): one
/// <c>custom:er/entity</c> node per table, its columns as the <c>[PK] [FK] name type [not null | null]</c> lines in
/// <c>props.columns</c>, and one crow's-foot <c>association</c> edge per foreign key, drawn from the referencing table to
/// the referenced one and labelled with the foreign key's columns. A foreign key is one-to-many unless its columns are
/// exactly the child's primary key, a unique constraint or a unique index, when it is one-to-one. A table whose composite
/// primary key includes a foreign key column to another table is a <c>weak-entity</c>; every other table is an
/// <c>entity</c>. A relationship a comment declares that the database does not enforce (SqlImpliedLinks) is drawn as a
/// dashed edge labelled "column (no FK)". No geometry is produced; the caller runs layout, which sizes each table from
/// its columns. Everything skipped or guessed is a warning, never an exception.
/// </summary>
public static class SqlImporter
{
    private const string DefaultTitle = "Imported schema";
    private const string CreatedBy = "sql-import";
    private const string EntityKind = "entity";
    private const string WeakEntityKind = "weak-entity";

    /// <summary>Ends the label of an edge that stands for a relationship the database does not enforce; the DDL export leaves such an edge out.</summary>
    public const string NoForeignKeyMarker = "(no FK)";

    /// <summary>The node prop that keeps a table's tag text.</summary>
    public const string TagProp = "tag";

    private const string LegendId = "legend";
    private const int MaxIdLength = 128;

    /// <exception cref="SourceRejectedException">A line took the reader past its regex match timeout (<see cref="RegexLimits"/>).</exception>
    public static ImportResult Import(string source, SqlImportOptions? options = null)
    {
        return SourceRejectedException.Guard("SQL", () => ImportCore(source, options ?? new SqlImportOptions()));
    }

    private static ImportResult ImportCore(string source, SqlImportOptions options)
    {
        if (options.Type is not (DocumentType.Architecture or DocumentType.Freeform))
        {
            throw new ArgumentException($"Document type '{options.Type}' cannot hold database tables; SQL import needs architecture or freeform.", nameof(options));
        }

        SqlSchema schema = SqlSchemaParser.Parse(source, options.Dialect);
        var warnings = new List<ImportWarning>(schema.Warnings);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var nodeIds = new Dictionary<SqlTable, string>();
        var nodes = new List<Node>();

        // Tags come from the comment block above each CREATE TABLE; the palette is dealt out in order of first appearance.
        var tags = new Dictionary<SqlTable, string>();
        if (options.Tags)
        {
            string[] lines = source.Split('\n');
            foreach (SqlTable table in schema.Tables)
            {
                if (SqlTableTags.Find(lines, table.Line) is { } tag)
                {
                    tags[table] = tag;
                }
            }
        }

        Dictionary<string, NodeStyle> tagStyles = SqlTagStyles.Assign(schema.Tables.Where(tags.ContainsKey).Select(t => tags[t]));

        var columnLines = new Dictionary<SqlTable, List<string>>();
        foreach (SqlTable table in schema.Tables)
        {
            string id = AllocateId(ids, table.Name.Display);
            nodeIds[table] = id;
            columnLines[table] = ColumnLines(table);
            var props = new Dictionary<string, JsonElement>
            {
                [ErShapeLibrary.ColumnsProp] = JsonSerializer.SerializeToElement(columnLines[table]),
            };
            NodeStyle? style = null;
            if (tags.TryGetValue(table, out string? tag))
            {
                props[TagProp] = JsonSerializer.SerializeToElement(tag);

                // Shared tables and untagged ones draw with the theme's own look; only the other tags recolour.
                style = SqlTagStyles.IsShared(tag) ? null : tagStyles[tag];
            }

            string kind = IsWeakEntity(schema, table) ? WeakEntityKind : EntityKind;
            nodes.Add(new Node(id, kind, table.Name.Display, ErShapeLibrary.Prefix + kind) { Props = props, Style = style });
        }

        AddLegend(nodes, tagStyles);

        var edges = new List<Edge>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SqlTable child in schema.Tables)
        {
            foreach (SqlForeignKey key in child.ForeignKeys)
            {
                SqlTable? parent = schema.Find(key.Target);
                if (parent is null)
                {
                    warnings.Add(new ImportWarning(ImportWarningCodes.MissingReference,
                        $"{child.Name.Display} ({string.Join(", ", key.Columns)}) references {key.Target.Display}, which is not in the source; no relationship was drawn.", key.Line, nodeIds[child]));
                    continue;
                }

                string identity = $"{nodeIds[child]}|{string.Join(",", key.Columns.Select(c => c.ToLowerInvariant()))}|{nodeIds[parent]}";
                if (!seen.Add(identity))
                {
                    continue;
                }

                bool oneToOne = IsOneToOne(child, key.Columns);
                bool optional = key.Columns.Any(column => IsNullable(child, column));
                // The connector runs from the FK column's row to the referenced key's row (a derived column port,
                // ErShapeLibrary.ColumnPortId); a composite key draws its first column pair, the label naming them all.
                string? parentColumn = key.TargetColumns.Count > 0 ? key.TargetColumns[0] : parent.PrimaryKey.FirstOrDefault();
                edges.Add(new Edge($"e_{edges.Count + 1}", "association", new Endpoint(nodeIds[child]) { Port = ColumnPort(child, key.Columns.FirstOrDefault(), columnLines[child]) }, new Endpoint(nodeIds[parent]) { Port = ColumnPort(parent, parentColumn, columnLines[parent]) })
                {
                    Label = string.Join(", ", key.Columns),
                    Routing = new Routing(RoutingMode.Orthogonal),
                    Style = new EdgeStyle
                    {
                        ArrowStart = oneToOne ? ArrowHead.ZeroOrOne : ArrowHead.ZeroOrMany,
                        ArrowEnd = optional ? ArrowHead.ZeroOrOne : ArrowHead.OneAndOnlyOne,
                    },
                });
            }
        }

        if (options.Implied)
        {
            foreach (SqlImpliedLink link in SqlImpliedLinks.Read(schema, warnings))
            {
                if (DuplicatesForeignKey(link))
                {
                    continue;
                }

                string label = $"{link.ChildColumn} {NoForeignKeyMarker}";
                bool oneToOne = IsOneToOne(link.Child, [link.ChildColumn]);
                bool optional = IsNullable(link.Child, link.ChildColumn);
                edges.Add(new Edge($"e_{edges.Count + 1}", "association", new Endpoint(nodeIds[link.Child]) { Port = ColumnPort(link.Child, link.ChildColumn, columnLines[link.Child]) }, new Endpoint(nodeIds[link.Parent]) { Port = ColumnPort(link.Parent, link.ParentColumn, columnLines[link.Parent]) })
                {
                    Label = label,
                    Routing = new Routing(RoutingMode.Orthogonal),
                    Style = new EdgeStyle
                    {
                        Dash = Dash.Dashed,
                        ArrowStart = oneToOne ? ArrowHead.ZeroOrOne : ArrowHead.ZeroOrMany,
                        ArrowEnd = optional ? ArrowHead.ZeroOrOne : ArrowHead.OneAndOnlyOne,
                    },
                });
            }
        }

        if (nodes.Count == 0)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.EmptyDiagram, "No CREATE TABLE statement was found in the source."));
        }

        DateTimeOffset now = options.Now ?? DateTimeOffset.UtcNow;
        var document = new Document(
            $"doc_{Guid.NewGuid():N}",
            Document.CurrentSchemaVersion,
            string.IsNullOrWhiteSpace(options.Title) ? DefaultTitle : options.Title,
            options.Type,
            nodes,
            edges,
            new Meta(now, now, CreatedBy));

        // Parser warnings come first in the list; keep the whole of it in source order.
        return new ImportResult(document, warnings.OrderBy(w => w.Line ?? 0).ToList());
    }

    /// <summary>
    /// A "Legend" container holding one small note per tag, filled in that tag's colours. Nothing is added when no table
    /// carried a tag, so untagged SQL imports exactly as before.
    /// </summary>
    private static void AddLegend(List<Node> nodes, Dictionary<string, NodeStyle> tagStyles)
    {
        if (tagStyles.Count == 0)
        {
            return;
        }

        nodes.Add(new Node(LegendId, "container", "Legend", "rectangle"));
        int index = 1;
        foreach ((string tag, NodeStyle style) in tagStyles)
        {
            nodes.Add(new Node($"{LegendId}_{index++}", "note", tag, "rectangle") { Parent = LegendId, Style = style });
        }
    }

    /// <summary>
    /// The column port (<c>col:&lt;name&gt;</c>) addressing a table's column as its row draws it, or null when the table
    /// has no such column (a key naming a typo) or the name has no id: the end then attaches to the table. The name is
    /// read back from the column's own line, so a name the line format rewrites (a quote) still resolves.
    /// </summary>
    private static string? ColumnPort(SqlTable table, string? column, List<string> lines)
    {
        int index = column is null ? -1 : table.Columns.FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : ErShapeLibrary.ColumnPortId(ErShapeLibrary.ParseColumn(lines[index]).Name);
    }

    /// <summary>The <c>[PK] [FK] name type [not null | null]</c> lines of a table (docs/er-entities.md).</summary>
    private static List<string> ColumnLines(SqlTable table)
    {
        var lines = new List<string>(table.Columns.Count);
        foreach (SqlColumn column in table.Columns)
        {
            var line = new StringBuilder();
            if (Contains(table.PrimaryKey, column.Name))
            {
                line.Append("PK ");
            }

            if (table.ForeignKeys.Any(key => Contains(key.Columns, column.Name)))
            {
                line.Append("FK ");
            }

            line.Append(QuoteName(column.Name));
            if (column.Type.Length > 0)
            {
                line.Append(' ').Append(column.Type);
            }

            line.Append(IsNullable(table, column.Name) ? " null" : " not null");
            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary>
    /// A column is nullable unless something makes it not: NOT NULL, membership of the primary key, or an identity or
    /// serial. A column the source never declared (a key column naming a typo) counts as nullable.
    /// </summary>
    private static bool IsNullable(SqlTable table, string column)
    {
        SqlColumn? found = table.Find(column);
        return found is not null && found.NotNull != true && !found.Generated && !Contains(table.PrimaryKey, column);
    }

    /// <summary>
    /// A weak entity in the maintainer's sense: the primary key is composite and includes a column of a foreign key to another
    /// table in the source (a self-reference or a target that is not in the source does not count, as it names no owner
    /// the diagram shows). A single-column key that is also a foreign key is a plain one-to-one child, so it stays an entity.
    /// </summary>
    private static bool IsWeakEntity(SqlSchema schema, SqlTable table)
    {
        return table.PrimaryKey.Count > 1
            && table.ForeignKeys.Any(key => key.Columns.Any(column => Contains(table.PrimaryKey, column))
                && schema.Find(key.Target) is { } parent && parent != table);
    }

    /// <summary>Whether a real foreign key already joins this column to the same column of the same table, so the comment adds nothing.</summary>
    private static bool DuplicatesForeignKey(SqlImpliedLink link)
    {
        foreach (SqlForeignKey key in link.Child.ForeignKeys)
        {
            int at = key.Columns.ToList().FindIndex(c => string.Equals(c, link.ChildColumn, StringComparison.OrdinalIgnoreCase));
            if (at < 0 || !key.Target.Key.Equals(link.Parent.Name.Key, StringComparison.Ordinal))
            {
                continue;
            }

            // A foreign key with no column list points at the target's primary key, in key order.
            string? targetColumn = key.TargetColumns.Count > at ? key.TargetColumns[at] : key.TargetColumns.Count == 0 && link.Parent.PrimaryKey.Count > at ? link.Parent.PrimaryKey[at] : null;
            if (targetColumn is not null && string.Equals(targetColumn, link.ParentColumn, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOneToOne(SqlTable child, IReadOnlyList<string> columns)
    {
        return SameColumns(child.PrimaryKey, columns) || child.UniqueKeys.Any(unique => SameColumns(unique, columns));
    }

    private static bool SameColumns(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        return a.Count > 0 && a.Count == b.Count && a.All(x => Contains(b, x)) && b.All(x => Contains(a, x));
    }

    private static bool Contains(IReadOnlyList<string> columns, string column)
    {
        return columns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The entity line format reads a name as one word, so a name with spaces (or one that reads as a key marker, or holds
    /// a quote) is written double quoted: <c>"Order Id"</c>.
    /// </summary>
    private static string QuoteName(string name)
    {
        bool plain = name.Length > 0
            && !name.Any(char.IsWhiteSpace)
            && !name.Contains('"', StringComparison.Ordinal)
            && !name.Equals("pk", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("fk", StringComparison.OrdinalIgnoreCase);
        return plain ? name : $"\"{name.Replace('"', '\'')}\"";
    }

    /// <summary>A unique, schema-valid, dot-free node id: <c>t_Users</c>, <c>t_journey_UserIdentities</c>.</summary>
    private static string AllocateId(HashSet<string> used, string display)
    {
        var text = new StringBuilder("t_");
        foreach (char c in display)
        {
            text.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        string candidate = text.Length > MaxIdLength ? text.ToString(0, MaxIdLength) : text.ToString();
        string unique = candidate;
        for (int suffix = 2; !used.Add(unique); suffix++)
        {
            string tail = $"_{suffix}";
            unique = candidate[..Math.Min(candidate.Length, MaxIdLength - tail.Length)] + tail;
        }

        return unique;
    }
}
