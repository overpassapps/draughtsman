using System.Text;
using System.Text.RegularExpressions;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Import;

/// <summary>Mermaid text for a diagram, and what it could not carry.</summary>
/// <param name="Text">The Mermaid source; every warning is also in it as a <c>%%</c> comment beside what it is about.</param>
/// <param name="Warnings">Shapes and arrowheads that have no Mermaid spelling and were drawn as the nearest plain one.</param>
public sealed record MermaidExportResult(string Text, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes a document as Mermaid, the text people paste into READMEs: the mirror of <see cref="MermaidImporter"/>. A
/// <c>sequence</c> document becomes a <c>sequenceDiagram</c> (participants in node order with <c>as</c> aliases, then the
/// messages in edge order, <c>+</c>/<c>-</c> for activation bars); anything else becomes a <c>flowchart</c> (Mermaid shape
/// syntax where one exists, <c>subgraph</c> for a container, a link per edge); a document made only of <c>custom:er</c>
/// entities becomes an <c>erDiagram</c> (see <see cref="WriteEntityRelationship"/>). No layout is exported: Mermaid lays the
/// picture out itself, so geometry, routing and colours are dropped silently. What has no Mermaid equivalent (a shape, an
/// arrowhead) degrades to the nearest plain one with a <c>%%</c> comment that says what was lost, never an error.
/// </summary>
/// <remarks>
/// Milestone 2 of the sequence type (docs/sequence-diagrams.md) is written too: a fragment becomes an <c>alt</c>/<c>else</c>,
/// <c>opt</c>, <c>loop</c>, <c>par</c>/<c>and</c>, <c>critical</c>/<c>option</c> or <c>break</c> block around its messages (guard
/// brackets dropped), a note a <c>Note over</c>, a <c>create</c> message a <c>create participant</c> before it and a
/// <c>destroy</c> message a <c>destroy</c> before its <c>-x</c>. A fragment whose operator Mermaid has no block for is flattened
/// with a warning, and so is a second operand of an operator that has no continuation word.
/// </remarks>
public static class MermaidExporter
{
    private const string Indent = "    ";

    /// <summary>Words Mermaid reads as syntax wherever they stand alone, so they cannot be a node or participant id.</summary>
    private static readonly HashSet<string> s_reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "end", "participant", "actor", "activate", "deactivate", "autonumber", "title", "note", "loop", "alt", "else", "opt",
        "par", "and", "option", "critical", "break", "rect", "box", "create", "destroy", "link", "links", "properties",
        "details", "over", "left", "right", "of", "subgraph", "graph", "flowchart", "direction", "style", "class",
        "classdef", "linkstyle", "click", "default", "call", "href", "callback", "accdescr", "acctitle",
    };

    public static MermaidExportResult Export(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var warnings = new List<string>();
        HashSet<string> legend = TagLegendIds(document);
        string text = document.Type == DocumentType.Sequence ? WriteSequence(document, warnings)
            : IsEntityDocument(document, legend) ? WriteEntityRelationship(document, legend, warnings)
            : WriteFlowchart(document, warnings);

        // StringBuilder.AppendLine writes the platform's newline; the text is pasted anywhere, so it is always "\n".
        return new MermaidExportResult(text.ReplaceLineEndings("\n"), warnings);
    }

    #region Entity relationship

    private static readonly Regex s_notWordChar = new("[^A-Za-z0-9_\\-\\[\\]()]", RegexOptions.Compiled, RegexLimits.Timeout);
    private static readonly Regex s_commaGap = new("\\s*,\\s*", RegexOptions.Compiled, RegexLimits.Timeout);
    private static readonly Regex s_gap = new("\\s+", RegexOptions.Compiled, RegexLimits.Timeout);
    private static readonly Regex s_plainEntityName = new("^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.Compiled, RegexLimits.Timeout);

    /// <summary>
    /// The ids of a tag legend (<c>SqlImporter</c>'s "Legend": a container of swatch notes), which is decoration on an ER
    /// document: an edge-less container whose children are all edge-less notes, and those children. Empty when there is none.
    /// </summary>
    private static HashSet<string> TagLegendIds(Document document)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var wired = new HashSet<string>(document.Edges.SelectMany(e => new[] { e.From.Node, e.To.Node }), StringComparer.Ordinal);
        foreach (Node container in document.Nodes.Where(n => n.Kind == "container" && !wired.Contains(n.Id) && n.Parent is null))
        {
            var children = document.Nodes.Where(n => n.Parent == container.Id).ToList();
            if (children.Count > 0 && children.All(c => c.Kind == "note" && !wired.Contains(c.Id)))
            {
                ids.Add(container.Id);
                ids.UnionWith(children.Select(c => c.Id));
            }
        }

        return ids;
    }

    private static bool IsEntity(Node node)
    {
        return ErShapeLibrary.IsErShape(node.Shape) || node.Kind is "entity" or "weak-entity";
    }

    /// <summary>
    /// True when every node is an ER entity (and there is at least one) and every edge joins two of them: the only
    /// document Mermaid's <c>erDiagram</c> can carry. A document that mixes entities with anything else keeps degrading
    /// to a <c>flowchart</c> with its usual <c>%%</c> comments, so the lossy result is never dressed up as an ER diagram.
    /// </summary>
    private static bool IsEntityDocument(Document document, HashSet<string> legend)
    {
        var entities = document.Nodes.Where(n => !legend.Contains(n.Id)).ToList();
        if (entities.Count == 0 || !entities.All(IsEntity))
        {
            return false;
        }

        var ids = new HashSet<string>(entities.Select(n => n.Id), StringComparer.Ordinal);
        return document.Edges.All(e => ids.Contains(e.From.Node) && ids.Contains(e.To.Node));
    }

    /// <summary>
    /// Writes entities as <c>erDiagram</c> blocks (one attribute per column: <c>type name PK, FK</c>) and each edge as a
    /// relationship whose two cardinality ends come from the crow's-foot ends (<c>arrowStart</c> the from end,
    /// <c>arrowEnd</c> the to end). Nullability, column ports, geometry and the weak entity's double border have no
    /// Mermaid spelling and are dropped; the last two, and an end with no crow's foot, are noted with a <c>%%</c> comment.
    /// </summary>
    private static string WriteEntityRelationship(Document document, HashSet<string> legend, List<string> warnings)
    {
        var script = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(document.Title))
        {
            script.AppendLine("---");
            script.Append("title: ").AppendLine(FrontMatterValue(document.Title));
            script.AppendLine("---");
        }

        script.AppendLine("erDiagram");
        if (legend.Count > 0)
        {
            // Decoration, not content: noted for a reader of the text but not counted as something lost.
            script.Append(Indent).AppendLine("%% the tag colour legend is not exported");
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes.Where(n => !legend.Contains(n.Id)))
        {
            string label = string.IsNullOrWhiteSpace(node.Label) ? node.Id : node.Label.ReplaceLineEndings(" ").Trim();
            string unique = label;
            for (int n = 2; !used.Add(unique); n++)
            {
                unique = $"{label} {n}";
            }

            names[node.Id] = s_plainEntityName.IsMatch(unique) ? unique : $"\"{unique.Replace("\"", "#quot;", StringComparison.Ordinal)}\"";
        }

        foreach (Node node in document.Nodes.Where(n => !legend.Contains(n.Id)))
        {
            if (ErShapeLibrary.TryGet(node.Shape, out ErShape? er) && er!.Double || node.Kind == "weak-entity")
            {
                Warn(script, warnings, $"{node.Id}: a weak entity's double border has no Mermaid equivalent; drawn as a plain entity");
            }

            script.Append(Indent).Append(names[node.Id]).AppendLine(" {");
            foreach (string line in ErShapeLibrary.ColumnLines(node.Props))
            {
                ErColumn column = ErShapeLibrary.ParseColumn(line);
                script.Append(Indent).Append(Indent).Append(AttributeType(column.Type)).Append(' ').Append(AttributeName(column.Name));
                if (column.Keys.Count > 0)
                {
                    script.Append(' ').Append(string.Join(", ", column.Keys));
                }

                script.AppendLine();
            }

            script.Append(Indent).AppendLine("}");
        }

        foreach (Edge edge in document.Edges)
        {
            string left = Cardinality(edge.Style?.ArrowStart, isRight: false, edge, "start", script, warnings);
            string right = Cardinality(edge.Style?.ArrowEnd, isRight: true, edge, "end", script, warnings);
            bool dashed = (edge.Style?.Dash ?? Dash.Solid) != Dash.Solid;
            string label = string.IsNullOrWhiteSpace(edge.Label) ? "" : edge.Label.ReplaceLineEndings(" ").Trim().Replace("\"", "#quot;", StringComparison.Ordinal);
            script.Append(Indent).Append(names[edge.From.Node]).Append(' ').Append(left).Append(dashed ? ".." : "--").Append(right).Append(' ')
                .Append(names[edge.To.Node]).Append(" : \"").Append(label).AppendLine("\"");
        }

        return script.ToString();
    }

    /// <summary>Mermaid's cardinality token for one crow's-foot end; an end that is not one (or is absent) is exactly one, with a comment.</summary>
    private static string Cardinality(ArrowHead? head, bool isRight, Edge edge, string which, StringBuilder script, List<string> warnings)
    {
        switch (head)
        {
            case ArrowHead.One or ArrowHead.OneAndOnlyOne:
                return "||";
            case ArrowHead.ZeroOrOne:
                return isRight ? "o|" : "|o";
            case ArrowHead.OneOrMany:
                return isRight ? "|{" : "}|";
            case ArrowHead.Many or ArrowHead.ZeroOrMany:
                return isRight ? "o{" : "}o";
            default:
                Warn(script, warnings, $"{edge.Id}: the {which} has no crow's-foot end; written as exactly one");
                return "||";
        }
    }

    private static void Warn(StringBuilder script, List<string> warnings, string message)
    {
        warnings.Add(message);
        script.Append(Indent).Append("%% ").AppendLine(message);
    }

    /// <summary>A data type as Mermaid reads an attribute type: a letter or underscore, then letters, digits, <c>_ - ( ) [ ]</c>; anything else becomes an underscore.</summary>
    private static string AttributeType(string type)
    {
        return Word(s_gap.Replace(s_commaGap.Replace(type.Trim(), "_"), "_"), "unknown");
    }

    private static string AttributeName(string name)
    {
        return Word(s_gap.Replace(name.Trim(), "_"), "column");
    }

    private static string Word(string text, string fallback)
    {
        string word = s_notWordChar.Replace(text, "_");
        if (word.Length == 0)
        {
            return fallback;
        }

        return char.IsAsciiLetter(word[0]) || word[0] == '_' ? word : "_" + word;
    }

    #endregion

    #region Sequence

    /// <summary>The block word Mermaid has for an operator, and the word that starts its next operand (null: it has none).</summary>
    private static readonly Dictionary<string, string?> s_blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alt"] = "else",
        ["opt"] = null,
        ["loop"] = null,
        ["par"] = "and",
        ["critical"] = "option",
        ["break"] = null,
    };

    private static string WriteSequence(Document document, List<string> warnings)
    {
        var ids = new IdMap();
        var script = new StringBuilder();
        script.AppendLine("sequenceDiagram");
        if (!string.IsNullOrWhiteSpace(document.Title))
        {
            script.Append(Indent).Append("title ").AppendLine(Escape(document.Title));
        }

        // A fragment is a frame, not a lifeline. A participant a message creates is declared by that message, not before it.
        Dictionary<string, Node> fragments = document.Nodes
            .Where(n => n.Kind == Schemas.SequenceMessages.FragmentKind)
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var participantIds = new HashSet<string>(document.Nodes.Where(n => n.Kind != Schemas.SequenceMessages.FragmentKind).Select(n => n.Id), StringComparer.Ordinal);
        var creates = new Dictionary<string, string>(StringComparer.Ordinal);
        var destroys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Edge edge in document.Edges)
        {
            string target = edge.To.Node;
            if (!participantIds.Contains(target) || !participantIds.Contains(edge.From.Node))
            {
                continue;
            }

            if (edge.Kind == Schemas.SequenceMessages.CreateKind && edge.From.Node != target && !creates.ContainsKey(target))
            {
                creates[target] = edge.Id;
            }
            else if (edge.Kind == Schemas.SequenceMessages.DestroyKind && !destroys.ContainsKey(target))
            {
                destroys[target] = edge.Id;
            }
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes.Where(n => n.Kind != Schemas.SequenceMessages.FragmentKind))
        {
            declared.Add(node.Id);
            if (creates.ContainsKey(node.Id))
            {
                continue;
            }

            script.Append(Indent).Append(ParticipantDeclaration(node, ids)).AppendLine();
        }

        // A message to an id no node declares would still be drawn by Mermaid, so it gets a lifeline rather than losing the message.
        foreach (Edge edge in document.Edges)
        {
            foreach (string end in new[] { edge.From.Node, edge.To.Node })
            {
                if (declared.Add(end))
                {
                    string id = ids.Allocate(end);
                    script.Append(Indent).Append("participant ").Append(id);
                    if (id != end)
                    {
                        script.Append(" as ").Append(Escape(end));
                    }

                    script.AppendLine();
                }
            }
        }

        IReadOnlyDictionary<string, IReadOnlyList<Schemas.SequenceMessages.FragmentLevel>> memberships = Schemas.SequenceMessages.Memberships(document);
        var open = new List<(string Fragment, int Operand, bool Written)>();
        string Pad() => new string(' ', Indent.Length * (open.Count(block => block.Written) + 1));
        foreach (Edge edge in document.Edges)
        {
            IReadOnlyList<Schemas.SequenceMessages.FragmentLevel> wanted = memberships.TryGetValue(edge.Id, out var levels) ? levels : [];

            // Close the blocks this message is no longer in, then start the operand or the blocks it is in.
            int keep = 0;
            while (keep < open.Count && keep < wanted.Count && open[keep].Fragment == wanted[keep].Fragment && open[keep].Operand == wanted[keep].Operand)
            {
                keep++;
            }

            bool sameFragmentNewOperand = keep < open.Count && keep < wanted.Count && open[keep].Fragment == wanted[keep].Fragment;
            for (int level = open.Count - 1; level >= (sameFragmentNewOperand ? keep + 1 : keep); level--)
            {
                if (open[level].Written)
                {
                    open.RemoveAt(level);
                    script.Append(Pad()).AppendLine("end");
                }
                else
                {
                    open.RemoveAt(level);
                }
            }

            if (sameFragmentNewOperand)
            {
                (string fragment, _, bool written) = open[keep];
                open[keep] = (fragment, wanted[keep].Operand, written);
                if (written && fragments.TryGetValue(fragment, out Node? node) && s_blocks.TryGetValue(Schemas.SequenceMessages.OperatorOf(node), out string? next) && next is not null)
                {
                    script.Append(BlockIndent(open, keep)).Append(next).Append(Guard(node, wanted[keep].Operand)).AppendLine();
                }
                else if (written)
                {
                    warnings.Add($"Fragment '{fragment}' has a second operand, which Mermaid's '{Schemas.SequenceMessages.OperatorOf(fragments[fragment])}' block has no word for; the messages after it stay in the same block.");
                    script.Append(Pad()).Append("%% operand ").Append(wanted[keep].Operand.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine(" of the fragment above");
                }
            }

            for (int level = open.Count; level < wanted.Count; level++)
            {
                Schemas.SequenceMessages.FragmentLevel want = wanted[level];
                Node node = fragments[want.Fragment];
                string @operator = Schemas.SequenceMessages.OperatorOf(node).ToLowerInvariant();
                bool written = s_blocks.ContainsKey(@operator);
                if (written)
                {
                    script.Append(Pad()).Append(@operator).Append(Guard(node, want.Operand)).AppendLine();
                }
                else
                {
                    warnings.Add($"Fragment '{want.Fragment}' has operator '{@operator}', which Mermaid has no block for; its messages are written without the frame.");
                    script.Append(Pad()).Append("%% ").Append(@operator).Append(Guard(node, want.Operand)).AppendLine();
                }

                open.Add((want.Fragment, want.Operand, written));
            }

            string from = ids.Allocate(edge.From.Node);
            string to = ids.Allocate(edge.To.Node);
            if (edge.Kind is Schemas.SequenceMessages.NoteKind or Schemas.SequenceMessages.InteractionUseKind)
            {
                // Mermaid has no interaction use: it is written as a note reading `ref: <name>`, which reads back as a note.
                bool interactionUse = edge.Kind == Schemas.SequenceMessages.InteractionUseKind;
                if (interactionUse)
                {
                    warnings.Add($"Interaction use '{edge.Id}' has no Mermaid form; it is written as a note reading 'ref: {edge.Label}'.");
                }

                script.Append(Pad()).Append("Note over ").Append(from);
                if (to != from)
                {
                    script.Append(',').Append(to);
                }

                script.Append(':');
                if (!string.IsNullOrEmpty(edge.Label))
                {
                    script.Append(' ').Append(Escape(interactionUse ? "ref: " + edge.Label : edge.Label));
                }
                else if (interactionUse)
                {
                    script.Append(" ref:");
                }

                script.AppendLine();
                continue;
            }

            bool creating = creates.GetValueOrDefault(edge.To.Node) == edge.Id;
            bool destroying = destroys.GetValueOrDefault(edge.To.Node) == edge.Id;
            if (creating)
            {
                Node created = document.Nodes.First(n => n.Id == edge.To.Node);
                script.Append(Pad()).Append("create ").AppendLine(ParticipantDeclaration(created, ids));
            }
            else if (destroying)
            {
                script.Append(Pad()).Append("destroy ").AppendLine(to);
            }

            bool activate = SequenceMessageFlag(edge, "activate");
            bool deactivate = SequenceMessageFlag(edge, "deactivate");

            // Mermaid has one mark per message: '+' opens a bar on the receiver, '-' closes the sender's. A message that does
            // both keeps the '+' and closes the bar on the line after, which the importer reads back as the same message.
            string mark = activate ? "+" : deactivate ? "-" : string.Empty;
            string arrow = destroying && edge.Style is null ? "-x" : SequenceArrow(edge);
            script.Append(Pad()).Append(from).Append(arrow).Append(mark).Append(to).Append(':');
            if (!string.IsNullOrEmpty(edge.Label))
            {
                script.Append(' ').Append(Escape(edge.Label));
            }

            script.AppendLine();
            if (activate && deactivate)
            {
                script.Append(Pad()).Append("deactivate ").AppendLine(from);
            }
        }

        for (int level = open.Count - 1; level >= 0; level--)
        {
            bool written = open[level].Written;
            open.RemoveAt(level);
            if (written)
            {
                script.Append(Pad()).AppendLine("end");
            }
        }

        return script.ToString();
    }

    /// <summary><c>participant id as Label</c> (or <c>actor</c>) for a node, allocating its Mermaid id.</summary>
    private static string ParticipantDeclaration(Node node, IdMap ids)
    {
        string id = ids.Allocate(node.Id);
        string keyword = node.Kind == "actor" ? "actor" : "participant";
        return node.Label.Length > 0 && node.Label != id ? $"{keyword} {id} as {Escape(node.Label)}" : $"{keyword} {id}";
    }

    /// <summary>The guard of an operand as Mermaid writes it: the text after the block word, without the UML brackets; empty for none.</summary>
    private static string Guard(Node fragment, int operand)
    {
        IReadOnlyList<string> guards = Schemas.SequenceMessages.OperandsOf(fragment);
        string text = operand < guards.Count ? guards[operand].Trim() : string.Empty;
        if (text.StartsWith('[') && text.EndsWith(']'))
        {
            text = text[1..^1].Trim();
        }

        return text.Length == 0 ? string.Empty : " " + Escape(text);
    }

    /// <summary>The indent of the block at <paramref name="level"/> of the open ones, where its continuation word (<c>else</c>) goes.</summary>
    private static string BlockIndent(List<(string Fragment, int Operand, bool Written)> open, int level)
    {
        return new string(' ', Indent.Length * (open.Take(level).Count(block => block.Written) + 1));
    }

    /// <summary>
    /// The arrow for a message: its kind names the family (<c>sync</c> a filled head, <c>async</c> an open one, <c>return</c> a dashed
    /// reply) and its style overrides the line and the ends, so what the picture shows is what Mermaid is told.
    /// </summary>
    private static string SequenceArrow(Edge edge)
    {
        (Dash defaultDash, ArrowHead defaultEnd) = edge.Kind switch
        {
            "return" => (Dash.Dashed, ArrowHead.Open),
            "create" => (Dash.Dashed, ArrowHead.Open),
            "async" => (Dash.Solid, ArrowHead.Open),
            _ => (Dash.Solid, ArrowHead.Arrow),
        };

        bool dashed = (edge.Style?.Dash ?? defaultDash) != Dash.Solid;
        ArrowHead end = edge.Style?.ArrowEnd ?? defaultEnd;
        ArrowHead start = edge.Style?.ArrowStart ?? ArrowHead.None;
        if (end == ArrowHead.None)
        {
            return dashed ? "-->" : "->";
        }

        if (start != ArrowHead.None)
        {
            return dashed ? "<<-->>" : "<<->>";
        }

        if (edge.Kind == "async")
        {
            return dashed ? "--)" : "-)";
        }

        return dashed ? "-->>" : "->>";
    }

    /// <summary>The same rule the drawing uses (<c>SequenceMessages.IsSet</c>): on is JSON <c>true</c> or the string <c>"true"</c>, any case.</summary>
    private static bool SequenceMessageFlag(Edge edge, string name)
    {
        return Schemas.SequenceMessages.IsSet(edge, name);
    }

    #endregion

    #region Flowchart

    private static string WriteFlowchart(Document document, List<string> warnings)
    {
        var ids = new IdMap();
        var script = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(document.Title))
        {
            script.AppendLine("---");
            script.Append("title: ").AppendLine(FrontMatterValue(document.Title));
            script.AppendLine("---");
        }

        script.AppendLine("flowchart TD");

        Dictionary<string, Node> byId = document.Nodes
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var children = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        var roots = new List<Node>();
        foreach (Node node in document.Nodes)
        {
            if (node.Parent is not null && node.Parent != node.Id && byId.ContainsKey(node.Parent))
            {
                if (!children.TryGetValue(node.Parent, out List<Node>? siblings))
                {
                    children[node.Parent] = siblings = [];
                }

                siblings.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (Node root in roots)
        {
            WriteNode(script, root, children, ids, written, warnings, depth: 1);
        }

        // Only a parent cycle leaves a node unwritten (each is another's child, so none is a root); write them flat rather than lose them.
        foreach (Node node in document.Nodes)
        {
            if (!written.Contains(node.Id))
            {
                WriteNode(script, node with { Parent = null }, [], ids, written, warnings, depth: 1);
            }
        }

        // A link to an id no node declares would make Mermaid invent a box named after it; declare it so the diagram says so.
        foreach (Edge edge in document.Edges)
        {
            foreach (string end in new[] { edge.From.Node, edge.To.Node })
            {
                if (written.Add(end))
                {
                    script.Append(Indent).Append(ids.Allocate(end)).Append("[\"").Append(Quote(end)).AppendLine("\"]");
                }
            }
        }

        foreach (Edge edge in document.Edges)
        {
            string from = ids.Allocate(edge.From.Node);
            string to = ids.Allocate(edge.To.Node);
            string link = FlowLink(edge, warnings, out string? loss);
            if (loss is not null)
            {
                script.Append(Indent).Append("%% ").AppendLine(loss);
            }

            script.Append(Indent).Append(from).Append(' ').Append(link);
            if (!string.IsNullOrEmpty(edge.Label))
            {
                script.Append("|\"").Append(Quote(edge.Label)).Append("\"|");
            }

            script.Append(' ').AppendLine(to);
        }

        return script.ToString();
    }

    private static void WriteNode(StringBuilder script, Node node, Dictionary<string, List<Node>> children, IdMap ids, HashSet<string> written, List<string> warnings, int depth)
    {
        if (!written.Add(node.Id))
        {
            return;
        }

        string pad = string.Concat(Enumerable.Repeat(Indent, depth));
        string id = ids.Allocate(node.Id);
        bool hasChildren = children.TryGetValue(node.Id, out List<Node>? members) && members.Count > 0;
        if (hasChildren || node.Kind == "container")
        {
            script.Append(pad).Append("subgraph ").Append(id).Append("[\"").Append(Quote(node.Label)).AppendLine("\"]");
            foreach (Node member in members ?? [])
            {
                WriteNode(script, member, children, ids, written, warnings, depth + 1);
            }

            script.Append(pad).AppendLine("end");
            return;
        }

        (string open, string close, string? loss) = FlowShape(node);
        if (loss is not null)
        {
            string warning = $"{node.Id}: {loss}";
            warnings.Add(warning);
            script.Append(pad).Append("%% ").AppendLine(warning);
        }

        script.Append(pad).Append(id).Append(open).Append('"').Append(Quote(node.Label)).Append('"').AppendLine(close);
    }

    /// <summary>
    /// The Mermaid brackets for a node, or a rectangle and what was lost. A shape maps only when Mermaid's own drawing means the
    /// same thing (a cylinder is a database, a stadium a terminator, a subroutine box a predefined process); a lookalike
    /// (a circle for a summing junction) is a loss, not a match, so the reader is told rather than shown a wrong symbol.
    /// </summary>
    private static (string Open, string Close, string? Loss) FlowShape(Node node)
    {
        switch (node.Shape)
        {
            case "rectangle":
                // Two kinds the importer reads back from a particular Mermaid shape keep it, so a paste-and-import round trip keeps them.
                return node.Kind switch
                {
                    "subprocess" => ("[[", "]]", null),
                    "note" => (">", "]", null),
                    _ => ("[", "]", null),
                };
            case "rounded":
                return ("(", ")", null);
            case "diamond":
                return ("{", "}", null);
            case "cylinder":
                return ("[(", ")]", null);
            case "pill":
                return ("([", "])", null);
            case "hexagon":
                return ("{{", "}}", null);
            case "parallelogram":
                return ("[/", "/]", null);
            case "predefined-process":
                return ("[[", "]]", null);
            case "on-page-connector":
                return ("((", "))", null);
            default:
                return ("[", "]", $"shape '{node.Shape}' has no Mermaid equivalent; drawn as a rectangle");
        }
    }

    /// <summary>
    /// The link operator for an edge: the kind's default look under the edge's own style, then the heads Mermaid has (none, an
    /// arrow, a circle) on a solid, dotted or thick line. A head it has not (a crow's foot, a diamond) is a plain arrow with a comment.
    /// </summary>
    private static string FlowLink(Edge edge, List<string> warnings, out string? loss)
    {
        // The kinds' looks in themes/diagram-*.yaml; an unknown kind draws as the default flow.
        (Dash dash, ArrowHead start, ArrowHead end) = edge.Kind switch
        {
            "association" => (Dash.Solid, ArrowHead.None, ArrowHead.None),
            "dotted-report" => (Dash.Dashed, ArrowHead.None, ArrowHead.None),
            "dependency" => (Dash.Dashed, ArrowHead.None, ArrowHead.Open),
            "data" => (Dash.Solid, ArrowHead.None, ArrowHead.Open),
            "message-flow" => (Dash.Dashed, ArrowHead.Circle, ArrowHead.Open),
            "bpmn-association" => (Dash.Dotted, ArrowHead.None, ArrowHead.None),
            "data-association" => (Dash.Dotted, ArrowHead.None, ArrowHead.Open),
            _ => (Dash.Solid, ArrowHead.None, ArrowHead.Arrow),
        };

        dash = edge.Style?.Dash ?? dash;
        start = edge.Style?.ArrowStart ?? start;
        end = edge.Style?.ArrowEnd ?? end;
        bool thick = edge.Style?.StrokeWidth is >= 3;

        var lost = new List<string>();
        string left = Head(start, isEnd: false, lost);
        string right = Head(end, isEnd: true, lost);
        loss = null;
        if (lost.Count > 0)
        {
            loss = $"{edge.Id}: arrowhead '{string.Join("', '", lost)}' has no Mermaid equivalent; drawn as a plain arrow";
            warnings.Add(loss);
        }

        if (thick)
        {
            return left + "==" + (right.Length == 0 ? "=" : right);
        }

        return dash == Dash.Solid ? left + "--" + (right.Length == 0 ? "-" : right) : left + "-." + "-" + right;
    }

    private static string Head(ArrowHead head, bool isEnd, List<string> lost)
    {
        switch (head)
        {
            case ArrowHead.None:
                return string.Empty;
            case ArrowHead.Circle:
                return "o";
            case ArrowHead.Arrow:
            case ArrowHead.Open:
                return isEnd ? ">" : "<";
            default:
                lost.Add(head.ToString().ToLowerInvariant());
                return isEnd ? ">" : "<";
        }
    }

    #endregion

    #region Text

    /// <summary>Text on an unquoted line (a participant alias, a message, a title): a newline is <c>&lt;br/&gt;</c>, and <c>;</c> and <c>#</c>, which Mermaid reads as a statement end and an entity, are entities the importer decodes.</summary>
    private static string Escape(string text)
    {
        // One pass: replacing '#' first and ';' second would turn the ';' of the '#35;' just written into '#59;'.
        var builder = new StringBuilder(text.Length + 8);
        foreach (char c in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal))
        {
            builder.Append(c switch { '#' => "#35;", ';' => "#59;", '\n' => "<br/>", _ => c.ToString() });
        }

        return builder.ToString();
    }

    /// <summary>Text inside a double-quoted flowchart label, where only the quote and the entity marker need care.</summary>
    private static string Quote(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("#", "#35;", StringComparison.Ordinal)
            .Replace("\"", "#quot;", StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal);
    }

    /// <summary>A front-matter title, quoted only when plain YAML would misread it (a colon, a hash, a leading quote or space).</summary>
    private static string FrontMatterValue(string title)
    {
        string line = title.ReplaceLineEndings(" ").Trim();
        bool plain = line.Length > 0 && !line.Contains(": ", StringComparison.Ordinal) && !line.Contains(" #", StringComparison.Ordinal)
            && !line.EndsWith(':') && !"\"'#&*!|>%@`[]{},-?".Contains(line[0], StringComparison.Ordinal);
        return plain ? line : $"\"{line.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    #endregion

    /// <summary>
    /// Mermaid ids for document ids: letters, digits and underscores only (a hyphen or dot ends an id in some places), not a
    /// word Mermaid reads as syntax, unique. The <c>p_</c>, <c>n_</c> or <c>c_</c> the importer puts on an id is dropped when what is left
    /// is usable, so the text a reader sees says <c>user->>web</c> and not <c>p_user->>p_web</c>.
    /// </summary>
    private sealed class IdMap
    {
        private readonly Dictionary<string, string> _byDocumentId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        public string Allocate(string documentId)
        {
            if (_byDocumentId.TryGetValue(documentId, out string? existing))
            {
                return existing;
            }

            string sanitised = Sanitise(documentId);
            string candidate = documentId.Length > 2 && documentId[1] == '_' && documentId[0] is 'n' or 'c' or 'p'
                ? Sanitise(documentId[2..])
                : sanitised;
            if (s_reserved.Contains(candidate))
            {
                candidate = "x_" + candidate;
            }

            string unique = candidate;
            for (int n = 2; !_used.Add(unique); n++)
            {
                unique = $"{candidate}_{n}";
            }

            _byDocumentId[documentId] = unique;
            return unique;
        }

        private static string Sanitise(string id)
        {
            var builder = new StringBuilder(id.Length);
            foreach (char c in id)
            {
                builder.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
            }

            return builder.Length == 0 ? "n" : builder.ToString();
        }
    }
}
