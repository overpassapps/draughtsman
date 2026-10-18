using System.Globalization;
using System.Text;
using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Diffing;

/// <summary>
/// One sentence of a <see cref="SemanticDiffReport"/>. <see cref="Category"/> is <c>document</c>, <c>node</c>, <c>edge</c>,
/// <c>order</c> or <c>flythrough</c>; <see cref="ElementId"/> names the element for a machine reader (the sentence itself
/// never does, it says the label); <see cref="Layout"/> marks a change to where something is drawn rather than to what the
/// diagram says (position, size, waypoints, ports, or the drawing order of a type whose order means nothing).
/// </summary>
public sealed record DiffEntry(string Category, string? ElementId, string Text, bool Layout);

/// <summary>
/// What changed between two diagrams, in sentences. <see cref="Changes"/> is the diagram's own change and is all that
/// <see cref="HasChanges"/> looks at; <see cref="Housekeeping"/> holds what every save rewrites (saved time, author, AI
/// provenance), reported apart so a save that changed nothing is not an edit. With <c>ignoreLayout</c>, layout entries are
/// not in <see cref="Changes"/>: they are counted in <see cref="LayoutOnlyChanges"/> instead.
/// </summary>
public sealed record SemanticDiffReport(
    string Title,
    bool IgnoreLayout,
    IReadOnlyList<DiffEntry> Changes,
    int LayoutOnlyChanges,
    IReadOnlyList<DiffEntry> Housekeeping)
{
    /// <summary>True when the diagram itself changed. Layout collapsed by <c>ignoreLayout</c> and housekeeping do not count.</summary>
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Turns <see cref="DocumentDiff"/> into the review a person wants in a pull request: sentences that name things by
/// their label, never by id ("Edge `Orders API` to `Postgres` relabelled from `read/write` to `reads`"), one per changed
/// field, prop key or style key. Pure over two documents, so the CLI, a test and any future surface share one account of
/// a change. Every label and value is wrapped as inline code (<see cref="Code"/>): a label is text somebody typed into a
/// diagram and the Markdown ends up in a pull request, so it must not be able to ping a user or inject markup.
/// </summary>
public static class SemanticDiff
{
    private const int MaxValueLength = 120;

    public static SemanticDiffReport Describe(Document from, Document to, bool ignoreLayout)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        DocumentDiffResult diff = DocumentDiff.Compare(from, to);
        var describer = new Describer(from, to);
        List<DiffEntry> entries = describer.Describe(diff);

        List<DiffEntry> changes = entries;
        int layoutOnly = 0;
        if (ignoreLayout)
        {
            // One per element, not per field: an element that moved and was resized is one layout change.
            layoutOnly = entries.Where(e => e.Layout).Select(e => (e.Category, e.ElementId)).Distinct().Count();
            changes = entries.Where(e => !e.Layout).ToList();
        }

        return new SemanticDiffReport(to.Title, ignoreLayout, changes, layoutOnly, describer.Housekeeping(diff));
    }

    /// <summary>The report as Markdown for a job summary or a comment: a heading, one bullet per change, then the housekeeping.</summary>
    public static string ToMarkdown(SemanticDiffReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        text.Append("### Diagram changes: ").Append(Code(report.Title)).Append("\n\n");

        string layoutLine = LayoutLine(report.LayoutOnlyChanges);
        if (!report.HasChanges)
        {
            text.Append(report.LayoutOnlyChanges > 0 ? $"No semantic changes; {layoutLine}.\n" : "No changes.\n");
        }
        else
        {
            foreach (DiffEntry entry in report.Changes)
            {
                text.Append("- ").Append(entry.Text).Append('\n');
            }

            if (report.LayoutOnlyChanges > 0)
            {
                text.Append("- ").Append(layoutLine).Append('\n');
            }
        }

        if (report.Housekeeping.Count > 0)
        {
            text.Append("\nNot counted as changes (every save rewrites these):\n\n");
            foreach (DiffEntry entry in report.Housekeeping)
            {
                text.Append("- ").Append(entry.Text).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>The report as JSON, written the way every other Draughtsman JSON is, with a trailing newline.</summary>
    public static string ToJson(SemanticDiffReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, DocumentJson.Options) + "\n";
    }

    private static string LayoutLine(int count)
    {
        return count == 1 ? "1 layout-only change" : $"{count} layout-only changes";
    }

    /// <summary>A label or value as inline Markdown code, on one line and capped, with a fence longer than any backtick run inside it.</summary>
    internal static string Code(string value)
    {
        var line = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            line.Append(char.IsControl(c) ? ' ' : c);
        }

        string flat = line.ToString().Trim();
        if (flat.Length > MaxValueLength)
        {
            flat = flat[..MaxValueLength] + "…";
        }

        if (flat.Length == 0)
        {
            return "``";
        }

        int longest = 0;
        int run = 0;
        foreach (char c in flat)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        string fence = new('`', longest + 1);
        string pad = flat[0] == '`' || flat[^1] == '`' ? " " : string.Empty;
        return $"{fence}{pad}{flat}{pad}{fence}";
    }

    private sealed class Describer(Document from, Document to)
    {
        private readonly Dictionary<string, Node> _fromNodes = from.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        private readonly Dictionary<string, Node> _toNodes = to.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        private readonly Dictionary<string, Edge> _fromEdges = from.Edges.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        private readonly Dictionary<string, Edge> _toEdges = to.Edges.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        public List<DiffEntry> Describe(DocumentDiffResult diff)
        {
            var entries = new List<DiffEntry>();

            foreach (FieldChange change in diff.DocumentChanges)
            {
                entries.Add(new DiffEntry("document", null, DocumentSentence(change), false));
            }

            foreach (Node node in diff.AddedNodes)
            {
                entries.Add(new DiffEntry("node", node.Id, $"Added node {Code(NodeName(node))} ({Code(node.Kind)})", false));
            }

            foreach (Node node in diff.RemovedNodes)
            {
                entries.Add(new DiffEntry("node", node.Id, $"Removed node {Code(NodeName(node))} ({Code(node.Kind)})", false));
            }

            // Changed elements come in the order the newer diagram lists them, which is how a reader walks it, not by id.
            foreach (ElementChange change in InDocumentOrder(diff.ChangedNodes, to.Nodes.Select(n => n.Id)))
            {
                DescribeNodeChange(change, entries);
            }

            foreach (Edge edge in diff.AddedEdges)
            {
                entries.Add(new DiffEntry("edge", edge.Id, $"Added {Code(edge.Kind)} edge {EdgeName(edge, _toNodes)}{LabelledSuffix(edge.Label)}", false));
            }

            foreach (Edge edge in diff.RemovedEdges)
            {
                entries.Add(new DiffEntry("edge", edge.Id, $"Removed {Code(edge.Kind)} edge {EdgeName(edge, _fromNodes)}{LabelledSuffix(edge.Label)}", false));
            }

            foreach (ElementChange change in InDocumentOrder(diff.ChangedEdges, to.Edges.Select(e => e.Id)))
            {
                DescribeEdgeChange(change, entries);
            }

            // Order is the diagram's meaning in a sequence (the order of the edges is time) and only the drawing order elsewhere.
            bool orderMatters = to.Type == DocumentType.Sequence || from.Type == DocumentType.Sequence;
            foreach (ElementMove move in diff.MovedNodes)
            {
                string after = move.After is { } id ? $"after {Code(NodeNameById(id, _toNodes))}" : "to the start";
                entries.Add(new DiffEntry("order", move.Id, $"Node {Code(NodeNameById(move.Id, _toNodes))} moved {after}", !orderMatters));
            }

            foreach (ElementMove move in diff.MovedEdges)
            {
                string after = move.After is { } id && _toEdges.TryGetValue(id, out Edge? previous) ? $"after edge {EdgeName(previous, _toNodes)}" : "to the start";
                string name = _toEdges.TryGetValue(move.Id, out Edge? edge) ? EdgeName(edge, _toNodes) : Code(move.Id);
                entries.Add(new DiffEntry("order", move.Id, $"Edge {name} moved {after}", !orderMatters));
            }

            if (diff.FlythroughChange is { } flythrough)
            {
                entries.Add(new DiffEntry("flythrough", null, FlythroughSentence(flythrough), false));
            }

            return entries;
        }

        private static IEnumerable<ElementChange> InDocumentOrder(IReadOnlyList<ElementChange> changes, IEnumerable<string> ids)
        {
            Dictionary<string, int> position = ids.Select((id, index) => (id, index)).GroupBy(p => p.id).ToDictionary(g => g.Key, g => g.First().index, StringComparer.Ordinal);
            return changes.OrderBy(c => position.GetValueOrDefault(c.Id, int.MaxValue));
        }

        public List<DiffEntry> Housekeeping(DocumentDiffResult diff)
        {
            var entries = new List<DiffEntry>();
            foreach (FieldChange change in diff.VolatileChanges)
            {
                string text = change.Field switch
                {
                    "meta.createdAt" => $"Created time changed from {Value(change.From)} to {Value(change.To)}",
                    "meta.updatedAt" => $"Last saved time changed from {Value(change.From)} to {Value(change.To)}",
                    "meta.createdBy" => $"Author changed from {Value(change.From)} to {Value(change.To)}",
                    _ => $"AI provenance trail changed ({EntryCount(change.From)} to {EntryCount(change.To)})",
                };
                entries.Add(new DiffEntry("housekeeping", null, text, false));
            }

            return entries;
        }

        private static string EntryCount(JsonElement? trail)
        {
            int count = trail is { ValueKind: JsonValueKind.Array } array ? array.GetArrayLength() : 0;
            return count == 1 ? "1 entry" : $"{count} entries";
        }

        private static string DocumentSentence(FieldChange change)
        {
            if (change.Field.StartsWith("theme.", StringComparison.Ordinal))
            {
                string token = Code(change.Field["theme.".Length..]);
                return (change.From, change.To) switch
                {
                    (null, _) => $"Theme override {token} set to {Value(change.To)}",
                    (_, null) => $"Theme override {token} removed (was {Value(change.From)})",
                    _ => $"Theme override {token} changed from {Value(change.From)} to {Value(change.To)}",
                };
            }

            string what = change.Field switch
            {
                "title" => "Title",
                "type" => "Diagram type",
                "schemaVersion" => "Schema version",
                _ => "Document id",
            };
            return $"{what} changed from {Value(change.From)} to {Value(change.To)}";
        }

        private void DescribeNodeChange(ElementChange change, List<DiffEntry> entries)
        {
            _fromNodes.TryGetValue(change.Id, out Node? before);
            _toNodes.TryGetValue(change.Id, out Node? after);
            string name = Code(NodeName(before ?? after!));

            void Add(string text, bool layout = false)
            {
                entries.Add(new DiffEntry("node", change.Id, text, layout));
            }

            foreach (FieldChange field in change.Fields)
            {
                switch (field.Field)
                {
                    case "label":
                        Add($"Node {name} relabelled to {Value(field.To)}");
                        break;
                    case "kind":
                        Add($"Node {name} changed kind from {Value(field.From)} to {Value(field.To)}");
                        break;
                    case "shape":
                        Add($"Node {name} changed shape from {Value(field.From)} to {Value(field.To)}");
                        break;
                    case "parent":
                        Add($"Node {name} {ParentSentence(before?.Parent, after?.Parent)}");
                        break;
                    case "style":
                        foreach (string sentence in KeyChangeSentences($"Node {name} style", field.From, field.To))
                        {
                            Add(sentence);
                        }

                        break;
                    case "props":
                        foreach (FieldChange prop in change.Props)
                        {
                            Add(PropSentence($"Node {name}", prop));
                        }

                        break;
                    case "geometry":
                        Add($"Node {name} {GeometrySentence(field.From, field.To)}", layout: true);
                        break;
                    case "ports":
                        Add($"Node {name} ports changed ({PortList(field.From)} to {PortList(field.To)})", layout: true);
                        break;
                }
            }
        }

        private string ParentSentence(string? before, string? after)
        {
            string Container(string id, Dictionary<string, Node> nodes) => Code(NodeNameById(id, nodes));
            return (before, after) switch
            {
                (null, { } to) => $"moved into {Container(to, _toNodes)}",
                ({ } fromId, null) => $"moved out of {Container(fromId, _fromNodes)}",
                ({ } fromId, { } toId) => $"moved from {Container(fromId, _fromNodes)} into {Container(toId, _toNodes)}",
                _ => "kept its container",
            };
        }

        private static string GeometrySentence(JsonElement? before, JsonElement? after)
        {
            if (before is not { ValueKind: JsonValueKind.Object } b)
            {
                return after is { ValueKind: JsonValueKind.Object } placed ? $"placed at {Point(placed)}, {Size(placed)}" : "had its position cleared";
            }

            if (after is not { ValueKind: JsonValueKind.Object } a)
            {
                return "had its position cleared";
            }

            var parts = new List<string>();
            if (Number(b, "x") != Number(a, "x") || Number(b, "y") != Number(a, "y"))
            {
                parts.Add($"moved from {Point(b)} to {Point(a)}");
            }

            if (Number(b, "w") != Number(a, "w") || Number(b, "h") != Number(a, "h"))
            {
                parts.Add($"resized from {Size(b)} to {Size(a)}");
            }

            if (Number(b, "rotation") != Number(a, "rotation"))
            {
                parts.Add($"rotated from {Format(Number(b, "rotation"))}° to {Format(Number(a, "rotation"))}°");
            }

            return string.Join(" and ", parts);
        }

        private static string Point(JsonElement geometry)
        {
            return $"({Format(Number(geometry, "x"))}, {Format(Number(geometry, "y"))})";
        }

        private static string Size(JsonElement geometry)
        {
            return $"{Format(Number(geometry, "w"))}x{Format(Number(geometry, "h"))}";
        }

        private static double Number(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
        }

        private static string Format(double number)
        {
            return number.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string PortList(JsonElement? ports)
        {
            if (ports is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0)
            {
                return "none";
            }

            string ids = string.Join(", ", array.EnumerateArray().Select(p => p.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty));
            return Code(ids);
        }

        private void DescribeEdgeChange(ElementChange change, List<DiffEntry> entries)
        {
            _fromEdges.TryGetValue(change.Id, out Edge? before);
            _toEdges.TryGetValue(change.Id, out Edge? after);
            bool reconnected = before is not null && after is not null && (before.From.Node != after.From.Node || before.To.Node != after.To.Node);
            string name = reconnected ? EdgeName(before!, _fromNodes) : EdgeName(after ?? before!, _toNodes);

            void Add(string text, bool layout = false)
            {
                entries.Add(new DiffEntry("edge", change.Id, text, layout));
            }

            foreach (FieldChange field in change.Fields)
            {
                switch (field.Field)
                {
                    case "label":
                        Add(LabelSentence(name, field.From, field.To));
                        break;
                    case "kind":
                        Add($"Edge {name} changed kind from {Value(field.From)} to {Value(field.To)}");
                        break;
                    case "from":
                    case "to":
                        DescribeEndpoint(name, field, before, after, Add);
                        break;
                    case "routing":
                        DescribeRouting(name, field, Add);
                        break;
                    case "style":
                        foreach (string sentence in KeyChangeSentences($"Edge {name} style", field.From, field.To))
                        {
                            Add(sentence);
                        }

                        break;
                    case "props":
                        foreach (FieldChange prop in change.Props)
                        {
                            Add(PropSentence($"Edge {name}", prop));
                        }

                        break;
                }
            }
        }

        private static string LabelSentence(string name, JsonElement? before, JsonElement? after)
        {
            return (before, after) switch
            {
                (null, _) => $"Edge {name} labelled {Value(after)}",
                (_, null) => $"Edge {name} label {Value(before)} removed",
                _ => $"Edge {name} relabelled from {Value(before)} to {Value(after)}",
            };
        }

        private void DescribeEndpoint(string name, FieldChange field, Edge? before, Edge? after, Action<string, bool> add)
        {
            bool source = field.Field == "from";
            Endpoint? older = source ? before?.From : before?.To;
            Endpoint? newer = source ? after?.From : after?.To;
            string end = source ? "starts" : "ends";
            if (older is not null && newer is not null && older.Node != newer.Node)
            {
                add($"Edge {name} reconnected: now {end} at {Code(NodeNameById(newer.Node, _toNodes))} (was {Code(NodeNameById(older.Node, _fromNodes))})", false);
            }

            if (older?.Port != newer?.Port)
            {
                // Which side of a box a line leaves from is drawing; which column of a table it names is the diagram's meaning.
                bool column = IsColumnPort(older?.Port) || IsColumnPort(newer?.Port);
                string was = older?.Port is { } o ? Code(o) : "no port";
                string now = newer?.Port is { } n ? Code(n) : "no port";
                add($"Edge {name} {(source ? "source" : "target")} port changed from {was} to {now}", !column);
            }
        }

        private static bool IsColumnPort(string? port)
        {
            return port is not null && port.StartsWith("col:", StringComparison.Ordinal);
        }

        private static void DescribeRouting(string name, FieldChange field, Action<string, bool> add)
        {
            string? Mode(JsonElement? routing) => routing is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("mode", out JsonElement m) ? m.GetString() : null;
            string? modeBefore = Mode(field.From);
            string? modeAfter = Mode(field.To);
            if (modeBefore != modeAfter)
            {
                add(modeBefore is null
                    ? $"Edge {name} routing set to {Code(modeAfter ?? "none")}"
                    : $"Edge {name} routing changed from {Code(modeBefore)} to {Code(modeAfter ?? "none")}", false);
            }

            string Waypoints(JsonElement? routing) => routing is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("waypoints", out JsonElement w) ? w.GetRawText() : "[]";
            if (Waypoints(field.From) != Waypoints(field.To))
            {
                add($"Edge {name} rerouted (waypoints changed)", true);
            }
        }

        private static string PropSentence(string subject, FieldChange prop)
        {
            string key = Code(prop.Field);
            return (prop.From, prop.To) switch
            {
                (null, _) => $"{subject}: prop {key} added ({Value(prop.To)})",
                (_, null) => $"{subject}: prop {key} removed (was {Value(prop.From)})",
                _ => $"{subject}: prop {key} changed from {Value(prop.From)} to {Value(prop.To)}",
            };
        }

        /// <summary>One sentence per key that differs between two JSON objects (a style, say): set, cleared or changed.</summary>
        private static IEnumerable<string> KeyChangeSentences(string subject, JsonElement? before, JsonElement? after)
        {
            Dictionary<string, JsonElement> older = Members(before);
            Dictionary<string, JsonElement> newer = Members(after);
            foreach (string key in older.Keys.Union(newer.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                bool had = older.TryGetValue(key, out JsonElement oldValue);
                bool has = newer.TryGetValue(key, out JsonElement newValue);
                if (had && has && oldValue.GetRawText() == newValue.GetRawText())
                {
                    continue;
                }

                yield return (had, has) switch
                {
                    (false, _) => $"{subject}: {Code(key)} set to {Value(newValue)}",
                    (_, false) => $"{subject}: {Code(key)} cleared (was {Value(oldValue)})",
                    _ => $"{subject}: {Code(key)} changed from {Value(oldValue)} to {Value(newValue)}",
                };
            }
        }

        private static Dictionary<string, JsonElement> Members(JsonElement? element)
        {
            var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (element is { ValueKind: JsonValueKind.Object } obj)
            {
                foreach (JsonProperty property in obj.EnumerateObject())
                {
                    members[property.Name] = property.Value;
                }
            }

            return members;
        }

        private static string FlythroughSentence(FlythroughChange change)
        {
            static string Count(Flythrough path) => path.Keyframes.Count == 1 ? "1 keyframe" : $"{path.Keyframes.Count} keyframes";
            return (change.From, change.To) switch
            {
                (null, { } added) => $"Added fly-through ({Count(added)})",
                ({ } removed, null) => $"Removed fly-through (was {Count(removed)})",
                ({ } older, { } newer) => $"Changed fly-through ({Count(older)} to {Count(newer)})",
                _ => "Changed fly-through",
            };
        }

        // The label of the node, or what to call a node with none; an id only when the edge points at a node that is not there.
        private static string NodeName(Node node)
        {
            return string.IsNullOrWhiteSpace(node.Label) ? $"unlabelled {node.Kind}" : node.Label;
        }

        private static string NodeNameById(string id, Dictionary<string, Node> nodes)
        {
            return nodes.TryGetValue(id, out Node? node) ? NodeName(node) : id;
        }

        private static string EdgeName(Edge edge, Dictionary<string, Node> nodes)
        {
            return $"{Code(NodeNameById(edge.From.Node, nodes))} to {Code(NodeNameById(edge.To.Node, nodes))}";
        }

        private static string LabelledSuffix(string? label)
        {
            return string.IsNullOrEmpty(label) ? string.Empty : $" labelled {Code(label)}";
        }

        private static string Value(JsonElement? value)
        {
            if (value is not { } element)
            {
                return "nothing";
            }

            return Code(element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText());
        }
    }
}
