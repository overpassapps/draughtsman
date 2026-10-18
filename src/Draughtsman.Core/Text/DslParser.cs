using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Text;

/// <summary>
/// Hand-written recursive-descent parser for the DSL. Line-oriented: every statement is one line, so it reads a
/// line at a time, dispatches on the first word, and resolves cross-references (container membership, edge
/// endpoints, default shapes, kind warnings) once the whole text has been read.
/// </summary>
internal sealed class DslParser
{
    private readonly string _text;
    private readonly List<DslWarning> _warnings = [];
    private readonly List<PendingNode> _nodes = [];
    private readonly List<PendingEdge> _edges = [];
    private readonly List<PendingChild> _children = [];
    private readonly Dictionary<string, int> _nodeLines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _edgeLines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _headerLines = new(StringComparer.Ordinal);
    private readonly List<Keyframe> _keyframes = [];
    private readonly Dictionary<string, int> _keyframeLines = new(StringComparer.Ordinal);

    private bool _seenMagic;
    private string? _id;
    private DocumentType? _type;
    private string? _title;
    private Dictionary<string, string>? _theme;
    private DateTimeOffset? _createdAt;
    private DateTimeOffset? _updatedAt;
    private string? _createdBy;
    private List<AiProvenance>? _provenance;
    private bool? _loop;
    private int _flythroughLine;

    public DslParser(string text)
    {
        _text = text;
    }

    private enum Statement
    {
        Node,
        Container,
        Edge,
        Theme,
        Meta,
        Provenance,
        Flythrough,
        Keyframe,
    }

    private enum ValueKind
    {
        Word,
        Quoted,
        Group,
    }

    public ParseResult Parse()
    {
        string[] lines = _text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i].TrimEnd('\r');
            string content = LineCursor.StripComment(raw);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            ParseStatement(new LineCursor(content, i + 1));
        }

        if (!_seenMagic)
        {
            throw new DslParseException(1, 1, $"the document is empty; the first line must be '{DslSyntax.Magic} {Document.CurrentSchemaVersion}'");
        }

        if (_type is null)
        {
            throw new DslParseException(1, 1, $"missing 'type' line; write one of {DslSyntax.EnumNames<DocumentType>()}");
        }

        return new ParseResult(Build(_type.Value), _warnings);
    }

    private void ParseStatement(LineCursor cursor)
    {
        int keywordColumn = cursor.TokenColumn;
        string keyword = cursor.ReadWord("a statement");
        if (!_seenMagic)
        {
            if (!string.Equals(keyword, DslSyntax.Magic, StringComparison.Ordinal))
            {
                throw cursor.ErrorAt(keywordColumn, $"the first line must be '{DslSyntax.Magic} {Document.CurrentSchemaVersion}'");
            }

            ParseMagic(cursor);
            return;
        }

        switch (keyword)
        {
            case DslSyntax.Magic:
                throw cursor.ErrorAt(keywordColumn, $"'{DslSyntax.Magic}' may only appear on the first line");
            case "id":
                MarkHeader(cursor, keyword, keywordColumn);
                int idColumn = cursor.TokenColumn;
                _id = ReadId(cursor, "the document id", idColumn);
                break;
            case "type":
                MarkHeader(cursor, keyword, keywordColumn);
                int typeColumn = cursor.TokenColumn;
                string typeText = cursor.ReadWord("a document type");
                if (!DslSyntax.TryParseEnum(typeText, out DocumentType type))
                {
                    throw cursor.ErrorAt(typeColumn, $"unknown document type '{typeText}'; expected one of {DslSyntax.EnumNames<DocumentType>()}");
                }

                _type = type;
                break;
            case "title":
                MarkHeader(cursor, keyword, keywordColumn);
                _title = cursor.ReadQuoted("the title");
                break;
            case "theme":
                MarkHeader(cursor, keyword, keywordColumn);
                ParseTheme(cursor, keywordColumn);
                break;
            case "meta":
                MarkHeader(cursor, keyword, keywordColumn);
                ParseMeta(cursor);
                break;
            case "provenance":
                ParseProvenance(cursor);
                break;
            case "node":
                ParseNode(cursor, isContainer: false);
                break;
            case DslSyntax.ContainerKind:
                ParseNode(cursor, isContainer: true);
                break;
            case "edge":
                ParseEdge(cursor);
                break;
            case DslSyntax.FlythroughStatement:
                MarkHeader(cursor, keyword, keywordColumn);
                ParseFlythrough(cursor);
                break;
            case DslSyntax.KeyframeStatement:
                ParseKeyframe(cursor);
                break;
            default:
                throw cursor.ErrorAt(keywordColumn, $"unknown statement '{keyword}'; expected node, container, edge, keyframe or a header line (id, type, title, theme, meta, provenance, flythrough)");
        }

        cursor.ExpectEnd();
    }

    private void ParseMagic(LineCursor cursor)
    {
        int column = cursor.TokenColumn;
        string version = cursor.ReadWord("a version number");
        if (!string.Equals(version, Document.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw cursor.ErrorAt(column, $"unsupported DSL version '{version}'; this build reads version {Document.CurrentSchemaVersion}");
        }

        cursor.ExpectEnd();
        _seenMagic = true;
    }

    private void MarkHeader(LineCursor cursor, string keyword, int column)
    {
        if (_headerLines.TryGetValue(keyword, out int previous))
        {
            throw cursor.ErrorAt(column, $"'{keyword}' was already given on line {previous}");
        }

        _headerLines[keyword] = cursor.Line;
    }

    private void ParseTheme(LineCursor cursor, int keywordColumn)
    {
        List<Attribute> attributes = ReadAttributes(cursor, Statement.Theme, out _);
        if (attributes.Count == 0)
        {
            throw cursor.ErrorAt(keywordColumn, "'theme' needs at least one token=\"value\" pair");
        }

        _theme = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Attribute attribute in attributes)
        {
            _theme[attribute.Key] = QuotedValue(cursor, attribute, "theme values");
        }
    }

    private void ParseMeta(LineCursor cursor)
    {
        foreach (Attribute attribute in ReadAttributes(cursor, Statement.Meta, out _))
        {
            switch (attribute.Key)
            {
                case "created":
                    _createdAt = TimestampValue(cursor, attribute);
                    break;
                case "updated":
                    _updatedAt = TimestampValue(cursor, attribute);
                    break;
                case "by":
                    _createdBy = TextValue(cursor, attribute);
                    break;
                default:
                    throw cursor.ErrorAt(attribute.KeyColumn, $"unknown meta field '{attribute.Key}'; expected created, updated or by");
            }
        }
    }

    private void ParseProvenance(LineCursor cursor)
    {
        string agent = cursor.ReadWordOrQuoted("an agent name");
        int actionColumn = cursor.TokenColumn;
        string actionText = cursor.ReadWord("an action");
        if (!DslSyntax.TryParseEnum(actionText, out AiAction action))
        {
            throw cursor.ErrorAt(actionColumn, $"unknown provenance action '{actionText}'; expected one of {DslSyntax.EnumNames<AiAction>()}");
        }

        int atColumn = cursor.TokenColumn;
        string atText = cursor.ReadWord("a timestamp");
        if (!DslSyntax.TryParseTimestamp(atText, out DateTimeOffset at))
        {
            throw cursor.ErrorAt(atColumn, $"'{atText}' is not a timestamp; write it as UTC {DslSyntax.TimestampExample}, for example {DslSyntax.TimestampSample}");
        }

        string? model = null;
        List<Attribute> attributes = ReadAttributes(cursor, Statement.Provenance, out List<(string Id, int Column)>? elements);
        foreach (Attribute attribute in attributes)
        {
            if (!string.Equals(attribute.Key, "model", StringComparison.Ordinal))
            {
                throw cursor.ErrorAt(attribute.KeyColumn, $"unknown provenance field '{attribute.Key}'; expected model");
            }

            model = TextValue(cursor, attribute);
        }

        if (elements is null)
        {
            throw cursor.Error("provenance needs the touched element ids in braces, for example { n_api e_1 }");
        }

        _provenance ??= [];
        _provenance.Add(new AiProvenance(agent, at, action, elements.Select(e => e.Id).ToList()) { Model = model });
    }

    /// <summary>The <c>flythrough loop=true</c> line: the tour-level settings. The keyframes are their own lines.</summary>
    private void ParseFlythrough(LineCursor cursor)
    {
        _flythroughLine = cursor.Line;
        foreach (Attribute attribute in ReadAttributes(cursor, Statement.Flythrough, out _))
        {
            if (!string.Equals(attribute.Key, "loop", StringComparison.Ordinal))
            {
                throw cursor.ErrorAt(attribute.KeyColumn, $"unknown flythrough field '{attribute.Key}'; expected loop");
            }

            _loop = BoolValue(cursor, attribute);
        }
    }

    /// <summary>
    /// <c>keyframe k_1 @ x,y wxh [caption="..."] [duration=ms] [hold=ms] [easing=name]</c>. The view needs its size (there
    /// is no default one to fall back to); a view with no area, which the schema refuses, skips the keyframe with a warning.
    /// </summary>
    private void ParseKeyframe(LineCursor cursor)
    {
        int idColumn = cursor.TokenColumn;
        string id = ReadId(cursor, "a keyframe id", idColumn);
        if (_keyframeLines.TryGetValue(id, out int previousLine))
        {
            throw cursor.ErrorAt(idColumn, $"keyframe id '{id}' was already used on line {previousLine}");
        }

        _keyframeLines[id] = cursor.Line;

        cursor.SkipSpaces();
        if (!cursor.TryConsume('@'))
        {
            throw cursor.Error("a keyframe needs the view it frames: write @ x,y wxh after the id");
        }

        int positionColumn = cursor.TokenColumn;
        string position = cursor.ReadWord("a position x,y after '@'");
        (double x, double y) = ReadPair(cursor, position, positionColumn, ',', "a position x,y");
        int sizeColumn = cursor.TokenColumn;
        string size = cursor.ReadWord("a size wxh");
        (double w, double h) = ReadPair(cursor, size, sizeColumn, 'x', "a size wxh");

        string? caption = null;
        double? duration = null;
        double? hold = null;
        KeyframeEasing? easing = null;
        foreach (Attribute attribute in ReadAttributes(cursor, Statement.Keyframe, out _))
        {
            switch (attribute.Key)
            {
                case "caption":
                    caption = QuotedValue(cursor, attribute, "a caption");
                    break;
                case "duration":
                    duration = MillisecondsValue(cursor, attribute);
                    break;
                case "hold":
                    hold = MillisecondsValue(cursor, attribute);
                    break;
                case "easing":
                    easing = EnumValue<KeyframeEasing>(cursor, attribute);
                    break;
                default:
                    throw cursor.ErrorAt(attribute.KeyColumn, $"unknown keyframe field '{attribute.Key}'; expected caption, duration, hold or easing");
            }
        }

        if (w <= 0 || h <= 0)
        {
            _warnings.Add(new DslWarning(cursor.Line, sizeColumn, $"keyframe '{id}' frames a view with no area (width and height must be above zero); the keyframe is skipped"));
            return;
        }

        _keyframes.Add(new Keyframe(id, new ViewRect(x, y, w, h)) { Caption = caption, DurationMs = duration, HoldMs = hold, Easing = easing });
    }

    /// <summary>The schema caps a keyframe's travel and hold time at ten minutes and admits no negative one; dropping it restores the default.</summary>
    private double? MillisecondsValue(LineCursor cursor, Attribute attribute)
    {
        double value = NumberValue(cursor, attribute);
        if (value is >= 0 and <= DslSyntax.MaxKeyframeMilliseconds)
        {
            return value;
        }

        _warnings.Add(new DslWarning(cursor.Line, attribute.Value.Column, $"'{attribute.Key}' must be between 0 and {DslSyntax.FormatNumber(DslSyntax.MaxKeyframeMilliseconds)} milliseconds; the default is used instead"));
        return null;
    }

    private void ParseNode(LineCursor cursor, bool isContainer)
    {
        int idColumn = cursor.TokenColumn;
        string id = ReadId(cursor, isContainer ? "a container id" : "a node id", idColumn);
        if (_nodeLines.TryGetValue(id, out int previousLine))
        {
            throw cursor.ErrorAt(idColumn, $"node id '{id}' was already used on line {previousLine}");
        }

        _nodeLines[id] = cursor.Line;

        int kindColumn = cursor.TokenColumn;
        string kind = isContainer ? DslSyntax.ContainerKind : cursor.ReadWordOrQuoted("a node kind");
        string label = cursor.ReadQuoted("the label");

        Geometry? geometry = ReadGeometry(cursor);
        List<Attribute> attributes = ReadAttributes(cursor, isContainer ? Statement.Container : Statement.Node, out List<(string Id, int Column)>? children);

        string? shape = null;
        string? parent = null;
        int parentColumn = 0;
        List<Port>? ports = null;
        NodeStyle? style = null;
        Dictionary<string, JsonElement>? props = null;
        foreach (Attribute attribute in attributes)
        {
            if (attribute.QuotedKey || !DslSyntax.IsReservedKey(attribute.Key))
            {
                props ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                props[attribute.Key] = PropValue(cursor, attribute);
                continue;
            }

            switch (attribute.Key)
            {
                case "shape":
                    shape = ShapeValue(cursor, attribute);
                    break;
                case "parent":
                    parent = IdValue(cursor, attribute);
                    parentColumn = attribute.Value.Column;
                    break;
                case "rotation":
                    if (geometry is null)
                    {
                        throw cursor.ErrorAt(attribute.KeyColumn, "rotation needs a position: write @ x,y before it");
                    }

                    geometry = geometry with { Rotation = NumberValue(cursor, attribute) };
                    break;
                case "ports":
                    ports = PortsValue(cursor, attribute);
                    break;
                default:
                    if (attribute.Key.StartsWith(DslSyntax.StylePrefix, StringComparison.Ordinal))
                    {
                        style = ApplyNodeStyle(cursor, style ?? new NodeStyle(), attribute);
                        break;
                    }

                    throw ReservedKeyError(cursor, attribute, isContainer ? Statement.Container : Statement.Node);
            }
        }

        var node = new Node(id, kind, label, shape ?? string.Empty)
        {
            Geometry = geometry,
            Style = style,
            Props = props,
            Parent = parent,
            Ports = ports,
        };
        _nodes.Add(new PendingNode(node, shape is not null, cursor.Line, kindColumn, parentColumn));

        if (children is not null)
        {
            foreach ((string childId, int column) in children)
            {
                _children.Add(new PendingChild(id, childId, cursor.Line, column));
            }
        }
    }

    private void ParseEdge(LineCursor cursor)
    {
        int idColumn = cursor.TokenColumn;
        string id = ReadId(cursor, "an edge id", idColumn);
        if (_edgeLines.TryGetValue(id, out int previousLine))
        {
            throw cursor.ErrorAt(idColumn, $"edge id '{id}' was already used on line {previousLine}");
        }

        _edgeLines[id] = cursor.Line;

        int kindColumn = cursor.TokenColumn;
        string kind = cursor.ReadWordOrQuoted("an edge kind");

        int fromColumn = cursor.TokenColumn;
        string from = ReadEndpointToken(cursor, "the source node");
        int arrowColumn = cursor.TokenColumn;
        string arrow = cursor.ReadWord("'->'");
        if (!string.Equals(arrow, "->", StringComparison.Ordinal))
        {
            throw cursor.ErrorAt(arrowColumn, $"expected '->' between the two endpoints but found '{arrow}'");
        }

        int toColumn = cursor.TokenColumn;
        string to = ReadEndpointToken(cursor, "the target node");

        // A quoted string here is the label unless an '=' follows it, in which case it is a quoted prop key.
        string? label = null;
        cursor.SkipSpaces();
        if (!cursor.AtEnd && cursor.Current == '"')
        {
            int start = cursor.Position;
            string candidate = cursor.ReadQuoted("the label");
            cursor.SkipSpaces();
            if (!cursor.AtEnd && cursor.Current == '=')
            {
                cursor.Reset(start);
            }
            else
            {
                label = candidate;
            }
        }

        List<Attribute> attributes = ReadAttributes(cursor, Statement.Edge, out _);
        RoutingMode? routing = null;
        List<Point>? waypoints = null;
        int waypointsColumn = 0;
        EdgeStyle? style = null;
        Dictionary<string, JsonElement>? props = null;
        foreach (Attribute attribute in attributes)
        {
            if (attribute.QuotedKey || !DslSyntax.IsReservedKey(attribute.Key))
            {
                props ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                props[attribute.Key] = PropValue(cursor, attribute);
                continue;
            }

            switch (attribute.Key)
            {
                case "routing":
                    routing = EnumValue<RoutingMode>(cursor, attribute);
                    break;
                case "waypoints":
                    waypoints = WaypointsValue(cursor, attribute);
                    waypointsColumn = attribute.KeyColumn;
                    break;
                default:
                    if (attribute.Key.StartsWith(DslSyntax.StylePrefix, StringComparison.Ordinal))
                    {
                        style = ApplyEdgeStyle(cursor, style ?? new EdgeStyle(), attribute);
                        break;
                    }

                    throw ReservedKeyError(cursor, attribute, Statement.Edge);
            }
        }

        if (waypoints is not null && routing is null)
        {
            throw cursor.ErrorAt(waypointsColumn, $"waypoints need a routing mode: add routing=<{DslSyntax.EnumNames<RoutingMode>()}>");
        }

        _edges.Add(new PendingEdge(id, kind, from, fromColumn, to, toColumn, label, routing, waypoints, style, props, cursor.Line, kindColumn));
    }

    private Geometry? ReadGeometry(LineCursor cursor)
    {
        cursor.SkipSpaces();
        if (!cursor.TryConsume('@'))
        {
            return null;
        }

        int positionColumn = cursor.TokenColumn;
        string position = cursor.ReadWord("a position x,y after '@'");
        (double x, double y) = ReadPair(cursor, position, positionColumn, ',', "a position x,y");

        double w = DslSyntax.DefaultWidth;
        double h = DslSyntax.DefaultHeight;
        string? next = cursor.PeekWord();
        if (next is not null && next.Contains('x', StringComparison.Ordinal) && !next.Contains('=', StringComparison.Ordinal) && (char.IsAsciiDigit(next[0]) || next[0] == '-'))
        {
            int sizeColumn = cursor.TokenColumn;
            cursor.ReadWord("a size");
            (w, h) = ReadPair(cursor, next, sizeColumn, 'x', "a size wxh");
            w = NotNegative(cursor, sizeColumn, w, DslSyntax.DefaultWidth, "a width");
            h = NotNegative(cursor, sizeColumn, h, DslSyntax.DefaultHeight, "a height");
        }

        return new Geometry(x, y, w, h);
    }

    private static (double, double) ReadPair(LineCursor cursor, string token, int column, char separator, string what)
    {
        int split = token.IndexOf(separator, StringComparison.Ordinal);
        if (split < 0 || token.IndexOf(separator, split + 1) >= 0)
        {
            throw cursor.ErrorAt(column, $"expected {what} but found '{token}'");
        }

        string first = token[..split];
        string second = token[(split + 1)..];
        if (!DslSyntax.TryParseNumber(first, out double x) || !DslSyntax.TryParseNumber(second, out double y))
        {
            throw cursor.ErrorAt(column, $"expected {what} with two numbers in range but found '{token}'");
        }

        return (x, y);
    }

    private static string ReadId(LineCursor cursor, string what, int column)
    {
        string id = cursor.ReadWord(what);
        if (!DslSyntax.IsValidId(id))
        {
            throw cursor.ErrorAt(column, $"'{id}' is not a valid id; ids start with a letter or underscore and contain letters, digits, '_', '.' or '-'");
        }

        return id;
    }

    private static string ReadEndpointToken(LineCursor cursor, string what)
    {
        int column = cursor.TokenColumn;
        string token = cursor.ReadWord(what);
        if (!DslSyntax.EndpointPattern().IsMatch(token))
        {
            throw cursor.ErrorAt(column, $"'{token}' is not a valid node reference; write <node> or <node>.<port>");
        }

        return token;
    }

    /// <summary>Reads the <c>key=value</c> tail of a statement, plus at most one <c>{ ids }</c> group where the statement allows it.</summary>
    private static List<Attribute> ReadAttributes(LineCursor cursor, Statement statement, out List<(string Id, int Column)>? group)
    {
        var attributes = new List<Attribute>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        group = null;
        while (true)
        {
            cursor.SkipSpaces();
            if (cursor.AtEnd)
            {
                return attributes;
            }

            if (cursor.Current == '{')
            {
                if (statement is not (Statement.Container or Statement.Provenance))
                {
                    throw cursor.Error(statement == Statement.Node
                        ? "only containers list children in braces; write 'container' instead of 'node'"
                        : "unexpected '{'");
                }

                if (group is not null)
                {
                    throw cursor.Error("only one { } group is allowed per line");
                }

                group = ReadIdGroup(cursor, statement == Statement.Container ? "the container's children" : "the touched element ids");
                continue;
            }

            int keyColumn = cursor.TokenColumn;
            bool quotedKey = cursor.Current == '"';
            string key = quotedKey ? cursor.ReadQuoted("a key") : cursor.ReadWord("key=value");
            cursor.Expect('=', $"after '{key}'");
            cursor.SkipSpaces();
            if (cursor.AtEnd)
            {
                throw cursor.Error($"expected a value after '{key}='");
            }

            int valueColumn = cursor.TokenColumn;
            AttributeValue value = cursor.Current switch
            {
                '"' => new AttributeValue(ValueKind.Quoted, cursor.ReadQuoted($"the value of '{key}'"), valueColumn),
                '[' => new AttributeValue(ValueKind.Group, cursor.ReadGroup('[', ']', $"the value of '{key}'"), valueColumn),
                '{' => new AttributeValue(ValueKind.Group, cursor.ReadGroup('{', '}', $"the value of '{key}'"), valueColumn),
                _ => new AttributeValue(ValueKind.Word, cursor.ReadWord($"a value for '{key}'"), valueColumn),
            };

            string seenKey = quotedKey ? "\"" + key : key;
            if (!seen.Add(seenKey))
            {
                throw cursor.ErrorAt(keyColumn, $"'{key}' is given twice on this line");
            }

            attributes.Add(new Attribute(key, quotedKey, value, keyColumn));
        }
    }

    private static List<(string Id, int Column)> ReadIdGroup(LineCursor cursor, string what)
    {
        int column = cursor.TokenColumn;
        string raw = cursor.ReadGroup('{', '}', what);
        return SplitIds(cursor, raw[1..^1], column + 1, what);
    }

    private static List<(string Id, int Column)> SplitIds(LineCursor cursor, string content, int column, string what)
    {
        var ids = new List<(string, int)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string token, int tokenColumn) in SplitTokens(content, column))
        {
            if (!DslSyntax.IsValidId(token))
            {
                throw cursor.ErrorAt(tokenColumn, $"'{token}' in {what} is not a valid id");
            }

            if (!seen.Add(token))
            {
                throw cursor.ErrorAt(tokenColumn, $"'{token}' is listed twice in {what}");
            }

            ids.Add((token, tokenColumn));
        }

        return ids;
    }

    private static IEnumerable<(string Token, int Column)> SplitTokens(string content, int column)
    {
        int i = 0;
        while (i < content.Length)
        {
            while (i < content.Length && char.IsWhiteSpace(content[i]))
            {
                i++;
            }

            int start = i;
            while (i < content.Length && !char.IsWhiteSpace(content[i]))
            {
                i++;
            }

            if (i > start)
            {
                yield return (content[start..i], column + start);
            }
        }
    }

    private static DslParseException ReservedKeyError(LineCursor cursor, Attribute attribute, Statement statement)
    {
        bool isEdge = statement == Statement.Edge;
        string message = attribute.Key switch
        {
            "id" => $"'id' is positional: the id is the first word after '{(isEdge ? "edge" : statement == Statement.Container ? DslSyntax.ContainerKind : "node")}'",
            "kind" when statement == Statement.Container => $"a container's kind is always '{DslSyntax.ContainerKind}'; use 'node' for any other kind",
            "kind" => "'kind' is positional: the kind is the word after the id",
            "label" when isEdge => "'label' is positional: write the label as a quoted string after the endpoints, before any key=value",
            "label" when statement == Statement.Container => "'label' is positional: write the label as the quoted string after the container id",
            "label" => "'label' is positional: write the label as the quoted string after the kind",
            "style" => isEdge
                ? "'style' takes a field: write style.stroke=..., style.strokeWidth=..., style.dash=..., style.arrowStart=... or style.arrowEnd=..."
                : "'style' takes a field: write style.fill=..., style.stroke=..., style.strokeWidth=..., style.textColor=..., style.font=..., style.dash=..., style.fontSize=... or style.bold=...",
            _ => isEdge ? $"'{attribute.Key}' applies to nodes, not edges" : $"'{attribute.Key}' applies to edges, not nodes",
        };
        return cursor.ErrorAt(attribute.KeyColumn, message);
    }

    private static string QuotedValue(LineCursor cursor, Attribute attribute, string what)
    {
        if (attribute.Value.Kind != ValueKind.Quoted)
        {
            throw cursor.ErrorAt(attribute.Value.Column, $"{KeyText(attribute)}={attribute.Value.Text}: {what} must be quoted, write {KeyText(attribute)}={DslSyntax.Quote(attribute.Value.Text)}");
        }

        return attribute.Value.Text;
    }

    private static string KeyText(Attribute attribute)
    {
        return attribute.QuotedKey ? DslSyntax.Quote(attribute.Key) : attribute.Key;
    }

    /// <summary>
    /// A field whose value the schema restricts to a vocabulary is lint, not a syntax error: the value falls back to
    /// the field's default and a warning names it, so one bad word in a pasted diagram does not reject the whole of
    /// it. The document <see cref="Dsl.Parse"/> returns always satisfies docs/schema/document.schema.json.
    /// </summary>
    private string? ShapeValue(LineCursor cursor, Attribute attribute)
    {
        string text = TextValue(cursor, attribute);
        if (DslSyntax.IsValidShape(text))
        {
            return text;
        }

        _warnings.Add(new DslWarning(cursor.Line, attribute.Value.Column, $"unknown shape '{text}'; the kind's default shape is used instead"));
        return null;
    }

    /// <summary>The schema admits no negative stroke width; dropping the override inherits the theme's.</summary>
    private double? StrokeWidthValue(LineCursor cursor, Attribute attribute)
    {
        double width = NumberValue(cursor, attribute);
        if (width >= 0)
        {
            return width;
        }

        _warnings.Add(new DslWarning(cursor.Line, attribute.Value.Column, $"'{attribute.Key}' cannot be negative; the theme's stroke width is used instead"));
        return null;
    }

    /// <summary>The schema admits no negative font size; dropping the override inherits the default label size.</summary>
    private double? NonNegativeNumberValue(LineCursor cursor, Attribute attribute)
    {
        double value = NumberValue(cursor, attribute);
        if (value >= 0)
        {
            return value;
        }

        _warnings.Add(new DslWarning(cursor.Line, attribute.Value.Column, $"'{attribute.Key}' cannot be negative; the default font size is used instead"));
        return null;
    }

    /// <summary>A boolean style value: true or false, read back from the word the printer writes.</summary>
    private static bool BoolValue(LineCursor cursor, Attribute attribute)
    {
        if (attribute.Value.Kind == ValueKind.Word && attribute.Value.Text is "true" or "false")
        {
            return attribute.Value.Text == "true";
        }

        throw cursor.ErrorAt(attribute.Value.Column, $"'{attribute.Key}' must be true or false");
    }

    /// <summary>The schema admits no negative width or height.</summary>
    private double NotNegative(LineCursor cursor, int column, double value, double fallback, string what)
    {
        if (value >= 0)
        {
            return value;
        }

        _warnings.Add(new DslWarning(cursor.Line, column, $"{what} cannot be negative; {DslSyntax.FormatNumber(fallback)} is used instead"));
        return fallback;
    }

    /// <summary>A port offset is a fraction of the node's width or height, which the schema caps at 0 to 1.</summary>
    private double Fraction(LineCursor cursor, int column, double value, string what)
    {
        if (value is >= 0 and <= 1)
        {
            return value;
        }

        double clamped = Math.Clamp(value, 0, 1);
        _warnings.Add(new DslWarning(cursor.Line, column, $"{what} must be between 0 and 1; {DslSyntax.FormatNumber(clamped)} is used instead"));
        return clamped;
    }

    private static string TextValue(LineCursor cursor, Attribute attribute)
    {
        if (attribute.Value.Kind == ValueKind.Group)
        {
            throw cursor.ErrorAt(attribute.Value.Column, $"'{attribute.Key}' takes text, not a bracketed value");
        }

        return attribute.Value.Text;
    }

    private static string IdValue(LineCursor cursor, Attribute attribute)
    {
        if (attribute.Value.Kind != ValueKind.Word || !DslSyntax.IsValidId(attribute.Value.Text))
        {
            throw cursor.ErrorAt(attribute.Value.Column, $"'{attribute.Key}' must be a node id");
        }

        return attribute.Value.Text;
    }

    private static double NumberValue(LineCursor cursor, Attribute attribute)
    {
        if (attribute.Value.Kind != ValueKind.Word || !DslSyntax.TryParseNumber(attribute.Value.Text, out double value))
        {
            throw cursor.ErrorAt(attribute.Value.Column, $"'{attribute.Key}' must be a number in range");
        }

        return value;
    }

    private static T EnumValue<T>(LineCursor cursor, Attribute attribute)
        where T : struct, Enum
    {
        if (attribute.Value.Kind == ValueKind.Word && DslSyntax.TryParseEnum(attribute.Value.Text, out T value))
        {
            return value;
        }

        throw cursor.ErrorAt(attribute.Value.Column, $"'{attribute.Key}' must be one of {DslSyntax.EnumNames<T>()}");
    }

    private static DateTimeOffset TimestampValue(LineCursor cursor, Attribute attribute)
    {
        if (attribute.Value.Kind == ValueKind.Word && DslSyntax.TryParseTimestamp(attribute.Value.Text, out DateTimeOffset value))
        {
            return value;
        }

        throw cursor.ErrorAt(attribute.Value.Column, $"'{attribute.Key}' must be a UTC timestamp written {DslSyntax.TimestampExample}, unquoted, for example {attribute.Key}={DslSyntax.TimestampSample}");
    }

    /// <summary>
    /// A prop value is JSON-shaped: a quoted string, a number, true, false, null, or an inline array or object.
    /// </summary>
    private static JsonElement PropValue(LineCursor cursor, Attribute attribute)
    {
        string text = attribute.Value.Text;
        switch (attribute.Value.Kind)
        {
            case ValueKind.Quoted:
                return JsonSerializer.SerializeToElement(text);
            case ValueKind.Word:
                if (DslSyntax.IsNumber(text) || text is "true" or "false" or "null")
                {
                    return JsonSerializer.Deserialize<JsonElement>(text);
                }

                return JsonSerializer.SerializeToElement(QuotedValue(cursor, attribute, "string values"));
            default:
                try
                {
                    return JsonSerializer.Deserialize<JsonElement>(text);
                }
                catch (JsonException ex)
                {
                    throw cursor.ErrorAt(attribute.Value.Column, $"the value of '{attribute.Key}' is not valid JSON: {ex.Message}");
                }
        }
    }

    private List<Port> PortsValue(LineCursor cursor, Attribute attribute)
    {
        string content = GroupContent(cursor, attribute, "ports=[id@x,y ...]");
        var ports = new List<Port>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string token, int column) in SplitTokens(content, attribute.Value.Column + 1))
        {
            int at = token.IndexOf('@', StringComparison.Ordinal);
            if (at <= 0)
            {
                throw cursor.ErrorAt(column, $"port '{token}' must be written id@x,y");
            }

            string id = token[..at];
            if (!DslSyntax.IsValidId(id))
            {
                throw cursor.ErrorAt(column, $"'{id}' is not a valid port id");
            }

            if (!seen.Add(id))
            {
                throw cursor.ErrorAt(column, $"port '{id}' is listed twice");
            }

            (double x, double y) = ReadPair(cursor, token[(at + 1)..], column + at + 1, ',', "a port position x,y");
            ports.Add(new Port(id, Fraction(cursor, column, x, $"port '{id}' x"), Fraction(cursor, column, y, $"port '{id}' y")));
        }

        return ports;
    }

    private static List<Point> WaypointsValue(LineCursor cursor, Attribute attribute)
    {
        string content = GroupContent(cursor, attribute, "waypoints=[x,y ...]");
        var points = new List<Point>();
        foreach ((string token, int column) in SplitTokens(content, attribute.Value.Column + 1))
        {
            (double x, double y) = ReadPair(cursor, token, column, ',', "a waypoint x,y");
            points.Add(new Point(x, y));
        }

        return points;
    }

    private static string GroupContent(LineCursor cursor, Attribute attribute, string form)
    {
        if (attribute.Value.Kind != ValueKind.Group || attribute.Value.Text[0] != '[')
        {
            throw cursor.ErrorAt(attribute.Value.Column, $"'{attribute.Key}' must be written {form}");
        }

        return attribute.Value.Text[1..^1];
    }

    private NodeStyle ApplyNodeStyle(LineCursor cursor, NodeStyle style, Attribute attribute)
    {
        string field = attribute.Key[DslSyntax.StylePrefix.Length..];
        return field switch
        {
            "fill" => style with { Fill = TextValue(cursor, attribute) },
            "stroke" => style with { Stroke = TextValue(cursor, attribute) },
            "strokeWidth" => style with { StrokeWidth = StrokeWidthValue(cursor, attribute) },
            "textColor" => style with { TextColor = TextValue(cursor, attribute) },
            "font" => style with { Font = TextValue(cursor, attribute) },
            "dash" => style with { Dash = EnumValue<Dash>(cursor, attribute) },
            "fontSize" => style with { FontSize = NonNegativeNumberValue(cursor, attribute) },
            "bold" => style with { Bold = BoolValue(cursor, attribute) },
            _ => throw cursor.ErrorAt(attribute.KeyColumn, $"unknown node style field '{field}'; expected fill, stroke, strokeWidth, textColor, font, dash, fontSize or bold"),
        };
    }

    private EdgeStyle ApplyEdgeStyle(LineCursor cursor, EdgeStyle style, Attribute attribute)
    {
        string field = attribute.Key[DslSyntax.StylePrefix.Length..];
        return field switch
        {
            "stroke" => style with { Stroke = TextValue(cursor, attribute) },
            "strokeWidth" => style with { StrokeWidth = StrokeWidthValue(cursor, attribute) },
            "dash" => style with { Dash = EnumValue<Dash>(cursor, attribute) },
            "arrowStart" => style with { ArrowStart = EnumValue<ArrowHead>(cursor, attribute) },
            "arrowEnd" => style with { ArrowEnd = EnumValue<ArrowHead>(cursor, attribute) },
            _ => throw cursor.ErrorAt(attribute.KeyColumn, $"unknown edge style field '{field}'; expected stroke, strokeWidth, dash, arrowStart or arrowEnd"),
        };
    }

    /// <summary>The nodes as the document will hold them, their default shapes filled in: what an endpoint's column port is checked against.</summary>
    private readonly Dictionary<string, Node> _finalNodes = new(StringComparer.Ordinal);

    /// <summary>Second pass: everything that needs the whole file read first.</summary>
    private Document Build(DocumentType type)
    {
        KindVocabulary.TryGet(type, out KindVocabulary? vocabulary);
        string typeName = DslSyntax.EnumName(type);

        var nodesById = new Dictionary<string, PendingNode>(StringComparer.Ordinal);
        foreach (PendingNode pending in _nodes)
        {
            nodesById[pending.Node.Id] = pending;
        }

        AssignContainerChildren(nodesById);

        var nodes = new List<Node>(_nodes.Count);
        foreach (PendingNode pending in _nodes)
        {
            Node node = pending.Node;
            if (!pending.HasExplicitShape)
            {
                node = node with { Shape = DslSyntax.DefaultShapeFor(type, node.Kind) };
            }

            if (vocabulary is not null && !vocabulary.IsKnownNodeKind(node.Kind))
            {
                _warnings.Add(new DslWarning(pending.Line, pending.KindColumn, $"unknown node kind '{node.Kind}' for {typeName} documents"));
            }

            if (node.Parent is not null && !nodesById.ContainsKey(node.Parent))
            {
                _warnings.Add(new DslWarning(pending.Line, pending.ParentColumn, $"node '{node.Id}' has parent '{node.Parent}', which is not declared"));
            }

            nodes.Add(node);
            _finalNodes[node.Id] = node;
        }

        var edges = new List<Edge>(_edges.Count);
        foreach (PendingEdge pending in _edges)
        {
            if (vocabulary is not null && !vocabulary.IsKnownEdgeKind(pending.Kind))
            {
                _warnings.Add(new DslWarning(pending.Line, pending.KindColumn, $"unknown edge kind '{pending.Kind}' for {typeName} documents"));
            }

            Endpoint from = ResolveEndpoint(pending, pending.From, pending.FromColumn, nodesById);
            Endpoint to = ResolveEndpoint(pending, pending.To, pending.ToColumn, nodesById);
            edges.Add(new Edge(pending.Id, pending.Kind, from, to)
            {
                Label = pending.Label,
                Routing = pending.Routing is { } mode ? new Routing(mode) { Waypoints = pending.Waypoints } : null,
                Style = pending.Style,
                Props = pending.Props,
            });
        }

        Flythrough? flythrough = null;
        if (_keyframes.Count > 0)
        {
            flythrough = new Flythrough(_keyframes) { Loop = _loop };
        }
        else if (_loop is not null)
        {
            _warnings.Add(new DslWarning(_flythroughLine, 1, "'flythrough' has no keyframes, so there is no tour and its settings are ignored"));
        }

        string title = _title ?? string.Empty;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        now = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
        DateTimeOffset createdAt = _createdAt ?? _updatedAt ?? now;
        DateTimeOffset updatedAt = _updatedAt ?? createdAt;

        return new Document(
            _id ?? DslSyntax.IdFromTitle(title),
            Document.CurrentSchemaVersion,
            title,
            type,
            nodes,
            edges,
            new Meta(createdAt, updatedAt, _createdBy ?? DslSyntax.DefaultCreatedBy) { AiProvenance = _provenance })
        {
            Theme = _theme,
            Flythrough = flythrough,
        };
    }

    private void AssignContainerChildren(Dictionary<string, PendingNode> nodesById)
    {
        foreach (PendingChild child in _children)
        {
            if (!nodesById.TryGetValue(child.ChildId, out PendingNode? pending))
            {
                throw new DslParseException(child.Line, child.Column, $"container '{child.ContainerId}' lists '{child.ChildId}', which is not declared as a node");
            }

            if (pending.Node.Parent is { } existing && !string.Equals(existing, child.ContainerId, StringComparison.Ordinal))
            {
                throw new DslParseException(child.Line, child.Column, $"'{child.ChildId}' is listed in container '{child.ContainerId}' but already belongs to '{existing}'");
            }

            PendingNode updated = pending with { Node = pending.Node with { Parent = child.ContainerId } };
            nodesById[child.ChildId] = updated;
            _nodes[_nodes.IndexOf(pending)] = updated;
        }
    }

    /// <summary>
    /// Splits <c>node.port</c>. Because ids may themselves contain dots, the whole token wins when it names a declared
    /// node; otherwise the text after the last dot is the port.
    /// </summary>
    private Endpoint ResolveEndpoint(PendingEdge edge, string token, int column, Dictionary<string, PendingNode> nodesById)
    {
        string nodeId = token;
        string? portId = null;
        if (!nodesById.ContainsKey(token))
        {
            // Longest declared prefix wins, so node n_api with port p.1 is found from "n_api.p.1". With no declared
            // prefix the text after the last dot is the port, provided that leaves two valid ids.
            int dot = token.LastIndexOf('.');
            for (int candidate = dot; candidate > 0; candidate = token.LastIndexOf('.', candidate - 1))
            {
                if (nodesById.ContainsKey(token[..candidate]))
                {
                    dot = candidate;
                    break;
                }
            }

            if (dot > 0 && DslSyntax.IsValidId(token[..dot]) && DslSyntax.IsValidPortId(token[(dot + 1)..]))
            {
                nodeId = token[..dot];
                portId = token[(dot + 1)..];
            }
        }

        // IdPattern admits any length, but a reference is an id: over-long is a hard error wherever one is declared.
        if (!DslSyntax.IsValidId(nodeId))
        {
            throw new DslParseException(edge.Line, column, $"'{nodeId}' is not a valid node reference; an id is 1 to 128 characters");
        }

        if (!nodesById.TryGetValue(nodeId, out PendingNode? target))
        {
            _warnings.Add(new DslWarning(edge.Line, column, $"edge '{edge.Id}' refers to node '{nodeId}', which is not declared"));
        }
        else if (portId is not null && ErShapeLibrary.IsColumnPortId(portId))
        {
            // A column port is derived from the table's rows, never declared; a name no row has attaches the end to the table.
            if (!_finalNodes.TryGetValue(nodeId, out Node? table) || ErShapeLibrary.ColumnRowCentre(table, portId) is null)
            {
                _warnings.Add(new DslWarning(edge.Line, column, $"edge '{edge.Id}' refers to column '{ErShapeLibrary.ColumnPortName(portId) ?? portId}', which node '{nodeId}' does not have; the end attaches to the table"));
            }
        }
        else if (portId is not null && (target.Node.Ports is null || !target.Node.Ports.Any(p => string.Equals(p.Id, portId, StringComparison.Ordinal))))
        {
            _warnings.Add(new DslWarning(edge.Line, column, $"edge '{edge.Id}' refers to port '{portId}', which node '{nodeId}' does not declare"));
        }

        return new Endpoint(nodeId) { Port = portId };
    }

    private sealed record Attribute(string Key, bool QuotedKey, AttributeValue Value, int KeyColumn);

    private sealed record AttributeValue(ValueKind Kind, string Text, int Column);

    private sealed record PendingNode(Node Node, bool HasExplicitShape, int Line, int KindColumn, int ParentColumn);

    private sealed record PendingEdge(
        string Id,
        string Kind,
        string From,
        int FromColumn,
        string To,
        int ToColumn,
        string? Label,
        RoutingMode? Routing,
        List<Point>? Waypoints,
        EdgeStyle? Style,
        Dictionary<string, JsonElement>? Props,
        int Line,
        int KindColumn);

    private sealed record PendingChild(string ContainerId, string ChildId, int Line, int Column);
}
