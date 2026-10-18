namespace Draughtsman.Core.Import;

/// <summary>Mermaid shapes the flowchart parser recognises, named as Mermaid's own docs name them.</summary>
internal enum MermaidShape
{
    Rectangle,
    Rounded,
    Stadium,
    Subroutine,
    Cylinder,
    Circle,
    DoubleCircle,
    Asymmetric,
    Rhombus,
    Hexagon,
    Parallelogram,
}

/// <summary>Line style of a Mermaid link.</summary>
internal enum MermaidLine
{
    Solid,
    Dotted,
    Thick,
}

/// <summary>Arrowhead at one end of a Mermaid link.</summary>
internal enum MermaidHead
{
    None,
    Arrow,
    Circle,
    Cross,
}

/// <summary>A node as declared or referenced in the source. Mutable while parsing because later statements refine earlier mentions.</summary>
internal sealed class MermaidNode
{
    public MermaidNode(string id, int line)
    {
        Id = id;
        Line = line;
    }

    public string Id { get; }

    public int Line { get; }

    /// <summary>Null until a statement gives the node text; the importer then falls back to the id.</summary>
    public string? Label { get; set; }

    /// <summary>Null until a statement gives the node a shape; the importer then treats it as a plain rectangle.</summary>
    public MermaidShape? Shape { get; set; }

    /// <summary>
    /// Id of the subgraph this node belongs to, or null at top level. Settled when blocks close, as in Mermaid: the first
    /// block to close that mentioned the node claims it, so a nested block beats its parent and an earlier sequential
    /// block beats a later one.
    /// </summary>
    public string? Subgraph { get; set; }
}

internal sealed record MermaidEdge(
    string From,
    string To,
    string? Label,
    MermaidLine Line,
    MermaidHead HeadStart,
    MermaidHead HeadEnd,
    int SourceLine);

internal sealed class MermaidSubgraph
{
    public MermaidSubgraph(string id, string title, string? parent, int line)
    {
        Id = id;
        Title = title;
        Parent = parent;
        Line = line;
    }

    public string Id { get; }

    public string Title { get; }

    public string? Parent { get; }

    public int Line { get; }
}

/// <summary>Everything the parser extracted, in source order, before any mapping into the document model.</summary>
internal sealed class MermaidGraph
{
    public string? Direction { get; set; }

    public string? Title { get; set; }

    /// <summary>Set when the header names a diagram type other than flowchart/graph; nothing else is then parsed.</summary>
    public string? UnsupportedDiagramType { get; set; }

    private readonly List<MermaidNode> _nodes = new();
    private readonly Dictionary<string, MermaidNode> _nodesById = new(StringComparer.Ordinal);
    private readonly List<MermaidSubgraph> _subgraphs = new();
    private readonly HashSet<string> _subgraphIds = new(StringComparer.Ordinal);

    public IReadOnlyList<MermaidNode> Nodes => _nodes;

    public List<MermaidEdge> Edges { get; } = new();

    public IReadOnlyList<MermaidSubgraph> Subgraphs => _subgraphs;

    public List<ImportWarning> Warnings { get; } = new();

    /// <summary>Adds the subgraph in source order; returns false, adding nothing, when one with the same id already exists.</summary>
    public bool TryAddSubgraph(MermaidSubgraph subgraph)
    {
        if (!_subgraphIds.Add(subgraph.Id))
        {
            return false;
        }

        _subgraphs.Add(subgraph);
        return true;
    }

    /// <summary>Returns the node with this id, creating it on first mention; nodes stay in first-mention order.</summary>
    public MermaidNode GetOrAddNode(string id, int line)
    {
        if (!_nodesById.TryGetValue(id, out MermaidNode? node))
        {
            node = new MermaidNode(id, line);
            _nodesById[id] = node;
            _nodes.Add(node);
        }

        return node;
    }
}
