using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Diffing;

/// <summary>A single field that differs between two revisions of the same element, by id.</summary>
public sealed record FieldChange(string Field, JsonElement? From, JsonElement? To);

/// <summary>
/// One element (node or edge) whose top-level fields changed between two revisions of the same id. <see cref="Fields"/>
/// carries each changed field whole (a changed <c>props</c> is one entry holding both objects, which is what an applier of
/// the diff such as the AI update path wants); <see cref="Props"/> is the same change taken apart, one entry per prop key,
/// which is what a reader wants.
/// </summary>
public sealed record ElementChange(string Id, IReadOnlyList<FieldChange> Fields)
{
    /// <summary>Per-key changes to <c>props</c>: the key as <see cref="FieldChange.Field"/>, with From null for a key added and To null for a key removed. Empty when props did not change.</summary>
    public IReadOnlyList<FieldChange> Props { get; init; } = [];
}

/// <summary>
/// An element that kept its id and fields but changed place among the elements both revisions hold: in the newer
/// revision it follows <see cref="After"/> (null when it is first). Order is the meaning in a sequence diagram - the
/// order of the edges is time - so a reorder is a change, not a no-op; <c>patch_document</c>'s <c>moveEdge</c> takes the same words.
/// </summary>
public sealed record ElementMove(string Id, string? After);

/// <summary>
/// Added, removed and changed nodes and edges between two revisions of a document, matched by id, and the elements
/// that only changed place (<see cref="MovedNodes"/>, <see cref="MovedEdges"/>). This is the <c>diff_documents</c>
/// shape: something an agent can reason about field by field, not a text diff of JSON.
/// </summary>
public sealed record DocumentDiffResult(
    IReadOnlyList<Node> AddedNodes,
    IReadOnlyList<Node> RemovedNodes,
    IReadOnlyList<ElementChange> ChangedNodes,
    IReadOnlyList<Edge> AddedEdges,
    IReadOnlyList<Edge> RemovedEdges,
    IReadOnlyList<ElementChange> ChangedEdges)
{
    /// <summary>Nodes that changed place among the nodes both revisions hold: the fewest moves that turn one order into the other.</summary>
    public IReadOnlyList<ElementMove> MovedNodes { get; init; } = [];

    /// <summary>Edges that changed place among the edges both revisions hold: the fewest moves that turn one order into the other.</summary>
    public IReadOnlyList<ElementMove> MovedEdges { get; init; } = [];

    /// <summary>
    /// The camera path before and after, when it differs (docs/flythrough.md); null when the two revisions have the same one,
    /// or none. The path is a presentation of the graph, so it is reported beside the graph's changes and never turned into ops.
    /// </summary>
    public FlythroughChange? FlythroughChange { get; init; }

    /// <summary>
    /// Changes to the document's own header that are part of what the diagram says: <c>id</c>, <c>schemaVersion</c>,
    /// <c>title</c>, <c>type</c> and each diagram-theme override as <c>theme.&lt;token&gt;</c> (From null for a token added, To null
    /// for one removed). Sorted by field name; empty when the headers agree.
    /// </summary>
    public IReadOnlyList<FieldChange> DocumentChanges { get; init; } = [];

    /// <summary>
    /// Changes to the header's bookkeeping, <c>meta.createdAt</c>, <c>meta.updatedAt</c>, <c>meta.createdBy</c> and
    /// <c>meta.aiProvenance</c>, which every save rewrites. They are reported apart from every other change on purpose: a
    /// consumer that asks "did the diagram change?" must not count them, or a save that changed nothing would read as an edit.
    /// </summary>
    public IReadOnlyList<FieldChange> VolatileChanges { get; init; } = [];
}

/// <summary>The camera path of the older revision and of the newer one; either is null when that revision has none.</summary>
public sealed record FlythroughChange(Flythrough? From, Flythrough? To);

public static class DocumentDiff
{
    private static readonly string[] NodeFields = ["kind", "label", "shape", "geometry", "style", "props", "parent", "ports"];
    private static readonly string[] EdgeFields = ["kind", "label", "from", "to", "routing", "style", "props"];

    public static DocumentDiffResult Compare(Document from, Document to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        (List<Node> addedNodes, List<Node> removedNodes, List<ElementChange> changedNodes) =
            CompareElements(from.Nodes, to.Nodes, n => n.Id, NodeFields);
        (List<Edge> addedEdges, List<Edge> removedEdges, List<ElementChange> changedEdges) =
            CompareElements(from.Edges, to.Edges, e => e.Id, EdgeFields);

        return new DocumentDiffResult(addedNodes, removedNodes, changedNodes, addedEdges, removedEdges, changedEdges)
        {
            MovedNodes = Moves(from.Nodes, to.Nodes, n => n.Id),
            MovedEdges = Moves(from.Edges, to.Edges, e => e.Id),
            FlythroughChange = SameFlythrough(from.Flythrough, to.Flythrough) ? null : new FlythroughChange(from.Flythrough, to.Flythrough),
            DocumentChanges = DocumentFieldChanges(from, to),
            VolatileChanges = MetaChanges(from.Meta, to.Meta),
        };
    }

    private static List<FieldChange> DocumentFieldChanges(Document from, Document to)
    {
        var changes = new List<FieldChange>();
        AddIfDifferent(changes, "id", JsonValue.Create(from.Id), JsonValue.Create(to.Id));
        AddIfDifferent(changes, "schemaVersion", JsonValue.Create(from.SchemaVersion), JsonValue.Create(to.SchemaVersion));
        AddIfDifferent(changes, "title", JsonValue.Create(from.Title), JsonValue.Create(to.Title));
        AddIfDifferent(changes, "type", JsonSerializer.SerializeToNode(from.Type, DocumentJson.Options), JsonSerializer.SerializeToNode(to.Type, DocumentJson.Options));

        // A theme override is a token name to a value; an empty map and no map are the same (the DSL writes neither).
        IReadOnlyDictionary<string, string> fromTheme = from.Theme ?? new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> toTheme = to.Theme ?? new Dictionary<string, string>();
        foreach (string token in fromTheme.Keys.Union(toTheme.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            AddIfDifferent(
                changes,
                $"theme.{token}",
                fromTheme.TryGetValue(token, out string? before) ? JsonValue.Create(before) : null,
                toTheme.TryGetValue(token, out string? after) ? JsonValue.Create(after) : null);
        }

        return changes;
    }

    private static List<FieldChange> MetaChanges(Meta from, Meta to)
    {
        var changes = new List<FieldChange>();
        AddIfDifferent(changes, "meta.createdAt", JsonSerializer.SerializeToNode(from.CreatedAt, DocumentJson.Options), JsonSerializer.SerializeToNode(to.CreatedAt, DocumentJson.Options));
        AddIfDifferent(changes, "meta.updatedAt", JsonSerializer.SerializeToNode(from.UpdatedAt, DocumentJson.Options), JsonSerializer.SerializeToNode(to.UpdatedAt, DocumentJson.Options));
        AddIfDifferent(changes, "meta.createdBy", JsonValue.Create(from.CreatedBy), JsonValue.Create(to.CreatedBy));
        // No trail and an empty one are the same thing; the DSL writes no provenance line for either.
        AddIfDifferent(changes, "meta.aiProvenance", ProvenanceNode(from), ProvenanceNode(to));
        return changes;
    }

    private static JsonNode? ProvenanceNode(Meta meta)
    {
        return meta.AiProvenance is { Count: > 0 } trail ? JsonSerializer.SerializeToNode(trail, DocumentJson.Options) : null;
    }

    private static void AddIfDifferent(List<FieldChange> changes, string field, JsonNode? from, JsonNode? to)
    {
        if (!JsonNode.DeepEquals(from, to))
        {
            changes.Add(new FieldChange(field, ToElement(from), ToElement(to)));
        }
    }

    /// <summary>The canonical JSON of each path, so two paths are the same when a writer would serialise them alike.</summary>
    private static bool SameFlythrough(Flythrough? from, Flythrough? to)
    {
        return string.Equals(
            from is null ? null : JsonSerializer.Serialize(from, DocumentJson.Options),
            to is null ? null : JsonSerializer.Serialize(to, DocumentJson.Options),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The elements to move to turn the order of the ids both revisions share into the newer one, the fewest:
    /// those outside a longest run that already keeps its relative order (so moving one message from the third row to the
    /// first reports that message, not the two it jumped over). Ties break towards keeping the earlier element in place,
    /// so the answer is the same every time. Each move names the element it now follows in the newer revision.
    /// </summary>
    private static List<ElementMove> Moves<T>(IReadOnlyList<T> from, IReadOnlyList<T> to, Func<T, string> idOf)
    {
        HashSet<string> fromIds = from.Select(idOf).ToHashSet(StringComparer.Ordinal);
        HashSet<string> toIds = to.Select(idOf).ToHashSet(StringComparer.Ordinal);
        string[] older = from.Select(idOf).Where(toIds.Contains).ToArray();
        string[] newer = to.Select(idOf).Where(fromIds.Contains).ToArray();

        Dictionary<string, int> newerIndex = newer.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);
        int[] sequence = older.Select(id => newerIndex[id]).ToArray();

        // Longest strictly increasing subsequence, O(n^2): the elements that stay put.
        int[] length = new int[sequence.Length];
        int[] previous = new int[sequence.Length];
        int best = -1;
        for (int i = 0; i < sequence.Length; i++)
        {
            length[i] = 1;
            previous[i] = -1;
            for (int j = 0; j < i; j++)
            {
                if (sequence[j] < sequence[i] && length[j] + 1 > length[i])
                {
                    length[i] = length[j] + 1;
                    previous[i] = j;
                }
            }

            if (best < 0 || length[i] > length[best])
            {
                best = i;
            }
        }

        var stays = new HashSet<string>(StringComparer.Ordinal);
        for (int i = best; i >= 0; i = previous[i])
        {
            stays.Add(older[i]);
        }

        var moves = new List<ElementMove>();
        for (int i = 0; i < newer.Length; i++)
        {
            if (!stays.Contains(newer[i]))
            {
                // The element before it in the newer revision, counting only elements both revisions hold, since an
                // added element is not something a reader of the older one can be pointed at.
                moves.Add(new ElementMove(newer[i], i == 0 ? null : newer[i - 1]));
            }
        }

        return moves;
    }

    private static (List<T> Added, List<T> Removed, List<ElementChange> Changed) CompareElements<T>(
        IReadOnlyList<T> from, IReadOnlyList<T> to, Func<T, string> idOf, IReadOnlyList<string> fields)
    {
        Dictionary<string, T> fromById = ById(from, idOf);
        Dictionary<string, T> toById = ById(to, idOf);

        var added = to.Where(e => !fromById.ContainsKey(idOf(e))).ToList();
        var removed = from.Where(e => !toById.ContainsKey(idOf(e))).ToList();

        var changed = new List<ElementChange>();
        foreach ((string id, T fromElement) in fromById)
        {
            if (!toById.TryGetValue(id, out T? toElement))
            {
                continue;
            }

            JsonObject fromJson = ToJsonObject(fromElement);
            JsonObject toJson = ToJsonObject(toElement);
            var fieldChanges = new List<FieldChange>();
            foreach (string field in fields)
            {
                JsonNode? fromValue = fromJson[field];
                JsonNode? toValue = toJson[field];
                if (!JsonNode.DeepEquals(fromValue, toValue))
                {
                    fieldChanges.Add(new FieldChange(field, ToElement(fromValue), ToElement(toValue)));
                }
            }

            if (fieldChanges.Count > 0)
            {
                changed.Add(new ElementChange(id, fieldChanges) { Props = PropChanges(fromJson["props"], toJson["props"]) });
            }
        }

        return (added, removed, changed.OrderBy(c => c.Id, StringComparer.Ordinal).ToList());
    }

    /// <summary>Each prop key whose value differs between two <c>props</c> objects (either may be absent), sorted by key.</summary>
    private static List<FieldChange> PropChanges(JsonNode? from, JsonNode? to)
    {
        JsonObject fromProps = from as JsonObject ?? [];
        JsonObject toProps = to as JsonObject ?? [];
        var changes = new List<FieldChange>();
        foreach (string key in fromProps.Select(p => p.Key).Union(toProps.Select(p => p.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            AddIfDifferent(changes, key, fromProps[key], toProps[key]);
        }

        return changes;
    }

    // The document schema and the REST write path do not yet enforce id uniqueness, so a document stored through
    // POST /api/documents can reach here with two nodes or edges sharing an id. Diffing matches by id, so say that
    // plainly instead of failing with an opaque dictionary error.
    private static Dictionary<string, T> ById<T>(IReadOnlyList<T> elements, Func<T, string> idOf)
    {
        var byId = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (T element in elements)
        {
            if (!byId.TryAdd(idOf(element), element))
            {
                throw new InvalidOperationException($"Cannot diff: a revision contains more than one element with id '{idOf(element)}'.");
            }
        }

        return byId;
    }

    private static JsonObject ToJsonObject<T>(T element)
    {
        return JsonSerializer.SerializeToNode(element, DocumentJson.Options)!.AsObject();
    }

    private static JsonElement? ToElement(JsonNode? node)
    {
        return node?.Deserialize<JsonElement>();
    }
}
