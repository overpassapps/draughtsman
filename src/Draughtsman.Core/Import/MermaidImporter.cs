using System.Text;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Import;

/// <summary>
/// Imports a Mermaid flowchart into a <see cref="Document"/> (an <c>erDiagram</c> goes to <see cref="MermaidErImporter"/>, and only into an <c>architecture</c> or <c>freeform</c> document; a <c>sequenceDiagram</c> goes to <see cref="MermaidSequenceImporter"/>, and only
/// into a <c>sequence</c> document; a source and a type that disagree throw, they are never relabelled): nodes get <c>n_&lt;mermaidId&gt;</c> ids, subgraphs become
/// <c>c_&lt;id&gt;</c> containers, links become <c>e_1</c>, <c>e_2</c>… edges in source order. No geometry is produced;
/// the caller runs layout. Every guessed kind and every skipped construct is reported as a warning, never an exception.
/// </summary>
public static class MermaidImporter
{
    private const string DefaultTitle = "Imported diagram";
    private const string CreatedBy = "mermaid-import";
    private const int MaxIdLength = 128;

    /// <summary>True when the source is a Mermaid <c>sequenceDiagram</c>, which imports as a <c>sequence</c> document; callers with no type of their own use it to pick one.</summary>
    public static bool IsSequenceDiagram(string? source)
    {
        return SourceRejectedException.Guard("Mermaid", () => MermaidSequenceImporter.IsSequenceDiagram(source));
    }

    /// <summary>True when the source is a Mermaid <c>erDiagram</c>, which imports as an <c>architecture</c> (or <c>freeform</c>) document of ER entities; callers with no type of their own use it to pick one.</summary>
    public static bool IsErDiagram(string? source)
    {
        return SourceRejectedException.Guard("Mermaid", () => MermaidErImporter.IsErDiagram(source));
    }

    /// <exception cref="SourceRejectedException">A line took the parser past its regex match timeout (<see cref="RegexLimits"/>).</exception>
    public static ImportResult Import(string source, MermaidImportOptions? options = null)
    {
        return SourceRejectedException.Guard("Mermaid", () => ImportCore(source, options ?? new MermaidImportOptions()));
    }

    /// <summary>A Mermaid shape in the words a person drawing it would use, for the warning that its kind was a judgement call.</summary>
    private static string DescribeShape(MermaidShape? shape) => shape switch
    {
        MermaidShape.Circle => "a circle",
        MermaidShape.DoubleCircle => "a double circle",
        MermaidShape.Asymmetric => "a flag shape",
        MermaidShape.Hexagon => "a hexagon",
        MermaidShape.Stadium => "a stadium (pill) shape",
        MermaidShape.Subroutine => "a subroutine box",
        MermaidShape.Parallelogram => "a parallelogram",
        MermaidShape.Rhombus => "a diamond",
        MermaidShape.Cylinder => "a cylinder",
        _ => "a plain box",
    };

    private static ImportResult ImportCore(string source, MermaidImportOptions options)
    {
        if (MermaidErImporter.IsErDiagram(source))
        {
            if (options.Type is not (DocumentType.Architecture or DocumentType.Freeform))
            {
                throw new ArgumentException($"This source is a Mermaid 'erDiagram', which imports as entities in an 'architecture' or 'freeform' document, not '{options.Type.ToString().ToLowerInvariant()}'. Use type 'architecture'.", nameof(options));
            }

            return MermaidErImporter.Import(source, options);
        }

        bool sequenceSource = MermaidSequenceImporter.IsSequenceDiagram(source);
        if (options.Type == DocumentType.Sequence)
        {
            if (!sequenceSource)
            {
                throw new ArgumentException("A sequence document is made from a Mermaid 'sequenceDiagram'; this source is not one. Use type 'flowchart' or 'architecture' for a flowchart, or start the source with 'sequenceDiagram'.", nameof(options));
            }

            return MermaidSequenceImporter.Import(source, options);
        }

        if (sequenceSource)
        {
            throw new ArgumentException($"This source is a Mermaid 'sequenceDiagram', which imports as a 'sequence' document, not '{options.Type.ToString().ToLowerInvariant()}'. Use type 'sequence'.", nameof(options));
        }

        if (!KindVocabulary.TryGet(options.Type, out _))
        {
            throw new ArgumentException($"Document type '{options.Type}' has no kind vocabulary; Mermaid import needs flowchart or architecture.", nameof(options));
        }

        MermaidGraph graph = MermaidFlowchartParser.Parse(source);
        var warnings = new List<ImportWarning>(graph.Warnings);
        var nodes = new List<Node>();
        var edges = new List<Edge>();

        var ids = new IdAllocator();
        var containerIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MermaidSubgraph subgraph in graph.Subgraphs)
        {
            containerIds[subgraph.Id] = ids.Allocate("c_", subgraph.Id, subgraph.Line, warnings);
        }

        foreach (MermaidSubgraph subgraph in graph.Subgraphs)
        {
            nodes.Add(new Node(containerIds[subgraph.Id], MermaidKindMapper.ContainerKind, subgraph.Title, "rectangle")
            {
                Parent = subgraph.Parent is not null && containerIds.TryGetValue(subgraph.Parent, out string? parentId) ? parentId : null,
            });
        }

        var nodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MermaidNode mermaidNode in graph.Nodes)
        {
            if (containerIds.TryGetValue(mermaidNode.Id, out string? containerId))
            {
                // A link to or from a subgraph names the container itself, not a separate node.
                nodeIds[mermaidNode.Id] = containerId;
                continue;
            }

            string id = ids.Allocate("n_", mermaidNode.Id, mermaidNode.Line, warnings);
            nodeIds[mermaidNode.Id] = id;
            KindMapping mapping = MermaidKindMapper.MapNode(options.Type, mermaidNode.Shape);
            if (mapping.Guessed)
            {
                // Said in the author's words: the label they wrote and the shape they drew, not an id they may not remember.
                warnings.Add(new ImportWarning(ImportWarningCodes.KindGuessed,
                    $"\"{mermaidNode.Label ?? mermaidNode.Id}\" is drawn as {DescribeShape(mermaidNode.Shape)}, so it was given the kind '{mapping.Kind}'; change it if that is not what it is.", mermaidNode.Line, id));
            }

            nodes.Add(new Node(id, mapping.Kind, mermaidNode.Label ?? mermaidNode.Id, mapping.Shape)
            {
                Parent = mermaidNode.Subgraph is not null && containerIds.TryGetValue(mermaidNode.Subgraph, out string? parentId) ? parentId : null,
            });
        }

        var nodeKinds = nodes.ToDictionary(n => n.Id, n => n.Kind, StringComparer.Ordinal);
        int edgeNumber = 0;
        foreach (MermaidEdge mermaidEdge in graph.Edges)
        {
            edgeNumber++;
            string id = $"e_{edgeNumber}";
            string from = nodeIds[mermaidEdge.From];
            string to = nodeIds[mermaidEdge.To];
            edges.Add(new Edge(id, EdgeKindFor(options.Type, mermaidEdge, nodeKinds[from], nodeKinds[to]), new Endpoint(from), new Endpoint(to))
            {
                Label = string.IsNullOrEmpty(mermaidEdge.Label) ? null : mermaidEdge.Label,
                Style = EdgeStyleFor(mermaidEdge, id, warnings),
            });
        }

        if (nodes.Count == 0 && graph.UnsupportedDiagramType is null)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.EmptyDiagram, "The source produced no nodes."));
        }

        DateTimeOffset now = options.Now ?? DateTimeOffset.UtcNow;
        var document = new Document(
            $"doc_{Guid.NewGuid():N}",
            Document.CurrentSchemaVersion,
            options.Title ?? graph.Title ?? DefaultTitle,
            options.Type,
            nodes,
            edges,
            new Meta(now, now, CreatedBy));

        return new ImportResult(document, warnings) { Direction = graph.Direction };
    }

    private static string EdgeKindFor(DocumentType type, MermaidEdge edge, string sourceKind, string targetKind)
    {
        bool undirected = edge.HeadStart == MermaidHead.None && edge.HeadEnd == MermaidHead.None;
        if (undirected)
        {
            return "association";
        }

        if (type == DocumentType.Architecture)
        {
            if (edge.Line == MermaidLine.Dotted)
            {
                return "dependency";
            }

            if (sourceKind == "database" || targetKind == "database")
            {
                return "data";
            }
        }

        return "flow";
    }

    /// <summary>Only what deviates from the kind default is written, so a plain arrow produces no style at all.</summary>
    private static EdgeStyle? EdgeStyleFor(MermaidEdge edge, string edgeId, List<ImportWarning> warnings)
    {
        Dash? dash = edge.Line == MermaidLine.Dotted ? Dash.Dashed : null;
        double? width = edge.Line == MermaidLine.Thick ? 3 : null;
        ArrowHead? start = ArrowHeadFor(edge.HeadStart, edgeId, edge.SourceLine, warnings);
        ArrowHead? end = ArrowHeadFor(edge.HeadEnd, edgeId, edge.SourceLine, warnings);
        if (end == ArrowHead.Arrow)
        {
            end = null;
        }

        if (dash is null && width is null && start is null && end is null)
        {
            return null;
        }

        return new EdgeStyle { Dash = dash, StrokeWidth = width, ArrowStart = start, ArrowEnd = end };
    }

    private static ArrowHead? ArrowHeadFor(MermaidHead head, string edgeId, int line, List<ImportWarning> warnings)
    {
        switch (head)
        {
            case MermaidHead.None:
                return null;
            case MermaidHead.Arrow:
                return ArrowHead.Arrow;
            case MermaidHead.Circle:
                return ArrowHead.Circle;
            case MermaidHead.Cross:
                warnings.Add(new ImportWarning(ImportWarningCodes.StyleApproximated,
                    "Cross arrowheads ('--x') have no equivalent; a plain arrowhead was used.", line, edgeId));
                return ArrowHead.Arrow;
            default:
                return null;
        }
    }

    /// <summary>Produces schema-valid, unique element ids from Mermaid ids, warning when either constraint forces a change.</summary>
    internal sealed class IdAllocator(string source = "Mermaid")
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _nextSuffix = new(StringComparer.Ordinal);

        public string Allocate(string prefix, string mermaidId, int? line, List<ImportWarning> warnings)
        {
            string sanitised = Sanitise(mermaidId);
            string candidate = Truncate(prefix + sanitised, MaxIdLength);
            string unique = candidate;
            if (!_used.Add(unique))
            {
                int suffix = _nextSuffix.GetValueOrDefault(candidate, 2);
                do
                {
                    string tail = $"_{suffix++}";
                    unique = Truncate(candidate, MaxIdLength - tail.Length) + tail;
                }
                while (!_used.Add(unique));
                _nextSuffix[candidate] = suffix;
            }

            if (unique != prefix + mermaidId)
            {
                warnings.Add(new ImportWarning(ImportWarningCodes.IdRenamed,
                    $"{source} id '{mermaidId}' became '{unique}' to satisfy the document id rules.", line, unique));
            }

            return unique;
        }

        private static string Sanitise(string id)
        {
            var builder = new StringBuilder(id.Length);
            foreach (char c in id)
            {
                builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_');
            }

            return builder.ToString();
        }

        private static string Truncate(string id, int maxLength)
        {
            return id.Length <= maxLength ? id : id[..maxLength];
        }
    }
}
