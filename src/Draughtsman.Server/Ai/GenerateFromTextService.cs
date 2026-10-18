using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Ai;
using Draughtsman.Ai.Prompting;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Diffing;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Core.Text;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Mcp.Tools;
using Draughtsman.Sidecar;
using ModelContextProtocol;

namespace Draughtsman.Server.Ai;

/// <summary>
/// "Generate from text" (build-spec-v0.1.md section 9, MVP AI feature 1): a description becomes Draughtsman DSL via
/// the configured <see cref="IModelProvider"/>, parsed with the same <see cref="Dsl"/> parser every other DSL
/// caller uses (one retry with the parse error if the first attempt does not parse), then written through <see
/// cref="DraughtsmanTools"/> - the same create_document/patch_document path an MCP agent uses - so the write is
/// checkpointed and its <c>aiProvenance</c> names this provider and model.
///
/// Two modes, chosen by whether <see cref="GenerateFromTextRequest.DocumentId"/> is given:
/// - Create (no documentId): the model writes a brand-new document from scratch. Laid out through the sidecar
///   (every node is new, so every position is), then written with <c>create_document</c>.
/// - Update (a documentId): the model is shown the target's exact current DSL and asked for the complete updated
///   document, so it can restructure the diagram - group existing nodes into new containers, relabel, reconnect,
///   remove - not just append to it. The reply is diffed against the document it was built from (<see
///   cref="DocumentDiff"/>) and applied as addressable <c>patch_document</c> ops, so an id the reply kept unchanged
///   really does keep its stored geometry, style and props untouched. Geometry is adjusted only where the reply
///   changed the structure: a new container with no geometry of its own is fitted around its children's existing
///   boxes (<see cref="FitNewContainers"/>), a brand-new node the reply left positionless is placed in free space
///   (<see cref="PlaceNewNodes"/>), and a container that gained a child is grown to enclose it (<see
///   cref="GrowContainersThatGainedChildren"/>), all without running layout, which would recompute every node's
///   position and move things the reply never touched.
/// </summary>
public sealed class GenerateFromTextService
{
    private const int MaxDescriptionLength = 20_000;

    // Two grid steps at the shipped default diagram theme's grid size of 20 (web/src/model/grouping.ts's
    // CONTAINER_PADDING_STEPS, which fits a container the same way when a person groups nodes by hand); the server
    // has no theme loaded here to read the real value from.
    private const double ContainerFitPadding = 40;

    // A new container nested inside another new container needs more than one pass to have both fitted; five passes
    // is far deeper nesting than any real diagram request produces.
    private const int MaxContainerFitPasses = 5;

    // The size the sidecar's layout gives a geometry-less node (sidecar/src/layout/elk.ts DEFAULT_SIZE, 160x80), the
    // same size the DSL gives `@ x,y` with no `wxh`, and the gap left between a newly placed node and the shape it
    // is placed beside. The server has no theme loaded here to read the grid size from, so the gap matches
    // ContainerFitPadding's two grid steps.
    private const double DefaultNodeWidth = 160;
    private const double DefaultNodeHeight = 80;
    private const double PlacementGap = 40;

    /// <summary>How many new, unplaced, top-level nodes make a block worth laying out together (PlaceNewBlock).</summary>
    private const int MinimumBlock = 3;

    // How far the free-space search walks (in whole node steps) before placing the node anyway; the plane is
    // unbounded, so in practice the first candidate is free and this is only a backstop.
    private const int MaxPlacementSteps = 200;

    // Mirrors Draughtsman.Mcp.Patching.PatchApplier's own field lists: only these fields can move through
    // updateNode/updateEdge, so a changed style or props field below goes through restyle/setProp/removeProp instead.
    private static readonly string[] NodeUpdatableFields = ["kind", "label", "shape", "geometry", "parent", "ports"];
    private static readonly string[] EdgeUpdatableFields = ["kind", "label", "from", "to", "routing"];

    private readonly IModelProvider? _provider;
    private readonly IStore _store;
    private readonly ISidecarClient _sidecar;
    private readonly DraughtsmanTools _tools;

    public GenerateFromTextService(IStore store, ISidecarClient sidecar, IPdfRenderer pdf, IAssetReader assets, IActorContext actorContext, IModelProvider? provider = null)
    {
        _provider = provider;
        _store = store;
        _sidecar = sidecar;
        _tools = new DraughtsmanTools(store, sidecar, pdf, assets, actorContext);
    }

    public bool IsAvailable => _provider is not null;

    public async Task<GenerateFromTextResult> GenerateAsync(GenerateFromTextRequest request, CancellationToken cancellationToken)
    {
        if (_provider is null)
        {
            throw new InvalidOperationException("GenerateAsync was called while no AI provider is configured; callers must check IsAvailable first.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new GenerateFromTextValidationException("description must not be empty.");
        }

        if (request.Description.Length > MaxDescriptionLength)
        {
            throw new GenerateFromTextValidationException($"description is limited to {MaxDescriptionLength:N0} characters.");
        }

        DocumentType type = ParseGeneratableType(request.DiagramType);

        Document? existing = null;
        if (!string.IsNullOrWhiteSpace(request.DocumentId))
        {
            existing = await _store.GetDocument(request.DocumentId, cancellationToken)
                ?? throw new GenerateFromTextDocumentNotFoundException(request.DocumentId);
        }

        // Privacy (build-spec-v0.1.md section 9): only the prompt, and, in update mode, the target document's own
        // content go to the model - never any other document. Update mode sends the whole document as DSL (exactly
        // what read_document format=dsl would show), not the outline the old add-only prompt sent, because
        // restructuring a diagram needs to see what is already there in order to keep it.
        string systemPrompt = existing is null
            ? GenerateFromTextPrompt.BuildSystemPrompt(type)
            : GenerateFromTextPrompt.BuildUpdateSystemPrompt(type, Dsl.Print(existing));
        var messages = new List<ModelMessage> { new(ModelRole.User, request.Description) };

        string firstAttempt = await _provider.CompleteAsync(systemPrompt, messages, cancellationToken);
        ParseResult parsed;
        try
        {
            parsed = Dsl.Parse(StripCodeFence(firstAttempt));
        }
        catch (DslParseException firstError)
        {
            messages.Add(new ModelMessage(ModelRole.Assistant, firstAttempt));
            messages.Add(new ModelMessage(ModelRole.User, GenerateFromTextPrompt.BuildRetryUserMessage(firstAttempt, firstError)));
            string retryAttempt = await _provider.CompleteAsync(systemPrompt, messages, cancellationToken);
            try
            {
                parsed = Dsl.Parse(StripCodeFence(retryAttempt));
            }
            catch (DslParseException secondError)
            {
                throw new GenerateFromTextParseException(firstError, secondError);
            }
        }

        string provider = _provider.Provider;
        string model = _provider.Model;
        string actor = $"agent:{provider}/{model}";

        return existing is null
            ? await CreateFromScratch(request, parsed.Document, actor, provider, model, cancellationToken)
            : await ApplyUpdate(existing, parsed.Document, request.DocumentId!, actor, provider, model, cancellationToken);
    }

    private async Task<GenerateFromTextResult> CreateFromScratch(GenerateFromTextRequest request, Document generatedDocument, string actor, string provider, string model, CancellationToken cancellationToken)
    {
        LayoutResult layout = await LayoutOrDegrade(generatedDocument, cancellationToken);
        Document generated = layout.Document;

        CreateDocumentResult created;
        try
        {
            created = await _tools.CreateDocument(
                DeriveTitle(request.Description), request.DiagramType, Dsl.Print(generated), "dsl", layout: false, actor, cancellationToken);
        }
        catch (McpException e)
        {
            throw new GenerateFromTextWriteException(e.Message, e);
        }

        Document stored = await _store.GetDocument(created.Id, cancellationToken)
            ?? throw new InvalidOperationException($"create_document reported id '{created.Id}' but the document could not be read back.");
        return new GenerateFromTextResult(stored, [.. layout.Warnings, .. created.Warnings], NodeIds(generated), EdgeIds(generated), 0, 0, provider, model);
    }

    private async Task<GenerateFromTextResult> ApplyUpdate(Document existing, Document generatedDocument, string documentId, string actor, string provider, string model, CancellationToken cancellationToken)
    {
        // Geometry the reply left alone is never touched; the three steps below only ever add a position to a new,
        // geometry-less node, grow a container that gained a child, and fit a new container around placed children.
        // A sequence diagram has no stored geometry to fit, place or grow (its picture is derived), and writing one would
        // put a position on a lifeline that is ignored and never meant to exist.
        Document updated = generatedDocument;
        IReadOnlyList<string> layoutWarnings = [];
        if (generatedDocument.Type != DocumentType.Sequence)
        {
            if (MostlyNew(existing, generatedDocument))
            {
                // An empty document, or one the reply mostly replaced: placing each new node beside its neighbours
                // walks a chain of them into one very long row, so lay the whole diagram out like a create would.
                LayoutResult relaid = await LayoutOrDegrade(generatedDocument, cancellationToken);
                updated = relaid.Document;
                layoutWarnings = relaid.Warnings;
            }
            else
            {
                (updated, layoutWarnings) = await PlaceNewBlock(existing, updated, cancellationToken);
                updated = FitNewContainers(updated);
                updated = PlaceNewNodes(existing, updated);
                updated = GrowContainersThatGainedChildren(existing, updated);
            }
        }

        DocumentDiffResult diff;
        try
        {
            diff = DocumentDiff.Compare(existing, updated);
        }
        catch (InvalidOperationException e)
        {
            throw new GenerateFromTextWriteException(e.Message, e);
        }

        JsonArray ops = BuildUpdateOps(diff, existing, updated);

        PatchDocumentResult patched;
        try
        {
            // The whole reply has already been fitted and diffed above, so layout never runs here: it would
            // recompute every node's position (ISidecarClient.LayoutAsync's contract), overwriting the geometry this
            // diff went out of its way to leave untouched for anything the reply did not change.
            patched = await _tools.PatchDocument(documentId, ops.ToJsonString(), layout: "none", actor, cancellationToken);
        }
        catch (McpException e)
        {
            throw new GenerateFromTextWriteException(e.Message, e);
        }

        return new GenerateFromTextResult(
            patched.Document,
            [.. layoutWarnings, .. patched.Warnings],
            [.. diff.AddedNodes.Select(n => n.Id)],
            [.. diff.AddedEdges.Select(e => e.Id)],
            diff.ChangedNodes.Count + diff.ChangedEdges.Count,
            diff.RemovedNodes.Count + diff.RemovedEdges.Count,
            provider,
            model);
    }

    /// <summary>
    /// Whether the reply is, in effect, a new diagram: the document held no nodes, or most of the nodes now in it are
    /// ones the reply added (strictly more than half), so keeping the few that were there where they are is not worth
    /// the shape a node-by-node placement gives the rest.
    /// </summary>
    private static bool MostlyNew(Document existing, Document generated)
    {
        if (existing.Nodes.Count == 0)
        {
            return true;
        }

        var existingIds = new HashSet<string>(existing.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        int added = generated.Nodes.Count(n => !existingIds.Contains(n.Id));
        return added * 2 > generated.Nodes.Count;
    }

    /// <summary>
    /// A handful of new, unplaced, top-level nodes that the reply wired together are laid out as a block of their own
    /// (layered layout over just those nodes and the edges between them) and put beside the existing diagram, instead of
    /// being walked one by one into a row. Fewer than <see cref="MinimumBlock"/> new nodes, a node inside an existing
    /// container, or an existing node never take part: those keep PlaceNewNodes' neighbour-by-neighbour placement.
    /// </summary>
    private async Task<(Document Document, IReadOnlyList<string> Warnings)> PlaceNewBlock(Document existing, Document document, CancellationToken cancellationToken)
    {
        var existingIds = new HashSet<string>(existing.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        List<Node> block = [.. document.Nodes.Where(n => !existingIds.Contains(n.Id) && n.Geometry is null && n.Parent is null)];
        var blockIds = new HashSet<string>(block.Select(n => n.Id), StringComparer.Ordinal);
        // A new container among them (or a child naming a new node as its parent) is a hierarchy PlaceNewNodes already fits.
        bool nested = document.Nodes.Any(n => n.Parent is { } parent && blockIds.Contains(parent));
        List<Box> placed = [.. document.Nodes.Where(n => n.Geometry is not null).Select(n => BoxOf(n.Geometry!))];
        if (block.Count < MinimumBlock || nested || placed.Count == 0)
        {
            return (document, []);
        }

        List<Edge> inside = [.. document.Edges.Where(e => blockIds.Contains(e.From.Node) && blockIds.Contains(e.To.Node))];
        LayoutResult laid = await LayoutOrDegrade(document with { Nodes = block, Edges = inside }, cancellationToken);
        if (laid.Document.Nodes.Any(n => n.Geometry is null))
        {
            return (document, laid.Warnings);
        }

        Box all = Union(placed);
        Box blockBox = Union([.. laid.Document.Nodes.Select(n => BoxOf(n.Geometry!))]);
        double dx = all.Right + (PlacementGap * 2) - blockBox.X;
        double dy = all.Y - blockBox.Y;
        Dictionary<string, Geometry> moved = laid.Document.Nodes.ToDictionary(
            n => n.Id,
            n => n.Geometry! with { X = n.Geometry!.X + dx, Y = n.Geometry.Y + dy },
            StringComparer.Ordinal);
        return (document with
        {
            Nodes = [.. document.Nodes.Select(n => moved.TryGetValue(n.Id, out Geometry? geometry) ? n with { Geometry = geometry } : n)],
        }, laid.Warnings);
    }

    private async Task<LayoutResult> LayoutOrDegrade(Document document, CancellationToken cancellationToken)
    {
        if (document.Type == DocumentType.Sequence)
        {
            // A sequence diagram's layout is derived on every render: there is nothing stored to place, and no sidecar to ask.
            return new LayoutResult(document, []);
        }

        try
        {
            LayoutResult laid = await _sidecar.LayoutAsync(document, null, cancellationToken);
            // ELK's bend points are not kept: they run from the middle of a box side, while the canvas attaches a connector to
            // the outline of a diamond or pill, so a pinned route leaves a short diagonal stub at that end. Left unpinned,
            // the canvas's own router draws the connector square from where it really attaches.
            return laid with { Document = laid.Document with { Edges = [.. laid.Document.Edges.Select(Unpinned)] } };
        }
        catch (SidecarException e)
        {
            throw new GenerateFromTextLayoutException(e);
        }
    }

    private static Edge Unpinned(Edge edge) =>
        edge.Routing is { Mode: RoutingMode.Orthogonal, Waypoints: not null } routing ? edge with { Routing = routing with { Waypoints = null } } : edge;

    /// <summary>A model reply wrapped in a ```dsl fenced block, despite being told not to: unwrap it rather than fail on the fence lines.</summary>
    private static string StripCodeFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        int firstNewline = trimmed.IndexOf('\n');
        int closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstNewline < 0 || closingFence <= firstNewline)
        {
            return text;
        }

        return trimmed[(firstNewline + 1)..closingFence];
    }

    private static string DeriveTitle(string description)
    {
        string firstLine = description.AsSpan().TrimStart().ToString();
        int newline = firstLine.IndexOf('\n');
        if (newline >= 0)
        {
            firstLine = firstLine[..newline];
        }

        firstLine = firstLine.Trim();
        const int maxTitleLength = 80;
        if (firstLine.Length == 0)
        {
            return "Generated diagram";
        }

        return firstLine.Length > maxTitleLength ? firstLine[..maxTitleLength].TrimEnd() : firstLine;
    }

    /// <summary>
    /// Gives a newly added, geometry-less container the bounding box of its children that already have a position
    /// (existing children kept their geometry from <paramref name="document"/>'s own DSL; a sibling new node with no
    /// geometry of its own is skipped, not treated as a zero-size point), padded like a hand-drawn group
    /// (<see cref="ContainerFitPadding"/>). A container whose children are all new and geometry-less is left with no
    /// geometry of its own too - the same "waiting for layout" state any DSL-imported node without <c>@ x,y</c> is
    /// in, which the canvas already renders with a provisional fallback position.
    /// </summary>
    private static Document FitNewContainers(Document document)
    {
        for (int pass = 0; pass < MaxContainerFitPasses; pass++)
        {
            (document, bool changed) = FitNewContainersOnce(document);
            if (!changed)
            {
                break;
            }
        }

        return document;
    }

    private static (Document Document, bool Changed) FitNewContainersOnce(Document document)
    {
        var childrenByParent = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes)
        {
            if (node.Parent is not { } parent)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(parent, out List<Node>? children))
            {
                children = [];
                childrenByParent[parent] = children;
            }

            children.Add(node);
        }

        bool changed = false;
        var nodes = new List<Node>(document.Nodes.Count);
        foreach (Node node in document.Nodes)
        {
            if (node.Geometry is null && childrenByParent.TryGetValue(node.Id, out List<Node>? children) && BoundingBox(children) is { } fitted)
            {
                nodes.Add(node with { Geometry = fitted });
                changed = true;
            }
            else
            {
                nodes.Add(node);
            }
        }

        return (changed ? document with { Nodes = nodes } : document, changed);
    }

    private static Geometry? BoundingBox(IReadOnlyList<Node> children)
    {
        List<Geometry> boxes = [.. children.Where(c => c.Geometry is not null).Select(c => c.Geometry!)];
        if (boxes.Count == 0)
        {
            return null;
        }

        double minX = boxes.Min(b => b.X);
        double minY = boxes.Min(b => b.Y);
        double maxX = boxes.Max(b => b.X + b.W);
        double maxY = boxes.Max(b => b.Y + b.H);
        return new Geometry(minX - ContainerFitPadding, minY - ContainerFitPadding, (maxX - minX) + (ContainerFitPadding * 2), (maxY - minY) + (ContainerFitPadding * 2));
    }

    /// <summary>
    /// Positions every brand-new node the reply left without geometry (checklist: brand-new stand-alone shapes added
    /// during an update must not land on top of existing ones). A node whose parent is a still-unplaced container has
    /// nowhere sensible to go yet and is left geometry-less, the same "waiting for layout" state any DSL-imported node
    /// without <c>@ x,y</c> is in, which the canvas already renders with a provisional fallback position.
    ///
    /// Anchors: a node inside a placed container starts at that container's padded top-left; a node connected by an
    /// edge to a placed neighbour starts just to the right of their union box; an unconnected node starts beside the
    /// whole diagram's bounding box. From there it walks right, then down, in whole node steps until its box overlaps
    /// no placed shape. Existing nodes never move (their geometry is absolute and untouched).
    /// </summary>
    private static Document PlaceNewNodes(Document existing, Document document)
    {
        var existingIds = new HashSet<string>(existing.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        var nodesById = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes)
        {
            nodesById.TryAdd(node.Id, node);
        }

        List<Node> unplaced = [.. document.Nodes.Where(n => !existingIds.Contains(n.Id) && n.Geometry is null)];
        if (unplaced.Count == 0)
        {
            return document;
        }

        var placedBoxes = new List<(string Id, Box Box)>();
        foreach (Node node in document.Nodes)
        {
            if (node.Geometry is { } geometry)
            {
                placedBoxes.Add((node.Id, BoxOf(geometry)));
            }
        }

        var geometryById = new Dictionary<string, Geometry>(StringComparer.Ordinal);
        foreach (Node node in unplaced)
        {
            if (node.Parent is { } parent && (!nodesById.TryGetValue(parent, out Node? parentNode) || parentNode.Geometry is null))
            {
                continue;
            }

            Geometry geometry = FindFreePlacement(node, document, nodesById, placedBoxes);
            geometryById[node.Id] = geometry;
            placedBoxes.Add((node.Id, BoxOf(geometry)));
            nodesById[node.Id] = node with { Geometry = geometry };
        }

        if (geometryById.Count == 0)
        {
            return document;
        }

        return document with
        {
            Nodes = [.. document.Nodes.Select(n => geometryById.TryGetValue(n.Id, out Geometry? geometry) ? n with { Geometry = geometry } : n)],
        };
    }

    private static Geometry FindFreePlacement(Node node, Document document, IReadOnlyDictionary<string, Node> nodesById, IReadOnlyList<(string Id, Box Box)> placedBoxes)
    {
        // A node's own ancestors are not obstacles: a child placed inside a container must be allowed to overlap the
        // container it sits in. Everything else placed (existing shapes and newly placed siblings) is.
        HashSet<string> ancestors = AncestorIds(node, nodesById);
        (double X, double Y) anchor = PlacementAnchor(node, document, nodesById, placedBoxes);
        double stepX = DefaultNodeWidth + PlacementGap;
        double stepY = DefaultNodeHeight + PlacementGap;
        for (int row = 0; row < MaxPlacementSteps; row++)
        {
            for (int column = 0; column < MaxPlacementSteps; column++)
            {
                var candidate = new Box(anchor.X + (column * stepX), anchor.Y + (row * stepY), DefaultNodeWidth, DefaultNodeHeight);
                if (!placedBoxes.Any(p => !ancestors.Contains(p.Id) && Overlaps(candidate, p.Box)))
                {
                    return new Geometry(candidate.X, candidate.Y, candidate.W, candidate.H);
                }
            }
        }

        // Unreachable for any real document: the plane is unbounded, so a free cell always exists. Place at the
        // anchor rather than fail the whole write if a pathological obstacle field somehow exhausts the search.
        return new Geometry(anchor.X, anchor.Y, DefaultNodeWidth, DefaultNodeHeight);
    }

    private static HashSet<string> AncestorIds(Node node, IReadOnlyDictionary<string, Node> nodesById)
    {
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        string? parent = node.Parent;
        while (parent is not null && ancestors.Add(parent) && nodesById.TryGetValue(parent, out Node? parentNode))
        {
            parent = parentNode.Parent;
        }

        return ancestors;
    }

    private static (double X, double Y) PlacementAnchor(Node node, Document document, IReadOnlyDictionary<string, Node> nodesById, IReadOnlyList<(string Id, Box Box)> placedBoxes)
    {
        // Inside a placed container: start at its padded top-left and let the overlap walk move aside.
        if (node.Parent is { } parent && nodesById.TryGetValue(parent, out Node? parentNode) && parentNode.Geometry is { } parentGeometry)
        {
            return (parentGeometry.X + ContainerFitPadding, parentGeometry.Y + ContainerFitPadding);
        }

        // Connected to a placed neighbour (including one placed earlier in this pass): beside their union box.
        List<Box> neighbourBoxes = ConnectedPlacedNeighbourBoxes(node, document, nodesById);
        if (neighbourBoxes.Count > 0)
        {
            Box neighbours = Union(neighbourBoxes);
            return (neighbours.Right + PlacementGap, neighbours.Y + ((neighbours.H - DefaultNodeHeight) / 2));
        }

        // Unconnected: beside the whole diagram's bounding box.
        if (placedBoxes.Count > 0)
        {
            Box all = Union([.. placedBoxes.Select(p => p.Box)]);
            return (all.Right + PlacementGap, all.Y);
        }

        return (0, 0);
    }

    private static List<Box> ConnectedPlacedNeighbourBoxes(Node node, Document document, IReadOnlyDictionary<string, Node> nodesById)
    {
        var boxes = new List<Box>();
        foreach (Edge edge in document.Edges)
        {
            string? otherId = edge.From.Node == node.Id ? edge.To.Node
                : edge.To.Node == node.Id ? edge.From.Node
                : null;
            if (otherId is not null && nodesById.TryGetValue(otherId, out Node? other) && other.Geometry is { } geometry)
            {
                boxes.Add(BoxOf(geometry));
            }
        }

        return boxes;
    }

    /// <summary>
    /// Grows every container that gained a child in this reply so it encloses all of its placed children with
    /// <see cref="ContainerFitPadding"/> (reusing <see cref="BoundingBox"/>), and grows the ancestors of a container
    /// that itself grew. Never shrinks a container and never touches one outside that subtree, so geometry the update
    /// did not affect stays byte-identical.
    /// </summary>
    private static Document GrowContainersThatGainedChildren(Document existing, Document document)
    {
        var existingParentById = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (Node node in existing.Nodes)
        {
            existingParentById.TryAdd(node.Id, node.Parent);
        }

        var gained = new HashSet<string>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes)
        {
            if (node.Parent is not { } parent)
            {
                continue;
            }

            if (!existingParentById.TryGetValue(node.Id, out string? previous) || previous != parent)
            {
                gained.Add(parent);
            }
        }

        if (gained.Count == 0)
        {
            return document;
        }

        HashSet<string> affected = AncestorsOf(gained, document);
        for (int pass = 0; pass < MaxContainerFitPasses; pass++)
        {
            (document, bool changed) = GrowAffectedContainersOnce(document, affected);
            if (!changed)
            {
                break;
            }
        }

        return document;
    }

    private static HashSet<string> AncestorsOf(IReadOnlySet<string> containerIds, Document document)
    {
        var parentById = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes)
        {
            parentById.TryAdd(node.Id, node.Parent);
        }

        var affected = new HashSet<string>(containerIds, StringComparer.Ordinal);
        foreach (string id in containerIds)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            string? parent = parentById.GetValueOrDefault(id);
            while (parent is not null && seen.Add(parent))
            {
                affected.Add(parent);
                parent = parentById.GetValueOrDefault(parent);
            }
        }

        return affected;
    }

    private static (Document Document, bool Changed) GrowAffectedContainersOnce(Document document, IReadOnlySet<string> affected)
    {
        var childrenByParent = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes)
        {
            if (node.Parent is not { } parent)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(parent, out List<Node>? children))
            {
                children = [];
                childrenByParent[parent] = children;
            }

            children.Add(node);
        }

        bool changed = false;
        var nodes = new List<Node>(document.Nodes.Count);
        foreach (Node node in document.Nodes)
        {
            if (affected.Contains(node.Id)
                && node.Geometry is { } current
                && childrenByParent.TryGetValue(node.Id, out List<Node>? children)
                && BoundingBox(children) is { } padded)
            {
                Geometry grown = Union(current, padded);
                if (grown != current)
                {
                    nodes.Add(node with { Geometry = grown });
                    changed = true;
                    continue;
                }
            }

            nodes.Add(node);
        }

        return (changed ? document with { Nodes = nodes } : document, changed);
    }

    private static Box BoxOf(Geometry geometry) => new(geometry.X, geometry.Y, geometry.W, geometry.H);

    private static Box Union(IReadOnlyList<Box> boxes)
    {
        double minX = boxes.Min(b => b.X);
        double minY = boxes.Min(b => b.Y);
        double maxX = boxes.Max(b => b.Right);
        double maxY = boxes.Max(b => b.Bottom);
        return new Box(minX, minY, maxX - minX, maxY - minY);
    }

    private static Geometry Union(Geometry a, Geometry b)
    {
        double minX = Math.Min(a.X, b.X);
        double minY = Math.Min(a.Y, b.Y);
        double maxX = Math.Max(a.X + a.W, b.X + b.W);
        double maxY = Math.Max(a.Y + a.H, b.Y + b.H);
        return a with { X = minX, Y = minY, W = maxX - minX, H = maxY - minY };
    }

    private static bool Overlaps(Box a, Box b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private readonly record struct Box(double X, double Y, double W, double H)
    {
        public double Right => X + W;

        public double Bottom => Y + H;
    }

    /// <summary>
    /// The <paramref name="generated"/> reply against <paramref name="diff"/>'s base, as <c>patch_document</c> ops:
    /// addressable, never a whole-document replace, so an id the reply left alone is never touched. Order matters
    /// only where PatchApplier's own op contract needs it (an added container must exist before a reparent into it,
    /// so adds run first; a removed edge should not dangle off a node about to be removed, so edge removals run
    /// before node removals).
    /// </summary>
    private static JsonArray BuildUpdateOps(DocumentDiffResult diff, Document existing, Document generated)
    {
        Dictionary<string, Node> nodesById = generated.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        Dictionary<string, Edge> edgesById = generated.Edges.ToDictionary(e => e.Id, StringComparer.Ordinal);

        var ops = new JsonArray();
        foreach (Node node in diff.AddedNodes)
        {
            ops.Add(new JsonObject { ["op"] = "addNode", ["node"] = JsonSerializer.SerializeToNode(node, DocumentJson.Options) });
        }

        // Edge order is time in a sequence diagram (elsewhere it is z-order, which a reply need not preserve), so there
        // an edge lands where the reply wrote it. The shared edges are put in the reply's order first (moveEdge, in the
        // reply's own order so each anchor is already final), then every new edge goes after its predecessor in the reply.
        // `order` mirrors the stored edge order as the ops run, because an anchor must exist when its op is applied.
        List<string>? order = generated.Type == DocumentType.Sequence ? [.. existing.Edges.Select(e => e.Id)] : null;
        if (order is not null)
        {
            AppendEdgeMoves(ops, diff, generated, order);
        }

        var addedEdgeIds = diff.AddedEdges.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < generated.Edges.Count; index++)
        {
            Edge edge = generated.Edges[index];
            if (!addedEdgeIds.Contains(edge.Id))
            {
                continue;
            }

            var op = new JsonObject { ["op"] = "addEdge", ["edge"] = JsonSerializer.SerializeToNode(edge, DocumentJson.Options) };
            if (order is not null)
            {
                if (index > 0)
                {
                    op["after"] = generated.Edges[index - 1].Id;
                    order.Insert(order.IndexOf(generated.Edges[index - 1].Id) + 1, edge.Id);
                }
                else
                {
                    if (order.Count > 0)
                    {
                        op["before"] = order[0];
                    }

                    order.Insert(0, edge.Id);
                }
            }

            ops.Add(op);
        }

        foreach (ElementChange change in diff.ChangedNodes)
        {
            AppendNodeChangeOps(ops, change, nodesById[change.Id]);
        }

        foreach (ElementChange change in diff.ChangedEdges)
        {
            AppendEdgeChangeOps(ops, change, edgesById[change.Id]);
        }

        foreach (Edge edge in diff.RemovedEdges)
        {
            ops.Add(new JsonObject { ["op"] = "removeEdge", ["id"] = edge.Id });
        }

        foreach (Node node in diff.RemovedNodes)
        {
            ops.Add(new JsonObject { ["op"] = "removeNode", ["id"] = node.Id });
        }

        return ops;
    }

    /// <summary>
    /// The <c>moveEdge</c> ops that put the edges both revisions hold into the reply's order: <c>diff.MovedEdges</c> is the
    /// fewest, and its <c>after</c> is the shared edge each now follows (null: first). Applied in the reply's order, so an
    /// anchor is always already in its final place; <paramref name="order"/> is the stored order and is kept up to date.
    /// </summary>
    private static void AppendEdgeMoves(JsonArray ops, DocumentDiffResult diff, Document generated, List<string> order)
    {
        var added = diff.AddedEdges.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var position = generated.Edges.Where(e => !added.Contains(e.Id)).Select((e, index) => (e.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        foreach (ElementMove move in diff.MovedEdges.OrderBy(m => position[m.Id]))
        {
            order.Remove(move.Id);
            var op = new JsonObject { ["op"] = "moveEdge", ["id"] = move.Id };
            if (move.After is not null)
            {
                op["after"] = move.After;
                order.Insert(order.IndexOf(move.After) + 1, move.Id);
            }
            else
            {
                // First among the shared edges: go before whichever edge is first now.
                op["before"] = order[0];
                order.Insert(0, move.Id);
            }

            ops.Add(op);
        }
    }

    private static void AppendNodeChangeOps(JsonArray ops, ElementChange change, Node node)
    {
        var full = (JsonObject)JsonSerializer.SerializeToNode(node, DocumentJson.Options)!;
        var set = new JsonObject();
        foreach (FieldChange field in change.Fields)
        {
            if (Array.IndexOf(NodeUpdatableFields, field.Field) >= 0)
            {
                set[field.Field] = full[field.Field]?.DeepClone();
            }
        }

        if (set.Count > 0)
        {
            ops.Add(new JsonObject { ["op"] = "updateNode", ["id"] = change.Id, ["set"] = set });
        }

        AppendStyleAndPropOps(ops, "node", change);
    }

    private static void AppendEdgeChangeOps(JsonArray ops, ElementChange change, Edge edge)
    {
        var full = (JsonObject)JsonSerializer.SerializeToNode(edge, DocumentJson.Options)!;
        var set = new JsonObject();
        foreach (FieldChange field in change.Fields)
        {
            if (Array.IndexOf(EdgeUpdatableFields, field.Field) >= 0)
            {
                set[field.Field] = full[field.Field]?.DeepClone();
            }
        }

        if (set.Count > 0)
        {
            ops.Add(new JsonObject { ["op"] = "updateEdge", ["id"] = change.Id, ["set"] = set });
        }

        AppendStyleAndPropOps(ops, "edge", change);
    }

    /// <summary>
    /// style and props cannot move through updateNode/updateEdge's <c>set</c> (PatchApplier refuses them there), so a
    /// changed style becomes a restyle naming every field the style object has ever had (a field dropped from the
    /// reply is set to null, clearing it, rather than left stale), and a changed props becomes one setProp/removeProp
    /// per key.
    /// </summary>
    private static void AppendStyleAndPropOps(JsonArray ops, string target, ElementChange change)
    {
        FieldChange? style = change.Fields.FirstOrDefault(f => f.Field == "style");
        if (style is not null)
        {
            JsonObject styleSet = BuildObjectFieldSet(style.From, style.To);
            if (styleSet.Count > 0)
            {
                ops.Add(new JsonObject { ["op"] = "restyle", ["target"] = target, ["id"] = change.Id, ["style"] = styleSet });
            }
        }

        FieldChange? props = change.Fields.FirstOrDefault(f => f.Field == "props");
        if (props is not null)
        {
            AppendPropOps(ops, target, change.Id, props.From, props.To);
        }
    }

    private static void AppendPropOps(JsonArray ops, string target, string id, JsonElement? from, JsonElement? to)
    {
        foreach (string key in PropertyNames(from).Union(PropertyNames(to), StringComparer.Ordinal))
        {
            ops.Add(TryGetProperty(to, key) is { } value
                ? new JsonObject { ["op"] = "setProp", ["target"] = target, ["id"] = id, ["key"] = key, ["value"] = JsonNode.Parse(value.GetRawText()) }
                : new JsonObject { ["op"] = "removeProp", ["target"] = target, ["id"] = id, ["key"] = key });
        }
    }

    private static JsonObject BuildObjectFieldSet(JsonElement? from, JsonElement? to)
    {
        var result = new JsonObject();
        foreach (string key in PropertyNames(from).Union(PropertyNames(to), StringComparer.Ordinal))
        {
            result[key] = TryGetProperty(to, key) is { } value ? JsonNode.Parse(value.GetRawText()) : null;
        }

        return result;
    }

    private static IEnumerable<string> PropertyNames(JsonElement? element)
    {
        return element is { ValueKind: JsonValueKind.Object } obj ? obj.EnumerateObject().Select(p => p.Name) : [];
    }

    private static JsonElement? TryGetProperty(JsonElement? element, string key)
    {
        return element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out JsonElement value) ? value : null;
    }

    private static IReadOnlyList<string> NodeIds(Document document)
    {
        return [.. document.Nodes.Select(n => n.Id)];
    }

    private static IReadOnlyList<string> EdgeIds(Document document)
    {
        return [.. document.Edges.Select(e => e.Id)];
    }

    /// <summary>flowchart | architecture | sequence: the AI panel's type chips (design/atlas/ai-panel.md) do not offer freeform, which has no kind vocabulary to write valid kinds against.</summary>
    private static DocumentType ParseGeneratableType(string type)
    {
        return type?.Trim().ToLowerInvariant() switch
        {
            "flowchart" => DocumentType.Flowchart,
            "architecture" => DocumentType.Architecture,
            "sequence" => DocumentType.Sequence,
            _ => throw new GenerateFromTextValidationException($"diagramType must be 'flowchart', 'architecture' or 'sequence'; got '{type}'."),
        };
    }
}

public sealed record GenerateFromTextRequest(string? DocumentId, string Description, string DiagramType);

public sealed record GenerateFromTextResult(
    Document Document,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> AddedNodeIds,
    IReadOnlyList<string> AddedEdgeIds,
    int ChangedElementCount,
    int RemovedElementCount,
    string Provider,
    string Model);

/// <summary>The request was malformed: empty/oversized description, or an unsupported diagram type.</summary>
public sealed class GenerateFromTextValidationException(string message) : Exception(message);

/// <summary>request.DocumentId named a document that does not exist.</summary>
public sealed class GenerateFromTextDocumentNotFoundException(string documentId) : Exception($"No document with id '{documentId}'.")
{
    public string DocumentId { get; } = documentId;
}

/// <summary>The model's output did not parse as DSL, even after one retry with the parser's own error fed back to it.</summary>
public sealed class GenerateFromTextParseException(DslParseException firstAttempt, DslParseException retryAttempt)
    : Exception($"The model's output could not be parsed as DSL, even after a retry. First attempt: line {firstAttempt.Line}, column {firstAttempt.Column}: {firstAttempt.Detail}. Retry: line {retryAttempt.Line}, column {retryAttempt.Column}: {retryAttempt.Detail}.")
{
    public DslParseException FirstAttempt { get; } = firstAttempt;

    public DslParseException RetryAttempt { get; } = retryAttempt;
}

/// <summary>The sidecar could not lay the generated document out.</summary>
public sealed class GenerateFromTextLayoutException(SidecarException inner) : Exception($"The generated diagram could not be laid out: {inner.Message}", inner);

/// <summary>create_document/patch_document refused the generated content (e.g. an id collision in patch mode), or the model's reply could not be diffed against the document it was built from.</summary>
public sealed class GenerateFromTextWriteException(string message, Exception inner) : Exception(message, inner);
