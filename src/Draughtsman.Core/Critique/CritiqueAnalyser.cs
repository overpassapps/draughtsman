using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Critique;

/// <summary>
/// A deterministic, model-free critique over a typed graph: it reports the structural defects the kind vocabularies
/// make decidable, each as a <see cref="CritiqueFinding"/> carrying the node and edge ids that are its evidence.
/// There is no judgement here - every rule is a pure function of node/edge kinds and graph topology, so the findings
/// are reproducible and an incumbent that stores pictures rather than a typed graph cannot produce them.
/// </summary>
public static class CritiqueAnalyser
{
    /// <summary>Stable rule ids an agent keys on. They are contract, not copy: rename them only deliberately.</summary>
    public static class Rules
    {
        public const string DanglingEdge = "dangling-edge";
        public const string UnknownColumn = "unknown-column";
        public const string EntityWithoutPrimaryKey = "entity-without-primary-key";
        public const string DanglingParent = "dangling-parent";
        public const string NonContainerParent = "non-container-parent";
        public const string DecisionSingleBranch = "decision-single-branch";
        public const string DecisionBranchUnlabelled = "decision-branch-unlabelled";
        public const string DeadEnd = "dead-end";
        public const string UnreachableNode = "unreachable-node";
        public const string IsolatedNode = "isolated-node";
        public const string EmptyContainer = "empty-container";
        public const string DatabaseUnused = "database-unused";
        public const string UnbalancedActivation = "unbalanced-activation";
        public const string EmptyFragment = "empty-fragment";
        public const string NonContiguousFragment = "non-contiguous-fragment";
        public const string UnknownFragment = "unknown-fragment";
    }

    // Kind names these rules key on, exactly as the shipped vocabularies spell them (flowchart.json / architecture.json).
    // Decorative kinds annotate rather than flow, so the graph rules skip them; container-ness is grouping, not flow.
    internal const string Terminal = "terminal";
    internal const string Process = "process";
    internal const string Decision = "decision";
    internal const string Io = "io";
    internal const string Subprocess = "subprocess";
    internal const string Container = "container";
    internal const string CompositeState = "composite-state";
    internal const string Pool = SwimlaneShapes.PoolKind;
    internal const string Lane = SwimlaneShapes.LaneKind;
    internal const string Database = "database";
    internal const string Note = "note";
    internal const string Fragment = SequenceMessages.FragmentKind;
    internal const string Text = "text";
    internal const string Image = "image";
    internal const string Sticky = "sticky";
    internal const string FlowEdge = "flow";
    internal const string DataEdge = "data";

    internal const string BpmnAnnotation = BpmnShapeLibrary.KindPrefix + "annotation";

    private static readonly HashSet<string> Decorative = new(new[] { Note, Text, Image, Sticky, BpmnAnnotation }, StringComparer.Ordinal);

    /// <summary>Analyse a document and return its findings, sorted by rule then evidence for stable output.</summary>
    public static IReadOnlyList<CritiqueFinding> Analyse(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var findings = new List<CritiqueFinding>();
        Dictionary<string, Node> nodeById = ById(document.Nodes);
        bool hasVocabulary = KindVocabulary.TryGet(document.Type, out _);

        ReportDanglingEdges(document, findings, nodeById);
        ReportUnknownColumns(document, findings, nodeById);
        ReportEntitiesWithoutPrimaryKey(document, findings);
        ReportParentDefects(document, findings, nodeById, hasVocabulary);

        if (hasVocabulary)
        {
            ReportIsolatedAndEmptyContainers(document, findings);
        }

        if (document.Type == DocumentType.Flowchart)
        {
            ReportDecisionDefects(document, findings);
            ReportReachability(document, findings);
        }
        else if (document.Type == DocumentType.Architecture)
        {
            ReportUnusedDatabases(document, findings);
        }
        else if (document.Type == DocumentType.Sequence)
        {
            ReportUnbalancedActivations(document, findings);
            ReportFragmentDefects(document, findings);
        }

        return findings
            .OrderBy(f => f.Rule, StringComparer.Ordinal)
            .ThenBy(f => string.Join(',', f.NodeIds), StringComparer.Ordinal)
            .ThenBy(f => string.Join(',', f.EdgeIds), StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, Node> ById(IReadOnlyList<Node> nodes)
    {
        var byId = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (Node node in nodes)
        {
            if (!byId.TryAdd(node.Id, node))
            {
                throw new InvalidOperationException($"Cannot critique: the document contains more than one node with id '{node.Id}'.");
            }
        }

        return byId;
    }

    private static void ReportDanglingEdges(Document document, List<CritiqueFinding> findings, Dictionary<string, Node> nodeById)
    {
        foreach (Edge edge in document.Edges)
        {
            if (!nodeById.ContainsKey(edge.From.Node))
            {
                findings.Add(new CritiqueFinding(Rules.DanglingEdge, FindingSeverity.Error,
                    $"Edge '{edge.Id}' references source node '{edge.From.Node}', which does not exist.", [], [edge.Id]));
            }

            if (!nodeById.ContainsKey(edge.To.Node))
            {
                findings.Add(new CritiqueFinding(Rules.DanglingEdge, FindingSeverity.Error,
                    $"Edge '{edge.Id}' references target node '{edge.To.Node}', which does not exist.", [], [edge.Id]));
            }
        }
    }

    /// <summary>
    /// A table (an ER entity or weak entity) none of whose columns is marked <c>PK</c>: a table with no key cannot be the
    /// target of a foreign key and its rows cannot be told apart. A table with no columns at all has no key row either.
    /// It is a warning, and it runs on every document type since a freeform document holds entities too.
    /// </summary>
    private static void ReportEntitiesWithoutPrimaryKey(Document document, List<CritiqueFinding> findings)
    {
        foreach (Node node in document.Nodes)
        {
            if (!ErShapeLibrary.IsErShape(node.Shape)
                || ErShapeLibrary.ColumnLines(node.Props).Any(line => ErShapeLibrary.ParseColumn(line).Keys.Contains("PK")))
            {
                continue;
            }

            findings.Add(new CritiqueFinding(Rules.EntityWithoutPrimaryKey, FindingSeverity.Warning,
                $"Table '{node.Label}' ({node.Id}) has no primary key: none of its columns is marked PK.", [node.Id], []));
        }
    }

    /// <summary>
    /// An edge end on a column port (<c>col:&lt;name&gt;</c>, ErShapeLibrary) whose node has no such column, or is not an
    /// entity at all. It is a warning: the renderers draw that end at the table, exactly as an end with no port.
    /// </summary>
    private static void ReportUnknownColumns(Document document, List<CritiqueFinding> findings, Dictionary<string, Node> nodeById)
    {
        foreach (Edge edge in document.Edges)
        {
            foreach ((Endpoint end, string role) in new[] { (edge.From, "source"), (edge.To, "target") })
            {
                if (ErShapeLibrary.IsColumnPortId(end.Port) && nodeById.TryGetValue(end.Node, out Node? node) && ErShapeLibrary.ColumnRowCentre(node, end.Port) is null)
                {
                    string column = ErShapeLibrary.ColumnPortName(end.Port) ?? end.Port!;
                    findings.Add(new CritiqueFinding(Rules.UnknownColumn, FindingSeverity.Warning,
                        $"Edge '{edge.Id}' attaches its {role} to column '{column}' of '{node.Id}', which has no such column, so that end is drawn at the table.", [node.Id], [edge.Id]));
                }
            }
        }
    }

    private static void ReportParentDefects(Document document, List<CritiqueFinding> findings, Dictionary<string, Node> nodeById, bool hasVocabulary)
    {
        foreach (Node node in document.Nodes)
        {
            if (node.Parent is not string parent)
            {
                continue;
            }

            if (!nodeById.TryGetValue(parent, out Node? parentNode))
            {
                findings.Add(new CritiqueFinding(Rules.DanglingParent, FindingSeverity.Error,
                    $"Node '{node.Id}' names parent '{parent}', which does not exist.", [node.Id], []));
            }
            else if (hasVocabulary && !IsContainerKind(parentNode.Kind))
            {
                findings.Add(new CritiqueFinding(Rules.NonContainerParent, FindingSeverity.Error,
                    $"Node '{node.Id}' names parent '{parent}', which is a '{parentNode.Kind}' rather than a container.", [node.Id, parent], []));
            }
        }
    }

    /// <summary>The kinds that may hold children: a generic container, a composite state (a container by meaning), or a swimlane pool or lane.</summary>
    private static bool IsContainerKind(string kind)
    {
        return string.Equals(kind, Container, StringComparison.Ordinal)
            || string.Equals(kind, CompositeState, StringComparison.Ordinal)
            || string.Equals(kind, Fragment, StringComparison.Ordinal)
            || SwimlaneShapes.IsSwimlaneKind(kind);
    }

    /// <summary>The kinds that are a grouping to name as empty and to keep out of the isolated-node rule: a generic container, pool or lane.</summary>
    private static bool IsGroupingKind(string kind)
    {
        return string.Equals(kind, Container, StringComparison.Ordinal) || SwimlaneShapes.IsSwimlaneKind(kind);
    }

    private static void ReportIsolatedAndEmptyContainers(Document document, List<CritiqueFinding> findings)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        var referencedBy = new HashSet<string>(StringComparer.Ordinal);
        foreach (Edge edge in document.Edges)
        {
            references.Add(edge.From.Node);
            referencedBy.Add(edge.To.Node);
        }

        Dictionary<string, int> children = document.Nodes
            .Where(n => n.Parent is not null)
            .GroupBy(n => n.Parent!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (Node node in document.Nodes)
        {
            if (IsGroupingKind(node.Kind))
            {
                if (!children.ContainsKey(node.Id))
                {
                    findings.Add(new CritiqueFinding(Rules.EmptyContainer, FindingSeverity.Warning,
                        $"Container '{node.Id}' has no node inside it.", [node.Id], []));
                }

                continue;
            }

            // A combined fragment holds messages by their `in` prop, not by an edge of its own: empty-fragment is its rule.
            if (Decorative.Contains(node.Kind) || string.Equals(node.Kind, Fragment, StringComparison.Ordinal))
            {
                continue;
            }

            if (!references.Contains(node.Id) && !referencedBy.Contains(node.Id))
            {
                findings.Add(new CritiqueFinding(Rules.IsolatedNode, FindingSeverity.Warning,
                    $"Node '{node.Id}' has no edges connecting it to the rest of the diagram.", [node.Id], []));
            }
        }
    }

    private static void ReportDecisionDefects(Document document, List<CritiqueFinding> findings)
    {
        foreach (Node node in document.Nodes.Where(n => string.Equals(n.Kind, Decision, StringComparison.Ordinal)))
        {
            List<Edge> branches = document.Edges
                .Where(e => string.Equals(e.From.Node, node.Id, StringComparison.Ordinal) && string.Equals(e.Kind, FlowEdge, StringComparison.Ordinal))
                .ToList();

            if (branches.Count < 2)
            {
                findings.Add(new CritiqueFinding(Rules.DecisionSingleBranch, FindingSeverity.Warning,
                    $"Decision '{node.Id}' has {DescribeCount(branches.Count)}; a decision needs at least two outgoing branches.",
                    [node.Id], branches.Select(b => b.Id).ToList()));
            }

            foreach (Edge branch in branches.Where(b => string.IsNullOrEmpty(b.Label)))
            {
                findings.Add(new CritiqueFinding(Rules.DecisionBranchUnlabelled, FindingSeverity.Warning,
                    $"The branch from decision '{node.Id}' on edge '{branch.Id}' has no label for its condition.",
                    [node.Id], [branch.Id]));
            }
        }
    }

    private static void ReportReachability(Document document, List<CritiqueFinding> findings)
    {
        HashSet<string> validNodes = document.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var outgoingFlow = document.Nodes.ToDictionary(n => n.Id, _ => new List<Edge>(), StringComparer.Ordinal);
        var incomingFlow = document.Nodes.ToDictionary(n => n.Id, _ => new List<Edge>(), StringComparer.Ordinal);

        foreach (Edge edge in document.Edges.Where(e => validNodes.Contains(e.From.Node) && validNodes.Contains(e.To.Node)))
        {
            if (!string.Equals(edge.Kind, FlowEdge, StringComparison.Ordinal))
            {
                continue;
            }

            outgoingFlow[edge.From.Node].Add(edge);
            incomingFlow[edge.To.Node].Add(edge);
        }

        // Where a flow begins: a terminal, a BPMN start event, or a BPMN intermediate event nothing flows into (an error
        // boundary event, or a catch event a message or a link arrives at, is an entry point of its own path).
        List<Node> entries = document.Nodes.Where(n => IsFlowEntry(n, incomingFlow)).ToList();
        if (entries.Count == 0)
        {
            // Without a start there is nothing to be unreachable from; isolated nodes still catch the fully-disconnected case.
            return;
        }

        HashSet<string> reachable = ReachableFrom(entries.Select(n => n.Id), outgoingFlow);

        foreach (Node node in document.Nodes)
        {
            bool isBpmnActivity = BpmnShapeLibrary.IsKindOfFamily(node.Kind, BpmnShapeLibrary.ActivityFamily);
            bool isFlowNode = string.Equals(node.Kind, Process, StringComparison.Ordinal)
                || string.Equals(node.Kind, Decision, StringComparison.Ordinal)
                || string.Equals(node.Kind, Io, StringComparison.Ordinal)
                || string.Equals(node.Kind, Subprocess, StringComparison.Ordinal)
                || isBpmnActivity
                || BpmnShapeLibrary.IsKindOfFamily(node.Kind, BpmnShapeLibrary.GatewayFamily);
            if (!isFlowNode)
            {
                continue;
            }

            bool hasEdges = incomingFlow[node.Id].Count > 0 || outgoingFlow[node.Id].Count > 0;
            if (!hasEdges)
            {
                continue;
            }

            if (!reachable.Contains(node.Id))
            {
                findings.Add(new CritiqueFinding(Rules.UnreachableNode, FindingSeverity.Warning,
                    $"Node '{node.Id}' cannot be reached from any terminal.", [node.Id], []));
            }
            // An io node is a legitimate sink (output written, report printed), so only mid-flow steps can dead-end.
            else if ((string.Equals(node.Kind, Process, StringComparison.Ordinal) || string.Equals(node.Kind, Subprocess, StringComparison.Ordinal) || isBpmnActivity)
                     && incomingFlow[node.Id].Count > 0 && outgoingFlow[node.Id].Count == 0)
            {
                findings.Add(new CritiqueFinding(Rules.DeadEnd, FindingSeverity.Warning,
                    $"Flow reaches '{node.Id}' and then stops; it should continue to another step or a terminal.", [node.Id], []));
            }
        }
    }

    private static bool IsFlowEntry(Node node, Dictionary<string, List<Edge>> incomingFlow)
    {
        return string.Equals(node.Kind, Terminal, StringComparison.Ordinal)
            || BpmnShapeLibrary.IsStartEventKind(node.Kind)
            || (BpmnShapeLibrary.IsIntermediateEventKind(node.Kind) && incomingFlow[node.Id].Count == 0);
    }

    private static void ReportUnusedDatabases(Document document, List<CritiqueFinding> findings)
    {
        foreach (Node node in document.Nodes.Where(n => string.Equals(n.Kind, Database, StringComparison.Ordinal)))
        {
            bool isUsed = document.Edges.Any(e =>
                string.Equals(e.Kind, DataEdge, StringComparison.Ordinal)
                && (string.Equals(e.From.Node, node.Id, StringComparison.Ordinal) || string.Equals(e.To.Node, node.Id, StringComparison.Ordinal)));

            if (!isUsed)
            {
                findings.Add(new CritiqueFinding(Rules.DatabaseUnused, FindingSeverity.Warning,
                    $"Database '{node.Id}' has no data edge, so nothing reads or writes it.", [node.Id], []));
            }
        }
    }

    /// <summary>
    /// A sequence diagram's activation bars are drawn from the messages' <c>activate</c> and <c>deactivate</c> props
    /// (web/src/model/sequenceScene.ts is the drawing side of this walk): <c>activate</c> opens a bar on the receiver,
    /// <c>deactivate</c> closes the sender's innermost open bar. A deactivate with nothing open is ignored by the
    /// drawing, and a bar still open at the last message is drawn to it; both are warnings here, evidence being the
    /// message and the participant, so the author can either pair them or drop the stray prop.
    /// </summary>
    private static void ReportUnbalancedActivations(Document document, List<CritiqueFinding> findings)
    {
        var open = new Dictionary<string, Stack<string>>(StringComparer.Ordinal);
        foreach (Edge edge in document.Edges)
        {
            if (SequenceMessages.IsSet(edge, SequenceMessages.Activate))
            {
                if (!open.TryGetValue(edge.To.Node, out Stack<string>? receiver))
                {
                    receiver = new Stack<string>();
                    open[edge.To.Node] = receiver;
                }

                receiver.Push(edge.Id);
            }

            if (SequenceMessages.IsSet(edge, SequenceMessages.Deactivate))
            {
                if (open.TryGetValue(edge.From.Node, out Stack<string>? sender) && sender.Count > 0)
                {
                    sender.Pop();
                }
                else
                {
                    findings.Add(new CritiqueFinding(Rules.UnbalancedActivation, FindingSeverity.Warning,
                        $"Message '{edge.Id}' deactivates '{edge.From.Node}', which has no open activation at that point, so it is ignored.", [edge.From.Node], [edge.Id]));
                }
            }

            // A destroy ends its target's lifeline, and every bar still open on it, so none of them is left unclosed.
            if (string.Equals(edge.Kind, SequenceMessages.DestroyKind, StringComparison.Ordinal) && open.TryGetValue(edge.To.Node, out Stack<string>? destroyed))
            {
                destroyed.Clear();
            }
        }

        foreach ((string participant, Stack<string> stack) in open.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (string messageId in stack.Reverse())
            {
                findings.Add(new CritiqueFinding(Rules.UnbalancedActivation, FindingSeverity.Warning,
                    $"Message '{messageId}' activates '{participant}' but nothing deactivates it afterwards, so the bar runs to the last message.", [participant], [messageId]));
            }
        }
    }

    /// <summary>
    /// A combined fragment's frame is derived from the messages that name it (<c>in</c>), which an agent writes by hand,
    /// so three things can be wrong that the model cannot reject. <c>empty-fragment</c>: no message is a member, directly or
    /// through a fragment nested in it, so it draws as an empty frame after the last row. <c>non-contiguous-fragment</c>:
    /// a message that is not a member sits between the first and last member, so the frame draws around it too; the
    /// evidence is the fragment and the messages that intrude. <c>unknown-fragment</c>: a message's <c>in</c> names no
    /// fragment (it is ignored, the message draws outside every frame), or a fragment's operator is none of the
    /// standard ones (it draws whatever it says, so this is a heads-up for a typo such as "alr"). All are warnings.
    /// </summary>
    private static void ReportFragmentDefects(Document document, List<CritiqueFinding> findings)
    {
        Dictionary<string, Node> fragments = document.Nodes
            .Where(n => string.Equals(n.Kind, Fragment, StringComparison.Ordinal))
            .GroupBy(n => n.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // The fragments each message is in: its own and every fragment it is nested in, the same chain the drawing walks.
        var membership = new List<(Edge Edge, HashSet<string> Fragments)>();
        foreach (Edge edge in document.Edges)
        {
            var chain = new HashSet<string>(StringComparer.Ordinal);
            if (SequenceMessages.FragmentOf(edge) is { } inside)
            {
                if (fragments.ContainsKey(inside))
                {
                    string? current = inside;
                    while (current is not null && fragments.TryGetValue(current, out Node? node) && chain.Add(current))
                    {
                        current = node.Parent;
                    }
                }
                else
                {
                    findings.Add(new CritiqueFinding(Rules.UnknownFragment, FindingSeverity.Warning,
                        $"Message '{edge.Id}' is in '{inside}', which is not a fragment node of this document, so it is drawn outside every frame.", [], [edge.Id]));
                }
            }

            membership.Add((edge, chain));
        }

        foreach (Node fragment in fragments.Values)
        {
            List<int> members = Enumerable.Range(0, membership.Count).Where(i => membership[i].Fragments.Contains(fragment.Id)).ToList();
            if (members.Count == 0)
            {
                findings.Add(new CritiqueFinding(Rules.EmptyFragment, FindingSeverity.Warning,
                    $"Fragment '{fragment.Id}' has no message in it, so it is drawn as an empty frame after the last message.", [fragment.Id], []));
            }
            else
            {
                List<string> intruders = Enumerable.Range(members[0], members[^1] - members[0] + 1)
                    .Where(i => !membership[i].Fragments.Contains(fragment.Id))
                    .Select(i => membership[i].Edge.Id)
                    .ToList();
                if (intruders.Count > 0)
                {
                    findings.Add(new CritiqueFinding(Rules.NonContiguousFragment, FindingSeverity.Warning,
                        $"Fragment '{fragment.Id}' has messages that are not in it between its first and last ({string.Join(", ", intruders)}), so its frame is drawn around them too; keep a fragment's messages next to each other.", [fragment.Id], intruders));
                }
            }

            string @operator = SequenceMessages.OperatorOf(fragment);
            if (!SequenceMessages.KnownOperators.Contains(@operator))
            {
                findings.Add(new CritiqueFinding(Rules.UnknownFragment, FindingSeverity.Warning,
                    $"Fragment '{fragment.Id}' has operator '{@operator}', which is not one of the standard ones ({string.Join(", ", SequenceMessages.KnownOperators.Order(StringComparer.Ordinal))}); it is drawn as written.", [fragment.Id], []));
            }
        }
    }

    private static HashSet<string> ReachableFrom(IEnumerable<string> starts, Dictionary<string, List<Edge>> outgoingFlow)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(starts.Where(start => outgoingFlow.ContainsKey(start)));

        while (queue.Count > 0)
        {
            string nodeId = queue.Dequeue();
            if (!reachable.Add(nodeId))
            {
                continue;
            }

            foreach (Edge edge in outgoingFlow[nodeId])
            {
                if (!reachable.Contains(edge.To.Node))
                {
                    queue.Enqueue(edge.To.Node);
                }
            }
        }

        return reachable;
    }

    private static string DescribeCount(int count)
    {
        return count == 0 ? "no outgoing branch" : "only one outgoing branch";
    }
}
