using System.Text.Json.Nodes;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Mcp.Patching;

/// <summary>
/// Applies <c>patch_document</c> ops to a document's JSON working copy. Ops are addressable (they name the node or
/// edge they touch, never the whole document) and every failure mode - an unknown op, a missing target, a duplicate
/// id being added, a field the op does not own - throws <see cref="PatchException"/> rather than silently doing
/// nothing, so a caller can never mistake "nothing happened" for "the target does not exist". The schema is
/// validated by the caller once every op in the batch has been applied; this class only enforces shape it must to
/// keep the JSON well-formed (arrays stay arrays, ids stay strings).
/// </summary>
public static class PatchApplier
{
    private static readonly JsonObject Definitions = JsonNode.Parse(DocumentSchema.RawJson)!["$defs"]!.AsObject();
    private static readonly string[] NodeUpdatableFields = ["label", "kind", "shape", "geometry", "parent", "ports"];
    private static readonly string[] EdgeUpdatableFields = ["label", "kind", "from", "to", "routing"];

    /// <summary>Applies every op in order, mutating <paramref name="document"/> in place. Returns warnings, never failures.</summary>
    public static List<string> Apply(JsonObject document, JsonArray ops)
    {
        JsonArray nodes = ArrayOf(document, "nodes");
        JsonArray edges = ArrayOf(document, "edges");
        var warnings = new List<string>();

        foreach (JsonNode? opNode in ops)
        {
            if (opNode is not JsonObject op)
            {
                throw new PatchException("Each patch op must be a JSON object.");
            }

            string kind = StringField(op, "op");
            switch (kind)
            {
                case "addNode":
                    AddElement(nodes, ObjectField(op, "node"), "node");
                    break;
                case "removeNode":
                    RemoveElement(nodes, StringField(op, "id"), "node");
                    WarnIfEdgesReference(edges, StringField(op, "id"), warnings);
                    break;
                case "updateNode":
                    UpdateElement(nodes, StringField(op, "id"), ObjectField(op, "set"), "node", NodeUpdatableFields);
                    break;
                case "addEdge":
                    {
                        JsonObject edge = ObjectField(op, "edge");
                        int position = PositionAmong(edges, op, "edge", selfId: null);
                        AddElement(edges, edge, "edge", position);
                        WarnIfEndpointMissing(nodes, edge, warnings);
                        break;
                    }

                case "moveEdge":
                    MoveEdge(edges, StringField(op, "id"), op);
                    break;

                case "removeEdge":
                    RemoveElement(edges, StringField(op, "id"), "edge");
                    break;
                case "updateEdge":
                    UpdateElement(edges, StringField(op, "id"), ObjectField(op, "set"), "edge", EdgeUpdatableFields);
                    break;
                case "setProp":
                    SetProp(TargetArray(op, nodes, edges), StringField(op, "id"), StringField(op, "key"), RequiredValue(op), TargetName(op));
                    break;
                case "removeProp":
                    RemoveProp(TargetArray(op, nodes, edges), StringField(op, "id"), StringField(op, "key"), TargetName(op));
                    break;
                case "restyle":
                    Restyle(TargetArray(op, nodes, edges), StringField(op, "id"), ObjectField(op, "style"), TargetName(op));
                    break;
                case "setFlythrough":
                    SetFlythrough(document, op);
                    break;
                case "":
                    throw new PatchException("Each patch op needs an 'op' field.");
                default:
                    throw new PatchException($"Unknown patch op '{kind}'. Expected one of: addNode, removeNode, updateNode, addEdge, removeEdge, updateEdge, moveEdge, setProp, removeProp, restyle, setFlythrough.");
            }
        }

        return warnings;
    }

    /// <summary>
    /// <c>setFlythrough</c> replaces the document's camera path (docs/flythrough.md) with the object in <c>flythrough</c>, or
    /// removes it when that is <c>null</c>. The path is one member of the document, not a list of elements, so it is
    /// replaced whole; the schema is what refuses a path it does not admit, once the batch is applied.
    /// </summary>
    private static void SetFlythrough(JsonObject document, JsonObject op)
    {
        if (!op.ContainsKey("flythrough"))
        {
            throw new PatchException("'setFlythrough' needs a 'flythrough' field: the camera path object, or null to remove it.");
        }

        switch (op["flythrough"])
        {
            case null:
                document.Remove("flythrough");
                break;
            case JsonObject flythrough:
                document["flythrough"] = flythrough.DeepClone();
                break;
            default:
                throw new PatchException("'flythrough' must be a JSON object (the camera path) or null.");
        }
    }

    private static void AddElement(JsonArray array, JsonObject element, string label, int position = -1)
    {
        string id = StringField(element, "id");
        if (id.Length == 0)
        {
            throw new PatchException($"The {label} being added needs an 'id'.");
        }

        if (IndexOf(array, id) >= 0)
        {
            throw new PatchException($"Cannot add {label} '{id}': a {label} with that id already exists.");
        }

        if (position < 0)
        {
            array.Add(element.DeepClone());
        }
        else
        {
            array.Insert(position, element.DeepClone());
        }
    }

    /// <summary>
    /// Where an op's optional <c>before</c> or <c>after</c> puts an edge: the array index to insert at, or -1 for the
    /// end when it names neither. Edge order is a document's meaning wherever order is (a sequence diagram's time), so
    /// an unknown reference, both fields at once, or a reference to the edge itself (<paramref name="selfId"/>) fails
    /// the op instead of falling back to the end. Called with the array as it will be when the edge is inserted.
    /// </summary>
    private static int PositionAmong(JsonArray array, JsonObject op, string label, string? selfId)
    {
        bool hasBefore = op["before"] is not null;
        bool hasAfter = op["after"] is not null;
        if (hasBefore && hasAfter)
        {
            throw new PatchException($"'{StringField(op, "op")}' takes 'before' or 'after', not both.");
        }

        if (!hasBefore && !hasAfter)
        {
            return -1;
        }

        string field = hasBefore ? "before" : "after";
        string reference = op[field] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;
        if (reference.Length == 0)
        {
            throw new PatchException($"'{field}' must be the id of an existing {label}.");
        }

        if (selfId is not null && reference == selfId)
        {
            throw new PatchException($"Cannot place {label} '{selfId}' {field} itself.");
        }

        int index = IndexOf(array, reference);
        if (index < 0)
        {
            throw new PatchException($"Cannot place the {label} {field} '{reference}': no {label} with that id exists.");
        }

        return hasBefore ? index : index + 1;
    }

    /// <summary>
    /// Moves an existing edge to just before or after another. Nothing but its position changes, and the position is the
    /// point of the op: in a sequence diagram the edge order is the time order. Exactly one of <c>before</c> and
    /// <c>after</c> is required, since a move that names no destination is a mistake, not a no-op.
    /// </summary>
    private static void MoveEdge(JsonArray edges, string id, JsonObject op)
    {
        int from = IndexOf(edges, id);
        if (from < 0)
        {
            throw new PatchException($"Cannot move edge '{id}': no edge with that id exists.");
        }

        if (op["before"] is null && op["after"] is null)
        {
            throw new PatchException("moveEdge needs 'before' or 'after': the id of the edge to move this one next to.");
        }

        JsonNode? edge = edges[from];
        edges.RemoveAt(from);
        try
        {
            edges.Insert(PositionAmong(edges, op, "edge", id), edge);
        }
        catch (PatchException)
        {
            // The caller fails the whole patch on this exception, but put the edge back so a caught op leaves the array whole.
            edges.Insert(from, edge);
            throw;
        }
    }

    private static void RemoveElement(JsonArray array, string id, string label)
    {
        int index = IndexOf(array, id);
        if (index < 0)
        {
            throw new PatchException($"Cannot remove {label} '{id}': no {label} with that id exists.");
        }

        array.RemoveAt(index);
    }

    private static void UpdateElement(JsonArray array, string id, JsonObject set, string label, IReadOnlyCollection<string> updatableFields)
    {
        int index = IndexOf(array, id);
        if (index < 0)
        {
            throw new PatchException($"Cannot update {label} '{id}': no {label} with that id exists.");
        }

        var element = (JsonObject)array[index]!;
        foreach ((string field, JsonNode? value) in set)
        {
            if (field is "id" or "style" or "props")
            {
                throw new PatchException($"updateNode/updateEdge cannot set '{field}' directly; use {(field == "style" ? "restyle" : field == "props" ? "setProp/removeProp" : "a different op")} instead.");
            }

            if (!updatableFields.Contains(field))
            {
                throw new PatchException($"'{field}' is not a field {label}.update can set. Expected one of: {string.Join(", ", updatableFields)}.");
            }

            SetField(element, field, value, label);
        }
    }

    private static void SetProp(JsonArray array, string id, string key, JsonNode? value, string label)
    {
        JsonObject element = Find(array, id, label);
        if (key.Length == 0)
        {
            throw new PatchException("setProp needs a 'key'.");
        }

        if (element["props"] is not JsonObject props)
        {
            props = [];
            element["props"] = props;
        }

        props[key] = value;
    }

    private static void RemoveProp(JsonArray array, string id, string key, string label)
    {
        JsonObject element = Find(array, id, label);
        if (element["props"] is not JsonObject props || !props.ContainsKey(key))
        {
            throw new PatchException($"Cannot remove prop '{key}' from {label} '{id}': it is not set.");
        }

        props.Remove(key);
        if (props.Count == 0)
        {
            element.Remove("props");
        }
    }

    private static void Restyle(JsonArray array, string id, JsonObject style, string label)
    {
        JsonObject element = Find(array, id, label);
        if (element["style"] is not JsonObject existing)
        {
            existing = [];
            element["style"] = existing;
        }

        foreach ((string field, JsonNode? value) in style)
        {
            SetField(existing, field, value, label + "Style");
        }

        if (existing.Count == 0)
        {
            element.Remove("style");
        }
    }

    private static void SetField(JsonObject target, string field, JsonNode? value, string definition)
    {
        JsonNode schema = Definitions[definition]!;
        bool optional = schema["properties"]!.AsObject().ContainsKey(field)
            && !(schema["required"]?.AsArray().Any(item => item!.GetValue<string>() == field) ?? false);
        if (value is null && optional)
        {
            target.Remove(field);
        }
        else
        {
            target[field] = value?.DeepClone();
        }
    }

    private static void WarnIfEdgesReference(JsonArray edges, string nodeId, List<string> warnings)
    {
        foreach (JsonNode? edgeNode in edges)
        {
            if (edgeNode is not JsonObject edge)
            {
                continue;
            }

            string edgeId = StringField(edge, "id");
            if (EndpointNode(edge, "from") == nodeId || EndpointNode(edge, "to") == nodeId)
            {
                warnings.Add($"edge '{edgeId}' still references removed node '{nodeId}'");
            }
        }
    }

    private static void WarnIfEndpointMissing(JsonArray nodes, JsonObject edge, List<string> warnings)
    {
        string edgeId = StringField(edge, "id");
        foreach (string end in new[] { "from", "to" })
        {
            string nodeId = EndpointNode(edge, end);
            if (nodeId.Length > 0 && IndexOf(nodes, nodeId) < 0)
            {
                warnings.Add($"edge '{edgeId}' references a missing node ('{nodeId}')");
            }
        }
    }

    private static string EndpointNode(JsonObject edge, string end)
    {
        return edge[end] is JsonObject endpoint ? StringField(endpoint, "node") : string.Empty;
    }

    private static JsonObject Find(JsonArray array, string id, string label)
    {
        int index = IndexOf(array, id);
        if (index < 0)
        {
            throw new PatchException($"No {label} with id '{id}' exists.");
        }

        return (JsonObject)array[index]!;
    }

    private static JsonNode RequiredValue(JsonObject op)
    {
        if (!op.TryGetPropertyValue("value", out JsonNode? value) || value is null)
        {
            throw new PatchException("setProp needs a 'value'.");
        }

        return value.DeepClone();
    }

    private static int IndexOf(JsonArray array, string id)
    {
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject element && StringField(element, "id") == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static JsonArray TargetArray(JsonObject op, JsonArray nodes, JsonArray edges)
    {
        return TargetName(op) switch
        {
            "node" => nodes,
            "edge" => edges,
            _ => throw new PatchException("'target' must be 'node' or 'edge'."),
        };
    }

    private static string TargetName(JsonObject op)
    {
        string target = StringField(op, "target");
        return target is "node" or "edge" ? target : string.Empty;
    }

    private static JsonArray ArrayOf(JsonObject document, string name)
    {
        return document[name] as JsonArray ?? throw new PatchException($"The document has no '{name}' array to patch.");
    }

    private static string StringField(JsonObject obj, string name)
    {
        return obj[name] is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;
    }

    private static JsonObject ObjectField(JsonObject obj, string name)
    {
        return obj[name] as JsonObject ?? throw new PatchException($"'{name}' must be a JSON object on this op.");
    }
}
