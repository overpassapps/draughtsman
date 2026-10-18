using System.Text.RegularExpressions;

namespace Draughtsman.Core.Import.Sql;

/// <summary>A relationship a comment declares that the database does not enforce: <paramref name="ChildColumn"/> of <paramref name="Child"/> holds <paramref name="ParentColumn"/> of <paramref name="Parent"/>.</summary>
internal sealed record SqlImpliedLink(SqlTable Child, string ChildColumn, SqlTable Parent, string ParentColumn, int Line);

/// <summary>
/// Reads the relationships a schema's author wrote down in comments because the database has no foreign key for them
/// (docs/er-entities.md). Two shapes are accepted, and nothing else, so a comment that merely mentions an arrow in prose
/// draws nothing:
/// <list type="bullet">
/// <item>Inline, trailing a column's definition inside its CREATE TABLE: <c>TypeId int NOT NULL, -- -&gt; UserTypes.Id (no FK)</c>.
/// The comment text starts with <c>-&gt;</c>, then the target <c>Table.Column</c> (or <c>schema.Table.Column</c>).</item>
/// <item>Listed, a whole comment line (in <c>--</c> or a <c>/* */</c> block, wherever it sits): <c>dbo.Users.TypeId -&gt; dbo.UserTypes.Id</c>,
/// the source <c>[schema.]Table.Column</c> then <c>-&gt;</c> then the target, optionally led by a <c>-</c> or <c>*</c> bullet.</item>
/// </list>
/// After the target only whitespace, an optional <c>(no FK)</c> and then either the end of the comment or a <c>;</c> and
/// free text may follow. Names may be bracketed, double quoted or back-quoted. Each link whose shape is accepted but that
/// cannot be drawn (a table or column that is not in the source, or an inline arrow on no column) is one warning; the same
/// link written twice, or already declared as a real foreign key (the importer's check), is dropped silently.
/// </summary>
internal static partial class SqlImpliedLinks
{
    private const string Segment = """(?:\[[^\]\r\n]+\]|"[^"\r\n]+"|`[^`\r\n]+`|[A-Za-z_][\w$#@]*)""";
    private const string Qualified = $"{Segment}(?:\\.{Segment}){{1,3}}";
    private const string Tail = """\s*(?:\(\s*no\s+fk\s*\))?\s*(?:;.*)?$""";

    public static List<SqlImpliedLink> Read(SqlSchema schema, List<ImportWarning> warnings)
    {
        var links = new List<SqlImpliedLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(SqlImpliedLink link)
        {
            string identity = $"{link.Child.Name.Key}|{link.ChildColumn.ToLowerInvariant()}|{link.Parent.Name.Key}|{link.ParentColumn.ToLowerInvariant()}";
            if (seen.Add(identity))
            {
                links.Add(link);
            }
        }

        foreach (SqlComment comment in schema.Comments)
        {
            Match inline = InlinePattern().Match(comment.Text);
            if (inline.Success)
            {
                SqlTable? owner = schema.Tables.FirstOrDefault(t => comment.Line >= t.Line && comment.Line <= t.EndLine);
                SqlColumn? column = owner is null || !comment.Trailing
                    ? null
                    : owner.Columns.LastOrDefault(c => comment.Line >= c.Line && comment.Line <= c.EndLine);
                if (owner is null || column is null)
                {
                    Skip(warnings, comment, $"\"{comment.Text}\" is not on a column's line, so it does not say which column it describes.");
                    continue;
                }

                if (Target(schema, warnings, comment, inline.Groups["dst"].Value) is { } target)
                {
                    Add(new SqlImpliedLink(owner, column.Name, target.Table, target.Column, comment.Line));
                }

                continue;
            }

            Match listed = ListedPattern().Match(comment.Text);
            if (!listed.Success)
            {
                continue;
            }

            (SqlTable Table, string Column)? source = Target(schema, warnings, comment, listed.Groups["src"].Value);
            (SqlTable Table, string Column)? destination = source is null ? null : Target(schema, warnings, comment, listed.Groups["dst"].Value);
            if (source is { } from && destination is { } to)
            {
                Add(new SqlImpliedLink(from.Table, from.Column, to.Table, to.Column, comment.Line));
            }
        }

        return links;
    }

    /// <summary>Resolves <c>[schema.]Table.Column</c> against the source's tables; warns and returns null when the table or the column is not there.</summary>
    private static (SqlTable Table, string Column)? Target(SqlSchema schema, List<ImportWarning> warnings, SqlComment comment, string qualified)
    {
        List<string> parts = SegmentPattern().Matches(qualified).Select(m => Unquote(m.Value)).ToList();
        string columnName = parts[^1];
        var name = new SqlName(parts.Count >= 3 ? parts[^3] : null, parts[^2]);
        SqlTable? table = schema.Find(name);
        if (table is null)
        {
            Skip(warnings, comment, $"{qualified} names a table ({name.Display}) that is not in the source.");
            return null;
        }

        SqlColumn? column = table.Find(columnName);
        if (column is null)
        {
            Skip(warnings, comment, $"{qualified} names a column that {table.Name.Display} does not have.");
            return null;
        }

        return (table, column.Name);
    }

    private static void Skip(List<ImportWarning> warnings, SqlComment comment, string reason)
    {
        warnings.Add(new ImportWarning(ImportWarningCodes.ImpliedLinkSkipped, $"Line {comment.Line}: no relationship was drawn for the comment link; {reason}", comment.Line));
    }

    private static string Unquote(string segment)
    {
        return segment.Length >= 2 && segment[0] is '[' or '"' or '`' ? segment[1..^1] : segment;
    }

    [GeneratedRegex($"^->\\s*(?<dst>{Qualified}){Tail}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex InlinePattern();

    [GeneratedRegex($"^(?:[-*]\\s+)?(?<src>{Qualified})\\s*->\\s*(?<dst>{Qualified}){Tail}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex ListedPattern();

    [GeneratedRegex(Segment, RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex SegmentPattern();
}
