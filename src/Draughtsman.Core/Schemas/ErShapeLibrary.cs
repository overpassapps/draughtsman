using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Draughtsman.Core.Models;

namespace Draughtsman.Core.Schemas;

/// <summary>The layout numbers every ER shape reader shares (er-shapes.json <c>metrics</c>).</summary>
public sealed record ErMetrics(double FontSize, double RowHeight, double PadX, double PadY, double CharWidth, double BoldWidth, double ColumnGap, double MinWidth, double DoubleInset);

/// <summary>One ER shape: an entity, or a weak entity (double border), each the default shape of an architecture kind.</summary>
public sealed record ErShape(string Name, string Label, string Kind, string DefaultLabel, bool Double, JsonElement DefaultProps);

/// <summary>What a column line says about nullability; <see langword="null"/> where it does not say.</summary>
public enum ErNullability
{
    Null,
    NotNull,
}

/// <summary>One column as drawn: its key markers (PK before FK), name, data type and nullability.</summary>
public sealed record ErColumn(IReadOnlyList<string> Keys, string Name, string Type, ErNullability? Nullable)
{
    /// <summary>The text a row draws in its key column: <c>PK</c>, <c>FK</c>, <c>PK FK</c>, or nothing.</summary>
    public string KeyText => string.Join(' ', Keys);

    /// <summary>The text a row draws in its nullability column: <c>NULL</c>, <c>NOT NULL</c>, or nothing when the line does not say.</summary>
    public string NullText => Nullable switch { ErNullability.Null => "NULL", ErNullability.NotNull => "NOT NULL", _ => "" };
}

/// <summary>The node-local left edge of each row column that has any content (null for a column no row fills).</summary>
public sealed record ErColumnOffsets(double? Key, double Name, double? Type, double? Nullable);

/// <summary>
/// The rows of an entity box, derived from the node's content alone: the name band, the aligned column offsets, and the
/// divider after the leading primary-key rows. A box taller than <see cref="NaturalHeight"/> gives the extra to the row
/// area. Mirrors web/src/model/erShapes.ts.
/// </summary>
public sealed record ErLayout(double HeaderHeight, double BodyTop, double BodyHeight, IReadOnlyList<ErColumn> Rows, ErColumnOffsets Offsets, double? KeyDivider, double NaturalHeight, double NaturalWidth);

/// <summary>
/// The ER (database table) shape library, loaded from the embedded <c>er-shapes.json</c>: the UML class library's
/// compartment mechanism (<see cref="UmlShapeLibrary"/>) applied to a table. An entity is one node whose label is the
/// table name and whose rows are <c>props.columns</c>, an array of strings, one column per string, stored exactly as
/// typed so they round-trip through JSON and the DSL; <see cref="ParseColumn"/> only derives what is drawn. Every shape
/// name stays inside the <c>custom:&lt;lib&gt;/&lt;name&gt;</c> branch the document schema already admits, so no schema
/// change is needed. docs/er-entities.md describes the model.
/// </summary>
public static class ErShapeLibrary
{
    /// <summary>The shape-name prefix every ER shape carries: <c>custom:er/&lt;name&gt;</c>.</summary>
    public const string Prefix = "custom:er/";

    public const string ColumnsProp = "columns";

    private static readonly Regex s_keyMarker = new("^(PK|FK)[ \t]+(?=[^ \t])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimits.Timeout);
    private static readonly Regex s_notNull = new("(?:^|[ \t])not[ \t]+null$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimits.Timeout);
    private static readonly Regex s_null = new("(?:^|[ \t])null$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimits.Timeout);
    private static readonly Regex s_spaces = new("[ \t]+", RegexOptions.CultureInvariant, RegexLimits.Timeout);

    private static readonly Lazy<Library> s_library = new(Load);

    public static ErMetrics Metrics => s_library.Value.Metrics;

    /// <summary>Every shape, in the library's own order (the palette's order).</summary>
    public static IReadOnlyList<ErShape> All => s_library.Value.Shapes;

    /// <summary>The raw embedded JSON, for the MCP get_schema tool (the file is the contract, never restated by hand).</summary>
    public static string RawJson
    {
        get
        {
            using Stream stream = OpenResource();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static bool IsErShape(string shape)
    {
        return TryGet(shape, out _);
    }

    public static bool TryGet(string shape, out ErShape? er)
    {
        er = null;
        if (!shape.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string name = shape[Prefix.Length..];
        foreach (ErShape candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                er = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The column lines of <c>props.columns</c>: the non-blank strings, in order; any other JSON value in the list is ignored.</summary>
    public static IReadOnlyList<string> ColumnLines(IReadOnlyDictionary<string, JsonElement>? props)
    {
        return UmlShapeLibrary.MemberLines(props, ColumnsProp);
    }

    /// <summary>
    /// Splits a column line into what is drawn. Key markers <c>PK</c> and <c>FK</c> (any case, each at most once, in
    /// either order) are taken from the front while something else follows them; then the name (one word, or double
    /// quoted when it holds spaces); then a trailing <c>not null</c> or <c>null</c>; whatever is left, spaces collapsed,
    /// is the data type. Mirrors <c>parseColumn</c> in web/src/model/erShapes.ts; er-shapes.json's examples pin both.
    /// </summary>
    public static ErColumn ParseColumn(string line)
    {
        string rest = line.Trim();
        var found = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            Match marker = s_keyMarker.Match(rest);
            if (!marker.Success)
            {
                break;
            }

            string key = marker.Groups[1].Value.ToUpperInvariant();
            if (!found.Add(key))
            {
                break;
            }

            rest = rest[marker.Length..];
        }

        string name;
        if (rest.StartsWith('"'))
        {
            int close = rest.IndexOf('"', 1);
            name = close < 0 ? rest[1..] : rest[1..close];
            rest = close < 0 ? "" : rest[(close + 1)..];
        }
        else
        {
            int end = 0;
            while (end < rest.Length && rest[end] != ' ' && rest[end] != '\t')
            {
                end++;
            }

            name = rest[..end];
            rest = rest[end..];
        }

        rest = rest.Trim();
        ErNullability? nullable = null;
        Match notNull = s_notNull.Match(rest);
        Match tail = notNull.Success ? notNull : s_null.Match(rest);
        if (tail.Success)
        {
            nullable = notNull.Success ? ErNullability.NotNull : ErNullability.Null;
            rest = rest[..tail.Index].Trim();
        }

        string[] keys = new[] { "PK", "FK" }.Where(found.Contains).ToArray();
        return new ErColumn(keys, name, s_spaces.Replace(rest, " "), nullable);
    }

    /// <summary>The prefix that marks a port id as a column of the entity it is on (<c>col:&lt;encoded name&gt;</c>).</summary>
    public const string ColumnPortPrefix = "col:";

    /// <summary>The longest id the document schema admits.</summary>
    private const int MaxIdLength = 128;

    private static readonly Regex s_percentEscape = new("(%[0-9A-Fa-f]{2})", RegexOptions.CultureInvariant, RegexLimits.Timeout);

    /// <summary>Whether a port id names a column (it may still name one the entity does not have).</summary>
    public static bool IsColumnPortId(string? portId)
    {
        return portId is not null && portId.StartsWith(ColumnPortPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// The port id addressing the column called <paramref name="name"/>: <c>col:</c> then the name with every byte
    /// outside <c>[A-Za-z0-9_-]</c> written <c>%XX</c> (UTF-8, upper-case hex), so it fits the schema's port pattern, the
    /// DSL's bare endpoint token and the id length limit. Null for an empty name or one whose id would pass 128
    /// characters. Mirrors <c>columnPortId</c> in web/src/model/columnPorts.ts.
    /// </summary>
    public static string? ColumnPortId(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        var encoded = new StringBuilder(ColumnPortPrefix);
        foreach (Rune rune in name.EnumerateRunes())
        {
            if (rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value) || rune.Value is '_' or '-'))
            {
                encoded.Append((char)rune.Value);
            }
            else
            {
                foreach (byte b in Encoding.UTF8.GetBytes(rune.ToString()))
                {
                    encoded.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
                }
            }
        }

        return encoded.Length > MaxIdLength ? null : encoded.ToString();
    }

    /// <summary>The column name a <c>col:</c> port id addresses, or null when the id is not one or its escapes do not decode.</summary>
    public static string? ColumnPortName(string? portId)
    {
        if (!IsColumnPortId(portId) || portId!.Length == ColumnPortPrefix.Length)
        {
            return null;
        }

        var bytes = new List<byte>();
        foreach (string part in s_percentEscape.Split(portId[ColumnPortPrefix.Length..]))
        {
            if (part.Length == 3 && part[0] == '%' && s_percentEscape.IsMatch(part))
            {
                bytes.Add(byte.Parse(part.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            else if (part.Contains('%', StringComparison.Ordinal))
            {
                return null;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(part));
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes.ToArray());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The node-local y of the middle of the row a column port names on an entity node, or null when the id is not a
    /// column port, the node is not an entity, or no row has that name. Rows sit under the name band, an
    /// <c>rowHeight</c> each, the first inset by <c>padY</c>; a taller box gives its extra to the bottom, so this depends
    /// only on the row's index. Mirrors <c>columnRowOf</c> in web/src/model/columnPorts.ts.
    /// </summary>
    public static double? ColumnRowCentre(Node node, string? portId)
    {
        string? name = ColumnPortName(portId);
        if (name is null || !IsErShape(node.Shape))
        {
            return null;
        }

        ErMetrics m = Metrics;
        IReadOnlyList<string> lines = ColumnLines(node.Props);
        for (int index = 0; index < lines.Count; index++)
        {
            if (string.Equals(ParseColumn(lines[index]).Name, name, StringComparison.Ordinal))
            {
                return (m.PadY * 2) + m.RowHeight + m.PadY + (m.RowHeight * index) + (m.RowHeight / 2);
            }
        }

        return null;
    }

    /// <summary>
    /// The port for a column row as fractions of the node's box, on its right edge when <paramref name="right"/> else its
    /// left, or null when the id names no row of this node or the node has no height. Mirrors <c>columnPort</c> in
    /// web/src/model/columnPorts.ts.
    /// </summary>
    public static Port? ColumnPort(Node node, string? portId, bool right)
    {
        double? centre = ColumnRowCentre(node, portId);
        if (centre is null || portId is null || node.Geometry is not { H: > 0 } geometry)
        {
            return null;
        }

        return new Port(portId, right ? 1 : 0, Math.Min(1, centre.Value / geometry.H));
    }

    /// <summary>The row area's height for <paramref name="rows"/> rows: an empty entity keeps one row.</summary>
    private static double BodyHeight(int rows)
    {
        ErMetrics m = Metrics;
        return (m.PadY * 2) + (m.RowHeight * Math.Max(1, rows));
    }

    /// <summary>The aligned columns of an entity of height <paramref name="height"/> holding <paramref name="label"/> and the columns in <paramref name="props"/>.</summary>
    public static ErLayout LayoutEntity(string label, IReadOnlyDictionary<string, JsonElement>? props, double height)
    {
        ErMetrics m = Metrics;
        ErColumn[] rows = ColumnLines(props).Select(ParseColumn).ToArray();
        double header = (m.PadY * 2) + m.RowHeight;
        double bodyNatural = BodyHeight(rows.Length);
        double Widest(Func<ErColumn, int> measure) => rows.Length == 0 ? 0 : rows.Max(measure);
        double keyWidth = Widest(row => row.KeyText.Length) * m.CharWidth * m.BoldWidth;
        double nameWidth = Widest(row => row.Name.Length) * m.CharWidth;
        double typeWidth = Widest(row => row.Type.Length) * m.CharWidth;
        double nullWidth = Widest(row => row.NullText.Length) * m.CharWidth * m.BoldWidth;

        double cursor = m.PadX;
        double? Place(double width)
        {
            if (width <= 0)
            {
                return null;
            }

            double at = cursor;
            cursor += width + m.ColumnGap;
            return at;
        }

        double? keyAt = Place(keyWidth);
        double? nameAt = Place(nameWidth);
        double? typeAt = Place(typeWidth);
        double? nullAt = Place(nullWidth);
        double content = Math.Max(cursor - m.ColumnGap - m.PadX, 0);
        double wide = content + (m.PadX * 2);

        int leadingKeys = Array.FindIndex(rows, row => !row.Keys.Contains("PK"));
        if (leadingKeys < 0)
        {
            leadingKeys = rows.Length;
        }

        return new ErLayout(
            header,
            header,
            Math.Max(bodyNatural, height - header),
            rows,
            new ErColumnOffsets(keyAt, nameAt ?? m.PadX, typeAt, nullAt),
            leadingKeys > 0 && leadingKeys < rows.Length ? header + m.PadY + (m.RowHeight * leadingKeys) : null,
            header + bodyNatural,
            Math.Max(m.MinWidth, Math.Ceiling(Math.Max(wide, (label.Length * m.CharWidth * m.BoldWidth) + (m.PadX * 2)))));
    }

    private static Library Load()
    {
        using Stream stream = OpenResource();
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        JsonElement metrics = root.GetProperty("metrics");
        double Metric(string name) => metrics.GetProperty(name).GetDouble();
        var parsed = new ErMetrics(Metric("fontSize"), Metric("rowHeight"), Metric("padX"), Metric("padY"), Metric("charWidth"), Metric("boldWidth"), Metric("columnGap"), Metric("minWidth"), Metric("doubleInset"));
        var shapes = new List<ErShape>();
        foreach (JsonElement shape in root.GetProperty("shapes").EnumerateArray())
        {
            shapes.Add(new ErShape(
                shape.GetProperty("name").GetString()!,
                shape.GetProperty("label").GetString()!,
                shape.GetProperty("kind").GetString()!,
                shape.GetProperty("defaultLabel").GetString()!,
                shape.GetProperty("double").GetBoolean(),
                shape.GetProperty("defaultProps").Clone()));
        }

        return new Library(parsed, shapes);
    }

    private static Stream OpenResource()
    {
        const string name = "schemas/er-shapes.json";
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream ?? throw new FileNotFoundException($"Embedded ER shape library '{name}' is missing from the Draughtsman.Core assembly.");
    }

    private sealed record Library(ErMetrics Metrics, IReadOnlyList<ErShape> Shapes);
}
