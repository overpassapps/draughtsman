using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Import;

/// <summary>
/// Imports a Mermaid <c>erDiagram</c> into an <c>architecture</c> (or <c>freeform</c>) document of ER entities (docs/er-entities.md),
/// the mirror of <see cref="MermaidExporter"/>'s entity output and of the SQL importer's shapes: an entity block becomes a
/// <c>custom:er/entity</c> node whose <c>props.columns</c> hold one <c>[PK] [FK] name type</c> line per attribute (Mermaid has no
/// nullability, so none is written), an entity only named in a relationship is created empty, <c>NAME["Display"]</c> gives the label,
/// and each relationship line becomes an <c>association</c> edge whose <c>arrowStart</c> and <c>arrowEnd</c> are the crow's-foot ends
/// (<c>--</c> identifying is solid, <c>..</c> non-identifying is dashed). A relationship joins table to table: Mermaid has no column-level
/// relationships. A tolerant reader in the manner of the SQL importer: what it cannot carry (<c>UK</c>, an attribute comment, styling
/// statements) is one <c>unsupported-syntax</c> warning per construct and a line it cannot read is a <c>parse-error</c> warning; later
/// lines still import, and it never throws on input.
/// </summary>
internal static partial class MermaidErImporter
{
    private const string DefaultTitle = "Imported diagram";
    private const string CreatedBy = "mermaid-import";
    private const string EntityKind = "entity";
    private const int MaxIdLength = 128;

    private const string Name = "\"[^\"]*\"|[A-Za-z_][A-Za-z0-9_\\-]*";

    /// <summary>Statements Mermaid accepts that carry nothing the model keeps: reported once each, then skipped.</summary>
    private static readonly string[] s_unsupportedStatements = { "classDef", "class", "style", "click", "accTitle", "accDescr", "link", "callback" };

    /// <summary>True when the first meaningful line, after a pasted code fence, front matter, blanks and comments, is the <c>erDiagram</c> header.</summary>
    public static bool IsErDiagram(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        string[] physical = Prepare(source, out _, out int start);
        for (int i = start; i < physical.Length; i++)
        {
            string line = physical[i].Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal))
            {
                continue;
            }

            return IsHeader(line);
        }

        return false;
    }

    public static ImportResult Import(string source, MermaidImportOptions options)
    {
        var warnings = new List<ImportWarning>();
        var entities = new List<Entity>();
        var relationships = new List<Relationship>();
        string? title = null;
        string? direction = null;
        try
        {
            string[] physical = Prepare(source ?? string.Empty, out MermaidGraph front, out int start);
            title = front.Title;
            ParseLines(physical, start, new ParseState(entities, relationships, warnings), ref direction);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or RegexMatchTimeoutException))
        {
            // Belt and braces, as in the flowchart parser: a bug here must not take the request down.
            warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Import stopped early: {ex.Message}"));
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<Node>(entities.Count);
        var nodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Entity entity in entities)
        {
            string id = AllocateId(used, entity.Name);
            nodeIds[entity.Name] = id;
            var node = new Node(id, EntityKind, entity.Label ?? entity.Name, ErShapeLibrary.Prefix + EntityKind);
            if (entity.Columns.Count > 0)
            {
                node = node with { Props = new Dictionary<string, JsonElement> { [ErShapeLibrary.ColumnsProp] = JsonSerializer.SerializeToElement(entity.Columns) } };
            }

            nodes.Add(node);
        }

        var edges = new List<Edge>(relationships.Count);
        foreach (Relationship relationship in relationships)
        {
            edges.Add(new Edge($"e_{edges.Count + 1}", "association", new Endpoint(nodeIds[relationship.From]), new Endpoint(nodeIds[relationship.To]))
            {
                Label = string.IsNullOrWhiteSpace(relationship.Label) ? null : relationship.Label,
                Routing = new Routing(RoutingMode.Orthogonal),
                Style = new EdgeStyle
                {
                    Dash = relationship.Identifying ? null : Dash.Dashed,
                    ArrowStart = relationship.Start,
                    ArrowEnd = relationship.End,
                },
            });
        }

        if (nodes.Count == 0)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.EmptyDiagram, "The source produced no entities."));
        }

        DateTimeOffset now = options.Now ?? DateTimeOffset.UtcNow;
        var document = new Document(
            $"doc_{Guid.NewGuid():N}",
            Document.CurrentSchemaVersion,
            options.Title ?? title ?? DefaultTitle,
            options.Type,
            nodes,
            edges,
            new Meta(now, now, CreatedBy));

        return new ImportResult(document, warnings) { Direction = direction };
    }

    private static string[] Prepare(string source, out MermaidGraph front, out int start)
    {
        string[] physical = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        MermaidFlowchartParser.BlankFence(physical);
        front = new MermaidGraph();
        start = MermaidFlowchartParser.ReadFrontMatter(physical, front);
        return physical;
    }

    private static bool IsHeader(string line)
    {
        return string.Equals(line.TrimEnd(';').Trim(), "erDiagram", StringComparison.OrdinalIgnoreCase);
    }

    private static void ParseLines(string[] physical, int start, ParseState state, ref string? direction)
    {
        bool headerSeen = false;
        Entity? open = null;
        for (int i = start; i < physical.Length; i++)
        {
            int lineNumber = i + 1;
            string line = physical[i].Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal))
            {
                continue;
            }

            if (!headerSeen)
            {
                headerSeen = true;
                if (IsHeader(line))
                {
                    continue;
                }

                // Only reachable when Import is called directly; the importer's dispatch checked the header already.
                state.Warnings.Add(new ImportWarning(ImportWarningCodes.MissingHeader, "No 'erDiagram' header found; assumed one.", lineNumber));
            }

            line = line.TrimEnd(';').TrimEnd();
            if (open is not null)
            {
                if (line == "}")
                {
                    open = null;
                }
                else
                {
                    ReadAttribute(line, lineNumber, open, state);
                }

                continue;
            }

            open = ReadStatement(line, lineNumber, state, ref direction);
        }

        if (open is not null)
        {
            state.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"The block for '{open.Name}' was never closed with '}}'; it was closed at the end.", null));
        }
    }

    /// <summary>Reads one statement outside a block; returns the entity whose block it opened, if it did.</summary>
    private static Entity? ReadStatement(string line, int lineNumber, ParseState state, ref string? direction)
    {
        Match relationship = RelationshipLine().Match(line);
        if (relationship.Success)
        {
            ReadRelationship(relationship, state);
            return null;
        }

        Match block = BlockOpen().Match(line);
        if (block.Success)
        {
            Entity entity = state.Declare(Unquote(block.Groups["name"].Value), block.Groups["alias"].Success ? Unquote(block.Groups["alias"].Value) : null);
            if (block.Groups["empty"].Success)
            {
                return null;
            }

            if (entity.Defined)
            {
                state.Warnings.Add(new ImportWarning(ImportWarningCodes.DuplicateTable, $"Entity '{entity.Name}' is defined more than once; the first definition was kept.", lineNumber));
                return new Entity(entity.Name) { Discard = true };
            }

            entity.Defined = true;
            return entity;
        }

        Match declaration = BareEntity().Match(line);
        if (declaration.Success)
        {
            state.Declare(Unquote(declaration.Groups["name"].Value), declaration.Groups["alias"].Success ? Unquote(declaration.Groups["alias"].Value) : null);
            return null;
        }

        Match directionLine = DirectionLine().Match(line);
        if (directionLine.Success)
        {
            direction = directionLine.Groups["dir"].Value.ToUpperInvariant() switch { "TD" or "TB" => "TB", var d => d };
            return null;
        }

        string firstWord = line.Split(' ', 2)[0];
        if (Array.Exists(s_unsupportedStatements, s => string.Equals(s, firstWord, StringComparison.Ordinal)))
        {
            state.Warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax, $"'{firstWord}' is not carried into the model; the line was skipped.", lineNumber));
            return null;
        }

        state.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Could not read '{Shorten(line)}'; the line was skipped.", lineNumber));
        return null;
    }

    private static void ReadRelationship(Match match, ParseState state)
    {
        Entity from = state.Declare(Unquote(match.Groups["a"].Value), null);
        Entity to = state.Declare(Unquote(match.Groups["b"].Value), null);
        string label = match.Groups["label"].Success ? Unquote(match.Groups["label"].Value.Trim()) : string.Empty;
        state.Relationships.Add(new Relationship(from.Name, to.Name, match.Groups["line"].Value == "--", HeadFor(match.Groups["l"].Value), HeadFor(match.Groups["r"].Value), label));
    }

    /// <summary>The crow's-foot end for one of Mermaid's cardinality tokens; either side's spelling of the same cardinality is the same end.</summary>
    private static ArrowHead HeadFor(string token)
    {
        return token switch
        {
            "||" => ArrowHead.OneAndOnlyOne,
            "|o" or "o|" => ArrowHead.ZeroOrOne,
            "}o" or "o{" => ArrowHead.ZeroOrMany,
            _ => ArrowHead.OneOrMany,
        };
    }

    private static void ReadAttribute(string line, int lineNumber, Entity entity, ParseState state)
    {
        Match attribute = AttributeLine().Match(line);
        if (!attribute.Success)
        {
            state.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Could not read the attribute '{Shorten(line)}' of '{entity.Name}'; the line was skipped.", lineNumber));
            return;
        }

        var dropped = new List<string>();
        var markers = new List<string>();
        if (attribute.Groups["keys"].Success)
        {
            foreach (string key in attribute.Groups["keys"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string upper = key.ToUpperInvariant();
                if (upper is "PK" or "FK")
                {
                    if (!markers.Contains(upper))
                    {
                        markers.Add(upper);
                    }
                }
                else
                {
                    dropped.Add(upper);
                }
            }
        }

        if (attribute.Groups["comment"].Success && attribute.Groups["comment"].Value.Length > 0)
        {
            dropped.Add("the comment");
        }

        if (dropped.Count > 0)
        {
            state.Warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                $"{entity.Name}.{attribute.Groups["name"].Value}: {string.Join(" and ", dropped)} is not carried into the model; the column was imported without it.", lineNumber));
        }

        if (entity.Discard)
        {
            return;
        }

        // PK before FK, as the column grammar reads them; the exporter writes them in that order too.
        var column = new StringBuilder();
        foreach (string marker in new[] { "PK", "FK" }.Where(markers.Contains))
        {
            column.Append(marker).Append(' ');
        }

        column.Append(attribute.Groups["name"].Value).Append(' ').Append(attribute.Groups["type"].Value);
        entity.Columns.Add(column.ToString());
    }

    /// <summary>A name as the document keeps it: the quotes come off and Mermaid's <c>#quot;</c> escape is a double quote.</summary>
    private static string Unquote(string text)
    {
        string inner = text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
        return inner.Replace("#quot;", "\"", StringComparison.Ordinal);
    }

    private static string Shorten(string line)
    {
        return line.Length <= 60 ? line : line[..60] + "…";
    }

    /// <summary>An id in the SQL importer's shape: <c>t_</c> plus the name with everything outside letters, digits and underscore replaced, unique in the document.</summary>
    private static string AllocateId(HashSet<string> used, string name)
    {
        var text = new StringBuilder("t_");
        foreach (char c in name)
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

    // `A ||--o{ B : "label"`: the left token is the from end, the right token the to end. Spaces around the tokens are optional.
    [GeneratedRegex("^(?<a>" + Name + @")\s*(?<l>\|\||\|o|\}o|\}\|)(?<line>--|\.\.)(?<r>\|\||o\||o\{|\|\{)\s*(?<b>" + Name + @")(?:\s*:\s*(?<label>.*))?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex RelationshipLine();

    [GeneratedRegex("^(?<name>" + Name + @")(?:\s*\[\s*""?(?<alias>[^""\]]*?)""?\s*\])?\s*\{(?<empty>\s*\})?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex BlockOpen();

    [GeneratedRegex("^(?<name>" + Name + @")(?:\s*\[\s*""?(?<alias>[^""\]]*?)""?\s*\])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex BareEntity();

    [GeneratedRegex(@"^direction\s+(?<dir>TB|TD|BT|LR|RL)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex DirectionLine();

    // `type name [PK, FK, UK] ["comment"]`; a type may carry a size (`varchar(10)`) or brackets (`string[]`).
    [GeneratedRegex(@"^(?<type>[A-Za-z_][A-Za-z0-9_\-\[\]]*(?:\([^)]*\))?)\s+(?<name>[A-Za-z_\*][A-Za-z0-9_\-\[\]\(\)]*)(?:\s+(?<keys>(?:PK|FK|UK)(?:\s*,\s*(?:PK|FK|UK))*))?(?:\s+""(?<comment>[^""]*)"")?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex AttributeLine();

    private sealed class Entity(string name)
    {
        public string Name { get; } = name;

        public string? Label { get; set; }

        public bool Defined { get; set; }

        /// <summary>A repeated block: its attributes are still read (and warned about) but go nowhere.</summary>
        public bool Discard { get; init; }

        public List<string> Columns { get; } = [];
    }

    private sealed record Relationship(string From, string To, bool Identifying, ArrowHead Start, ArrowHead End, string Label);

    private sealed class ParseState(List<Entity> entities, List<Relationship> relationships, List<ImportWarning> warnings)
    {
        private readonly Dictionary<string, Entity> _byName = new(StringComparer.Ordinal);

        public List<Relationship> Relationships { get; } = relationships;

        public List<ImportWarning> Warnings { get; } = warnings;

        /// <summary>The entity of that name, created (empty) on first mention; an alias, when one is given, becomes its label.</summary>
        public Entity Declare(string name, string? alias)
        {
            if (!_byName.TryGetValue(name, out Entity? entity))
            {
                entity = new Entity(name);
                _byName[name] = entity;
                entities.Add(entity);
            }

            if (!string.IsNullOrWhiteSpace(alias))
            {
                entity.Label ??= alias.Trim();
            }

            return entity;
        }
    }
}
