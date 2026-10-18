using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Draughtsman.Core.Import;

/// <summary>
/// Hand-rolled parser for the Mermaid <c>flowchart</c> / <c>graph</c> subset: header and direction, node declarations with
/// the common shape brackets, links with labels, <c>subgraph … end</c> blocks and <c>%%</c> comments.
/// Anything it cannot read becomes a warning and is skipped; it never throws on input.
/// Mermaid has no formal grammar, so this follows the documented syntax (https://mermaid.js.org/syntax/flowchart.html)
/// rather than the JISON lexer line by line.
/// </summary>
internal static partial class MermaidFlowchartParser
{
    private static readonly string[] s_otherDiagramTypes =
    {
        "classDiagram", "stateDiagram", "stateDiagram-v2", "journey", "gantt", "pie",
        "quadrantChart", "requirementDiagram", "gitGraph", "mindmap", "timeline", "zenuml", "sankey-beta", "xychart-beta",
        "block-beta", "packet-beta", "kanban", "architecture-beta", "C4Context", "C4Container", "C4Component", "C4Dynamic",
        "C4Deployment", "radar-beta", "treemap-beta",
    };

    /// <summary>Statements Mermaid accepts that carry nothing the model keeps. Reported once each, then skipped.</summary>
    private static readonly string[] s_ignoredStatements = { "classDef", "class", "style", "linkStyle", "click", "accTitle", "accDescr" };

    /// <summary>Openers of a "-- text -->" link; the closer that follows the text carries the arrowhead.</summary>
    private static readonly string[] s_textLinkOpeners = { "--", "==", "-." };

    // Shape openers, longest first so "([" is not read as "(" then "[".
    private static readonly (string Open, string[] Close, MermaidShape Shape)[] s_shapes =
    {
        ("(((", new[] { ")))" }, MermaidShape.DoubleCircle),
        ("([", new[] { "])" }, MermaidShape.Stadium),
        ("[[", new[] { "]]" }, MermaidShape.Subroutine),
        ("[(", new[] { ")]" }, MermaidShape.Cylinder),
        ("((", new[] { "))" }, MermaidShape.Circle),
        ("{{", new[] { "}}" }, MermaidShape.Hexagon),
        ("[/", new[] { "/]", "\\]" }, MermaidShape.Parallelogram),
        ("[\\", new[] { "\\]", "/]" }, MermaidShape.Parallelogram),
        (">", new[] { "]" }, MermaidShape.Asymmetric),
        ("[", new[] { "]" }, MermaidShape.Rectangle),
        ("(", new[] { ")" }, MermaidShape.Rounded),
        ("{", new[] { "}" }, MermaidShape.Rhombus),
    };

    public static MermaidGraph Parse(string source)
    {
        var graph = new MermaidGraph();
        try
        {
            ParseCore(source ?? string.Empty, graph);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or RegexMatchTimeoutException))
        {
            // Belt and braces: the statement parsers report their own problems, but a bug here must not take the request down.
            graph.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Import stopped early: {ex.Message}"));
        }

        return graph;
    }

    private static void ParseCore(string source, MermaidGraph graph)
    {
        string[] physical = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        BlankFence(physical);
        int index = ReadFrontMatter(physical, graph);
        List<(string Text, int Line)> lines = JoinQuotedLines(physical, index);

        bool headerSeen = false;
        var subgraphStack = new Stack<SubgraphBlock>();
        var ignoredReported = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string raw, int lineNumber) in lines)
        {
            List<string> statements = SplitStatements(raw);
            if (statements.Count == 0)
            {
                continue;
            }

            if (!headerSeen)
            {
                headerSeen = true;
                if (TryReadHeader(statements[0], lineNumber, graph))
                {
                    statements.RemoveAt(0);
                }
                else if (graph.UnsupportedDiagramType is not null)
                {
                    return;
                }
                else
                {
                    graph.Warnings.Add(new ImportWarning(ImportWarningCodes.MissingHeader,
                        "No 'flowchart' or 'graph' header found; assumed 'flowchart TB'.", lineNumber));
                    graph.Direction ??= "TB";
                }
            }

            foreach (string statement in statements)
            {
                ParseStatement(statement, lineNumber, graph, subgraphStack, ignoredReported);
            }
        }

        if (subgraphStack.Count > 0)
        {
            graph.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError,
                $"{subgraphStack.Count} subgraph(s) were not closed with 'end'; they were closed at end of input.", physical.Length));
            while (subgraphStack.Count > 0)
            {
                subgraphStack.Pop().Close();
            }
        }
    }

    /// <summary>
    /// An open <c>subgraph … end</c> block and the nodes mentioned directly inside it. Membership is settled when the block
    /// closes, as Mermaid does: the first block to close claims each node it mentioned that no earlier block has claimed,
    /// so a nested block wins over its parent and, for sequential blocks, the first one wins.
    /// </summary>
    private sealed class SubgraphBlock
    {
        private readonly List<MermaidNode> _members = new();

        public SubgraphBlock(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public void Mention(MermaidNode node)
        {
            _members.Add(node);
        }

        public void Close()
        {
            foreach (MermaidNode node in _members)
            {
                node.Subgraph ??= Id;
            }
        }
    }

    /// <summary>
    /// Mermaid lets a quoted label span lines, so physical lines are joined while a quote is open.
    /// Each logical line keeps the number of the physical line it started on for warnings.
    /// </summary>
    private static List<(string Text, int Line)> JoinQuotedLines(string[] physical, int start)
    {
        var result = new List<(string, int)>();
        var pending = new StringBuilder();
        int pendingLine = 0;
        int openQuotes = 0;
        for (int i = start; i < physical.Length; i++)
        {
            if (pending.Length == 0)
            {
                pendingLine = i + 1;
            }
            else
            {
                pending.Append('\n');
            }

            pending.Append(physical[i]);
            openQuotes = CountQuotes(physical[i], openQuotes);
            if (openQuotes % 2 == 0)
            {
                result.Add((pending.ToString(), pendingLine));
                pending.Clear();
                openQuotes = 0;
            }
        }

        if (pending.Length > 0)
        {
            // An unterminated quote: hand the fragments over line by line so the statement parser reports them.
            string[] rest = pending.ToString().Split('\n');
            for (int i = 0; i < rest.Length; i++)
            {
                result.Add((rest[i], pendingLine + i));
            }
        }

        return result;
    }

    /// <summary>Adds the line's quotes to a running count, ignoring a '%%' comment that starts outside a quote.</summary>
    private static int CountQuotes(string line, int openQuotes)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (openQuotes % 2 == 0 && line[i] == '%' && i + 1 < line.Length && line[i + 1] == '%')
            {
                break;
            }

            if (line[i] == '"')
            {
                openQuotes++;
            }
        }

        return openQuotes;
    }

    /// <summary>
    /// Accepts a pasted ```mermaid fenced block as well as bare text, because that is how LLMs hand it over.
    /// Fence lines are blanked in place rather than removed so warning line numbers still match the pasted text.
    /// </summary>
    internal static void BlankFence(string[] lines)
    {
        int opener = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (opener < 0 || !lines[opener].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            return;
        }

        lines[opener] = string.Empty;
        int closer = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("```", StringComparison.Ordinal));
        if (closer <= opener)
        {
            return;
        }

        for (int i = closer; i < lines.Length; i++)
        {
            lines[i] = string.Empty;
        }
    }

    /// <summary>Reads the optional YAML front matter (--- title: … ---) and returns the index of the first line after it.</summary>
    internal static int ReadFrontMatter(string[] lines, MermaidGraph graph)
    {
        int first = 0;
        while (first < lines.Length && lines[first].Trim().Length == 0)
        {
            first++;
        }

        if (first >= lines.Length || lines[first].Trim() != "---")
        {
            return 0;
        }

        for (int i = first + 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line == "---")
            {
                return i + 1;
            }

            Match title = FrontMatterTitle().Match(line);
            if (title.Success)
            {
                graph.Title = Unquote(title.Groups["title"].Value.Trim());
            }
        }

        // No closing marker: treat the whole thing as diagram text and let the statement parser complain.
        return 0;
    }

    private static bool TryReadHeader(string line, int lineNumber, MermaidGraph graph)
    {
        Match header = Header().Match(line);
        if (header.Success)
        {
            graph.Direction = NormaliseDirection(header.Groups["dir"].Value);
            return true;
        }

        string firstWord = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        string? other = Array.Find(s_otherDiagramTypes, t => string.Equals(t, firstWord, StringComparison.OrdinalIgnoreCase));
        if (other is not null)
        {
            graph.UnsupportedDiagramType = other;
            graph.Warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedDiagram,
                $"'{other}' diagrams are not supported; only 'flowchart' and 'graph' can be imported.", lineNumber));
        }

        return false;
    }

    private static string NormaliseDirection(string direction)
    {
        return direction.ToUpperInvariant() switch
        {
            "TD" or "TB" or "" => "TB",
            "LR" => "LR",
            "RL" => "RL",
            "BT" => "BT",
            _ => "TB",
        };
    }

    /// <summary>
    /// Splits a line on ';' outside quotes and brackets, which is how Mermaid separates statements on one line.
    /// A '%%' comment outside quotes and brackets ends the line, whether it is the whole line or trails a statement.
    /// </summary>
    private static List<string> SplitStatements(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (!inQuote && depth == 0 && c == '%' && i + 1 < line.Length && line[i + 1] == '%')
            {
                break;
            }

            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && c is '[' or '(' or '{')
            {
                depth++;
            }
            else if (!inQuote && c is ']' or ')' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }

            if (c == ';' && !inQuote && depth == 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    private static void ParseStatement(string statement, int lineNumber, MermaidGraph graph, Stack<SubgraphBlock> subgraphStack, HashSet<string> ignoredReported)
    {
        string keyword = FirstWord(statement);

        if (keyword == "subgraph")
        {
            ParseSubgraph(statement[keyword.Length..].Trim(), lineNumber, graph, subgraphStack);
            return;
        }

        if (keyword == "end" && statement.Length == keyword.Length)
        {
            if (subgraphStack.Count == 0)
            {
                graph.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, "'end' without a matching 'subgraph'; ignored.", lineNumber));
            }
            else
            {
                subgraphStack.Pop().Close();
            }

            return;
        }

        if (SubgraphDirection().IsMatch(statement))
        {
            // Per-subgraph direction is a layout hint the model has no home for yet.
            return;
        }

        if (Header().IsMatch(statement))
        {
            graph.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, "A second diagram header was ignored.", lineNumber));
            return;
        }

        string? ignored = Array.Find(s_ignoredStatements, s => string.Equals(s, keyword, StringComparison.Ordinal));
        if (ignored is not null && IsKeywordStatement(statement, ignored))
        {
            if (ignoredReported.Add(ignored))
            {
                graph.Warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                    $"'{ignored}' statements are not imported; styling and interaction stay in Mermaid.", lineNumber));
            }

            return;
        }

        var scanner = new Scanner(statement, lineNumber, graph, subgraphStack.Count > 0 ? subgraphStack.Peek() : null);
        scanner.ParseChain();
    }

    private static void ParseSubgraph(string rest, int lineNumber, MermaidGraph graph, Stack<SubgraphBlock> subgraphStack)
    {
        string? parent = subgraphStack.Count > 0 ? subgraphStack.Peek().Id : null;
        string id;
        string title;

        Match withTitle = SubgraphWithTitle().Match(rest);
        if (withTitle.Success)
        {
            id = withTitle.Groups["id"].Value;
            title = CleanLabel(withTitle.Groups["title"].Value);
        }
        else if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"')
        {
            title = CleanLabel(rest);
            id = title;
        }
        else if (rest.Length > 0)
        {
            id = rest;
            title = rest;
        }
        else
        {
            id = $"subgraph_{graph.Subgraphs.Count + 1}";
            title = string.Empty;
            graph.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, "'subgraph' without a name; an id was generated.", lineNumber));
        }

        if (!graph.TryAddSubgraph(new MermaidSubgraph(id, title, parent, lineNumber)))
        {
            graph.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Subgraph '{id}' is declared twice; the second block was merged into the first.", lineNumber));
        }

        subgraphStack.Push(new SubgraphBlock(id));
    }

    /// <summary>
    /// Mermaid only reads a keyword when whitespace follows it (or ':' / '{' for accTitle and accDescr), so
    /// "click[Click handler]" is a node called click, not a click statement.
    /// </summary>
    private static bool IsKeywordStatement(string statement, string keyword)
    {
        if (statement.Length == keyword.Length)
        {
            return true;
        }

        char next = statement[keyword.Length];
        return char.IsWhiteSpace(next) || (keyword.StartsWith("acc", StringComparison.Ordinal) && next is ':' or '{');
    }

    private static string FirstWord(string statement)
    {
        int end = 0;
        while (end < statement.Length && (char.IsLetter(statement[end]) || statement[end] == '_'))
        {
            end++;
        }

        return statement[..end];
    }

    /// <summary>Turns Mermaid label text into plain text: quotes, markdown backticks, &lt;br&gt; line breaks and #entity; escapes.</summary>
    internal static string CleanLabel(string text)
    {
        string value = Unquote(text.Trim());
        if (value.Length >= 2 && value[0] == '`' && value[^1] == '`')
        {
            value = value[1..^1];
        }

        value = LineBreakTag().Replace(value, "\n");
        value = Entity().Replace(value, m =>
        {
            string name = m.Groups["name"].Value;
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int code) && code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
            {
                return char.ConvertFromUtf32(code);
            }

            return name switch
            {
                "quot" => "\"",
                "amp" => "&",
                "lt" => "<",
                "gt" => ">",
                "nbsp" => " ",
                _ => m.Value,
            };
        });

        return string.Join('\n', value.Split('\n').Select(l => l.Trim()));
    }

    private static string Unquote(string text)
    {
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
    }

    [GeneratedRegex(@"^(?:flowchart(?:-elk)?|graph)(?:\s+(?<dir>TB|TD|BT|RL|LR))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex Header();

    [GeneratedRegex(@"^direction\s+(?i:TB|TD|BT|RL|LR)\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex SubgraphDirection();

    [GeneratedRegex(@"^title\s*:\s*(?<title>.+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex FrontMatterTitle();

    [GeneratedRegex(@"^(?<id>[^\s\[""]+)\s*\[(?<title>[^\]]*)\]\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex SubgraphWithTitle();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"#(?<name>[A-Za-z0-9]+);", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex Entity();

    // A node id: letters, digits, underscore and dot. A hyphen is allowed only between such characters and never before a dot,
    // so "A-->B" reads as A, -->, B and "A-.->B" reads as A, -.->, B.
    [GeneratedRegex(@"\G[\p{L}\p{N}_.]+(?:-[\p{L}\p{N}_][\p{L}\p{N}_.]*)*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex NodeId();

    // "-->" style links, optionally followed by |text|. Two bare dashes or equals with no head are not a link (that is the opener above).
    [GeneratedRegex(@"\G(?<start>[<xo](?=[-=]))?(?<body>-{2,}|={2,}|-\.+-)(?<head>[>xo])?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex ClosedLink();

    /// <summary>Walks one statement: a node group, then zero or more (link, node group) pairs.</summary>
    private sealed class Scanner
    {
        private readonly string _text;
        private readonly int _line;
        private readonly MermaidGraph _graph;
        private readonly SubgraphBlock? _subgraph;
        private int _pos;

        public Scanner(string text, int line, MermaidGraph graph, SubgraphBlock? subgraph)
        {
            _text = text;
            _line = line;
            _graph = graph;
            _subgraph = subgraph;
        }

        public void ParseChain()
        {
            List<string>? left = ParseNodeGroup();
            if (left is null)
            {
                Fail("Expected a node id.");
                return;
            }

            while (true)
            {
                SkipWhitespace();
                if (_pos >= _text.Length)
                {
                    return;
                }

                if (!TryParseLink(out string? label, out MermaidLine lineStyle, out MermaidHead headStart, out MermaidHead headEnd))
                {
                    Fail("Expected a link such as '-->' after the node.");
                    return;
                }

                List<string>? right = ParseNodeGroup();
                if (right is null)
                {
                    Fail("Expected a node id after the link.");
                    return;
                }

                foreach (string from in left)
                {
                    foreach (string to in right)
                    {
                        _graph.Edges.Add(new MermaidEdge(from, to, label, lineStyle, headStart, headEnd, _line));
                    }
                }

                left = right;
            }
        }

        private void Fail(string why)
        {
            string remainder = _text[Math.Min(_pos, _text.Length)..].Trim();
            string context = remainder.Length > 40 ? remainder[..40] + "…" : remainder;
            _graph.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError,
                $"{why} Skipped the rest of the line at '{context}'.", _line));
        }

        /// <summary>One or more nodes joined by '&amp;'. Returns their ids, or null when no node starts here.</summary>
        private List<string>? ParseNodeGroup()
        {
            var ids = new List<string>();
            while (true)
            {
                SkipWhitespace();
                string? id = ParseNode();
                if (id is null)
                {
                    return ids.Count == 0 ? null : ids;
                }

                ids.Add(id);
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == '&')
                {
                    _pos++;
                    continue;
                }

                return ids;
            }
        }

        private string? ParseNode()
        {
            Match id = NodeId().Match(_text, _pos);
            if (!id.Success)
            {
                return null;
            }

            _pos += id.Length;
            MermaidNode node = GetOrAddNode(id.Value);

            SkipWhitespace();
            foreach ((string open, string[] closers, MermaidShape shape) in s_shapes)
            {
                if (!Matches(open))
                {
                    continue;
                }

                int start = _pos + open.Length;
                if (!TryReadShapeText(start, closers, out string text, out int end))
                {
                    _graph.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError,
                        $"Node '{node.Id}' opens a shape with '{open}' that is never closed; its label was left as the id.", _line, null));
                    _pos = _text.Length;
                    return node.Id;
                }

                node.Label = CleanLabel(text);
                node.Shape = shape;
                _pos = end;
                break;
            }

            // ":::className" attaches a style class, which the model does not keep.
            if (Matches(":::"))
            {
                _pos += 3;
                Match cls = NodeId().Match(_text, _pos);
                if (cls.Success)
                {
                    _pos += cls.Length;
                }
            }

            return node.Id;
        }

        /// <summary>Reads shape text up to the first closer, honouring a fully quoted label so a closer inside quotes does not end it.</summary>
        private bool TryReadShapeText(int start, string[] closers, out string text, out int end)
        {
            int cursor = start;
            while (cursor < _text.Length && _text[cursor] == ' ')
            {
                cursor++;
            }

            if (cursor < _text.Length && _text[cursor] == '"')
            {
                int closeQuote = _text.IndexOf('"', cursor + 1);
                if (closeQuote > 0)
                {
                    int afterQuote = closeQuote + 1;
                    while (afterQuote < _text.Length && _text[afterQuote] == ' ')
                    {
                        afterQuote++;
                    }

                    foreach (string closer in closers)
                    {
                        if (string.CompareOrdinal(_text, afterQuote, closer, 0, closer.Length) == 0)
                        {
                            text = _text[(cursor + 1)..closeQuote];
                            end = afterQuote + closer.Length;
                            return true;
                        }
                    }
                }
            }

            for (int at = start; at < _text.Length; at++)
            {
                foreach (string closer in closers)
                {
                    if (Matches(closer, at))
                    {
                        text = _text[start..at];
                        end = at + closer.Length;
                        return true;
                    }
                }
            }

            text = string.Empty;
            end = start;
            return false;
        }

        /// <summary>
        /// A closed link ("-->", "---", "-.->") is read before the "-- text -->" form, as Mermaid's lexer does, so a long
        /// rail such as "-----" stays an unlabelled link rather than becoming text between two openers.
        /// </summary>
        private bool TryParseLink(out string? label, out MermaidLine lineStyle, out MermaidHead headStart, out MermaidHead headEnd)
        {
            label = null;
            Match closed = ClosedLink().Match(_text, _pos);
            string body = closed.Groups["body"].Value;
            string head = closed.Groups["head"].Value;
            if (!closed.Success || (head.Length == 0 && body is "--" or "=="))
            {
                return TryParseTextLink(out label, out lineStyle, out headStart, out headEnd);
            }

            lineStyle = LineStyleOf(body);
            headStart = HeadOf(closed.Groups["start"].Value, isStart: true);
            headEnd = HeadOf(head, isStart: false);
            _pos += closed.Length;

            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '|')
            {
                int closeBar = FindLabelBar(_pos + 1);
                if (closeBar < 0)
                {
                    _graph.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, "Link label opened with '|' but never closed; the label was dropped.", _line));
                    _pos = _text.Length;
                    return true;
                }

                label = CleanLabel(_text[(_pos + 1)..closeBar]);
                _pos = closeBar + 1;
            }

            return true;
        }

        /// <summary>
        /// "-- text -->" links: an optional start head, an opener, free text, then a closer of the same family carrying the
        /// end head. Whitespace around the text is optional, as in Mermaid, so "A--Yes-->B" is a labelled link. Scanned by hand
        /// in one pass because a lazy regex over the free text backtracks quadratically on long lines.
        /// </summary>
        private bool TryParseTextLink(out string? label, out MermaidLine lineStyle, out MermaidHead headStart, out MermaidHead headEnd)
        {
            label = null;
            lineStyle = MermaidLine.Solid;
            headStart = MermaidHead.None;
            headEnd = MermaidHead.None;

            int cursor = _pos;
            string start = string.Empty;
            if (cursor < _text.Length && _text[cursor] is '<' or 'x' or 'o')
            {
                start = _text.Substring(cursor, 1);
                cursor++;
            }

            string? opener = Array.Find(s_textLinkOpeners, o => Matches(o, cursor));
            if (opener is null)
            {
                return false;
            }

            int textStart = cursor + opener.Length;
            while (textStart < _text.Length && char.IsWhiteSpace(_text[textStart]))
            {
                textStart++;
            }

            if (textStart >= _text.Length)
            {
                return false;
            }

            // Each candidate position is examined once: a failed closer reports how far it scanned so the loop
            // skips the rest of that rail run, keeping the pass linear on long runs.
            for (int at = textStart + 1; at < _text.Length; at++)
            {
                if (TryReadTextLinkCloser(opener, at, out string head, out int end, out int scanned))
                {
                    lineStyle = LineStyleOf(opener);
                    headStart = HeadOf(start, isStart: true);
                    headEnd = HeadOf(head, isStart: false);
                    label = CleanLabel(_text[textStart..at]);
                    _pos = end;
                    return true;
                }

                at = Math.Max(at, scanned - 1);
            }

            return false;
        }

        /// <summary>
        /// A text-link closer matching the opener: "-->" / "==>" (two or more rails with a head), "---" / "===" (three or more
        /// without), or ".->" / ".-" after a "-." opener. <paramref name="scanned"/> is the position after the rails examined,
        /// whether or not they formed a closer.
        /// </summary>
        private bool TryReadTextLinkCloser(string opener, int at, out string head, out int end, out int scanned)
        {
            head = string.Empty;
            end = at;
            scanned = at + 1;
            bool dotted = opener == "-.";
            char rail = dotted ? '-' : opener[0];
            if (_text[at] != (dotted ? '.' : rail))
            {
                return false;
            }

            int cursor = dotted ? at + 1 : at;
            while (cursor < _text.Length && _text[cursor] == rail)
            {
                cursor++;
            }

            scanned = cursor;
            int rails = cursor - (dotted ? at + 1 : at);
            bool hasHead = cursor < _text.Length && _text[cursor] is '>' or 'x' or 'o';
            int needed = dotted ? 1 : hasHead ? 2 : 3;
            if (rails < needed)
            {
                return false;
            }

            if (hasHead)
            {
                head = _text.Substring(cursor, 1);
                cursor++;
            }

            end = cursor;
            return true;
        }

        /// <summary>Finds the closing '|' of a link label, skipping one that sits inside a quoted label.</summary>
        private int FindLabelBar(int from)
        {
            int cursor = from;
            while (cursor < _text.Length && _text[cursor] == ' ')
            {
                cursor++;
            }

            if (cursor < _text.Length && _text[cursor] == '"')
            {
                int closeQuote = _text.IndexOf('"', cursor + 1);
                if (closeQuote > 0)
                {
                    int bar = _text.IndexOf('|', closeQuote + 1);
                    if (bar > 0 && _text[(closeQuote + 1)..bar].Trim().Length == 0)
                    {
                        return bar;
                    }
                }
            }

            return _text.IndexOf('|', from);
        }

        private static MermaidLine LineStyleOf(string body)
        {
            if (body.StartsWith('='))
            {
                return MermaidLine.Thick;
            }

            return body.Contains('.', StringComparison.Ordinal) ? MermaidLine.Dotted : MermaidLine.Solid;
        }

        private static MermaidHead HeadOf(string marker, bool isStart)
        {
            return marker switch
            {
                ">" => MermaidHead.Arrow,
                "<" when isStart => MermaidHead.Arrow,
                "o" => MermaidHead.Circle,
                "x" => MermaidHead.Cross,
                _ => MermaidHead.None,
            };
        }

        private MermaidNode GetOrAddNode(string id)
        {
            MermaidNode node = _graph.GetOrAddNode(id, _line);
            _subgraph?.Mention(node);
            return node;
        }

        private bool Matches(string token)
        {
            return Matches(token, _pos);
        }

        private bool Matches(string token, int at)
        {
            return string.CompareOrdinal(_text, at, token, 0, token.Length) == 0;
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }
    }
}
