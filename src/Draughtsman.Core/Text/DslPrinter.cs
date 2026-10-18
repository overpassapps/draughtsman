using System.Text;
using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;

namespace Draughtsman.Core.Text;

/// <summary>
/// Writes the canonical DSL form of a document: header lines, a blank line, nodes and containers in document order,
/// a blank line, edges in document order. One statement per line, single spaces, LF line endings, so the output is
/// deterministic and git diffs stay small. Empty style, props, ports, waypoints and theme are written as absent, and
/// so is a flythrough with no keyframes; a flythrough's keyframes follow the edges in tour order.
/// </summary>
internal static class DslPrinter
{
    public static string Print(Document document)
    {
        var lines = new List<string>();
        PrintHeader(document, lines);

        var containerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes)
        {
            RequireId(node.Id, $"node '{node.Id}'");
            if (string.Equals(node.Kind, DslSyntax.ContainerKind, StringComparison.Ordinal))
            {
                containerIds.Add(node.Id);
            }
        }

        var childrenByContainer = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes)
        {
            if (node.Parent is { } parent && containerIds.Contains(parent))
            {
                if (!childrenByContainer.TryGetValue(parent, out List<string>? children))
                {
                    children = [];
                    childrenByContainer[parent] = children;
                }

                children.Add(node.Id);
            }
        }

        if (document.Nodes.Count > 0)
        {
            lines.Add(string.Empty);
            foreach (Node node in document.Nodes)
            {
                lines.Add(PrintNode(document, node, containerIds, childrenByContainer));
            }
        }

        if (document.Edges.Count > 0)
        {
            lines.Add(string.Empty);
            foreach (Edge edge in document.Edges)
            {
                lines.Add(PrintEdge(edge));
            }
        }

        if (document.Flythrough is { Keyframes.Count: > 0 } flythrough)
        {
            lines.Add(string.Empty);
            if (flythrough.Loop is { } loop)
            {
                lines.Add($"{DslSyntax.FlythroughStatement} loop={(loop ? "true" : "false")}");
            }

            foreach (Keyframe keyframe in flythrough.Keyframes)
            {
                lines.Add(PrintKeyframe(keyframe));
            }
        }

        var text = new StringBuilder();
        foreach (string line in lines)
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    private static void PrintHeader(Document document, List<string> lines)
    {
        RequireId(document.Id, "the document id");
        lines.Add($"{DslSyntax.Magic} {document.SchemaVersion}");
        lines.Add($"id {document.Id}");
        lines.Add($"type {DslSyntax.EnumName(document.Type)}");
        lines.Add($"title {DslSyntax.Quote(document.Title)}");

        if (document.Theme is { Count: > 0 })
        {
            var parts = new List<string> { "theme" };
            foreach ((string key, string value) in document.Theme)
            {
                parts.Add($"{Key(key)}={DslSyntax.Quote(value)}");
            }

            lines.Add(string.Join(' ', parts));
        }

        Meta meta = document.Meta;
        lines.Add($"meta created={DslSyntax.FormatTimestamp(meta.CreatedAt)} updated={DslSyntax.FormatTimestamp(meta.UpdatedAt)} by={Token(meta.CreatedBy)}");

        if (meta.AiProvenance is { Count: > 0 })
        {
            foreach (AiProvenance entry in meta.AiProvenance)
            {
                var parts = new List<string>
                {
                    "provenance",
                    Token(entry.Agent),
                    DslSyntax.EnumName(entry.Action),
                    DslSyntax.FormatTimestamp(entry.At),
                };
                if (entry.Model is { } model)
                {
                    parts.Add($"model={Value(model)}");
                }

                parts.Add("{");
                foreach (string id in entry.ElementIds)
                {
                    RequireId(id, "a provenance element id");
                    parts.Add(id);
                }

                parts.Add("}");
                lines.Add(string.Join(' ', parts));
            }
        }
    }

    private static string PrintNode(Document document, Node node, HashSet<string> containerIds, Dictionary<string, List<string>> childrenByContainer)
    {
        bool isContainer = containerIds.Contains(node.Id);
        var parts = new List<string> { isContainer ? DslSyntax.ContainerKind : "node", node.Id };
        if (!isContainer)
        {
            parts.Add(Token(node.Kind));
        }

        parts.Add(DslSyntax.Quote(node.Label));

        if (node.Geometry is { } geometry)
        {
            parts.Add("@");
            parts.Add($"{DslSyntax.FormatNumber(geometry.X)},{DslSyntax.FormatNumber(geometry.Y)}");
            parts.Add($"{DslSyntax.FormatNumber(geometry.W)}x{DslSyntax.FormatNumber(geometry.H)}");
            if (geometry.Rotation is { } rotation)
            {
                parts.Add($"rotation={DslSyntax.FormatNumber(rotation)}");
            }
        }

        if (!string.Equals(node.Shape, DslSyntax.DefaultShapeFor(document.Type, node.Kind), StringComparison.Ordinal))
        {
            parts.Add($"shape={Value(node.Shape)}");
        }

        // Membership of a container is written in the container's braces; parent= is only for anything else.
        if (node.Parent is { } parent && !containerIds.Contains(parent))
        {
            RequireId(parent, $"the parent of node '{node.Id}'");
            parts.Add($"parent={parent}");
        }

        if (node.Ports is { Count: > 0 })
        {
            var ports = new List<string>(node.Ports.Count);
            foreach (Port port in node.Ports)
            {
                RequireId(port.Id, $"a port of node '{node.Id}'");
                ports.Add($"{port.Id}@{DslSyntax.FormatNumber(port.X)},{DslSyntax.FormatNumber(port.Y)}");
            }

            parts.Add($"ports=[{string.Join(' ', ports)}]");
        }

        if (node.Style is { } style)
        {
            AddStyle(parts, "fill", style.Fill);
            AddStyle(parts, "stroke", style.Stroke);
            AddStyle(parts, "strokeWidth", style.StrokeWidth);
            AddStyle(parts, "textColor", style.TextColor);
            AddStyle(parts, "font", style.Font);
            AddStyle(parts, "dash", style.Dash);
            AddStyle(parts, "fontSize", style.FontSize);
            AddStyle(parts, "bold", style.Bold);
        }

        AddProps(parts, node.Props);

        if (childrenByContainer.TryGetValue(node.Id, out List<string>? children))
        {
            parts.Add("{");
            parts.AddRange(children);
            parts.Add("}");
        }

        return string.Join(' ', parts);
    }

    private static string PrintKeyframe(Keyframe keyframe)
    {
        RequireId(keyframe.Id, $"keyframe '{keyframe.Id}'");
        ViewRect view = keyframe.View;
        var parts = new List<string>
        {
            DslSyntax.KeyframeStatement,
            keyframe.Id,
            "@",
            $"{DslSyntax.FormatNumber(view.X)},{DslSyntax.FormatNumber(view.Y)}",
            $"{DslSyntax.FormatNumber(view.W)}x{DslSyntax.FormatNumber(view.H)}",
        };

        if (keyframe.Caption is { } caption)
        {
            parts.Add($"caption={DslSyntax.Quote(caption)}");
        }

        if (keyframe.DurationMs is { } duration)
        {
            parts.Add($"duration={DslSyntax.FormatNumber(duration)}");
        }

        if (keyframe.HoldMs is { } hold)
        {
            parts.Add($"hold={DslSyntax.FormatNumber(hold)}");
        }

        if (keyframe.Easing is { } easing)
        {
            parts.Add($"easing={DslSyntax.EnumName(easing)}");
        }

        return string.Join(' ', parts);
    }

    private static string PrintEdge(Edge edge)
    {
        RequireId(edge.Id, $"edge '{edge.Id}'");
        var parts = new List<string>
        {
            "edge",
            edge.Id,
            Token(edge.Kind),
            EndpointToken(edge, edge.From),
            "->",
            EndpointToken(edge, edge.To),
        };

        if (edge.Label is { } label)
        {
            parts.Add(DslSyntax.Quote(label));
        }

        if (edge.Routing is { } routing)
        {
            parts.Add($"routing={DslSyntax.EnumName(routing.Mode)}");
            if (routing.Waypoints is { Count: > 0 })
            {
                parts.Add($"waypoints=[{string.Join(' ', routing.Waypoints.Select(p => $"{DslSyntax.FormatNumber(p.X)},{DslSyntax.FormatNumber(p.Y)}"))}]");
            }
        }

        if (edge.Style is { } style)
        {
            AddStyle(parts, "stroke", style.Stroke);
            AddStyle(parts, "strokeWidth", style.StrokeWidth);
            AddStyle(parts, "dash", style.Dash);
            AddStyle(parts, "arrowStart", style.ArrowStart);
            AddStyle(parts, "arrowEnd", style.ArrowEnd);
        }

        AddProps(parts, edge.Props);
        return string.Join(' ', parts);
    }

    private static string EndpointToken(Edge edge, Endpoint endpoint)
    {
        RequireId(endpoint.Node, $"an endpoint of edge '{edge.Id}'");
        if (endpoint.Port is { } port)
        {
            if (!DslSyntax.IsValidPortId(port))
            {
                throw new ArgumentException($"Cannot print a port on an endpoint of edge '{edge.Id}': '{port}' is not a valid port id (an id, or col:<name> for a table column).");
            }

            return $"{endpoint.Node}.{port}";
        }

        return endpoint.Node;
    }

    private static void AddStyle(List<string> parts, string field, string? value)
    {
        if (value is not null)
        {
            parts.Add($"{DslSyntax.StylePrefix}{field}={Value(value)}");
        }
    }

    private static void AddStyle(List<string> parts, string field, double? value)
    {
        if (value is { } number)
        {
            parts.Add($"{DslSyntax.StylePrefix}{field}={DslSyntax.FormatNumber(number)}");
        }
    }

    private static void AddStyle<T>(List<string> parts, string field, T? value)
        where T : struct, Enum
    {
        if (value is { } member)
        {
            parts.Add($"{DslSyntax.StylePrefix}{field}={DslSyntax.EnumName(member)}");
        }
    }

    private static void AddStyle(List<string> parts, string field, bool? value)
    {
        if (value is { } flag)
        {
            parts.Add($"{DslSyntax.StylePrefix}{field}={(flag ? "true" : "false")}");
        }
    }

    private static void AddProps(List<string> parts, IReadOnlyDictionary<string, JsonElement>? props)
    {
        if (props is null)
        {
            return;
        }

        foreach ((string key, JsonElement value) in props)
        {
            parts.Add($"{Key(key)}={PropValue(value)}");
        }
    }

    /// <summary>Props are JSON-shaped; strings are always quoted so a value like "42" stays a string.</summary>
    private static string PropValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => DslSyntax.Quote(value.GetString()!),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            _ => DslSyntax.CompactJson(value),
        };
    }

    private static string Key(string key)
    {
        return DslSyntax.IsBareKey(key) ? key : DslSyntax.Quote(key);
    }

    /// <summary>A positional string such as a kind or an author: bare when it is a plain word, quoted otherwise.</summary>
    private static string Token(string text)
    {
        return DslSyntax.IsBareToken(text) ? text : DslSyntax.Quote(text);
    }

    /// <summary>A string after <c>=</c> whose type is already known to be text: bare when it is a plain word, quoted otherwise.</summary>
    private static string Value(string text)
    {
        return DslSyntax.IsBareValue(text) ? text : DslSyntax.Quote(text);
    }

    private static void RequireId(string id, string what)
    {
        if (!DslSyntax.IsValidId(id))
        {
            throw new ArgumentException($"Cannot print {what}: '{id}' is not a valid id (letter or underscore, then letters, digits, '_', '.' or '-').");
        }
    }
}
