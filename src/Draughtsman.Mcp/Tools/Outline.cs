using System.Text;
using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Mcp.Tools;

/// <summary>
/// The compact <c>read_document</c> outline: one line per node and edge, no geometry or style, so it costs far
/// fewer tokens than the JSON or DSL forms when all an agent needs is what the diagram says. Every line carries the
/// element's id, because <c>patch_document</c> addresses nodes and edges by id and this is the cheap read.
/// </summary>
public static class Outline
{
    public static string Render(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder();
        text.Append(Escape(document.Title)).Append(" (").Append(document.Type.ToString().ToLowerInvariant()).Append(")\n");

        text.Append("nodes:\n");
        foreach (Node node in document.Nodes)
        {
            text.Append("  ").Append(node.Id).Append(" [").Append(Escape(node.Kind)).Append("] \"").Append(Escape(node.Label)).Append('"');
            if (node.Parent is { } parent)
            {
                text.Append(" in ").Append(parent);
            }

            text.Append('\n');
        }

        // In a sequence diagram the edge order is the time order, so the outline numbers the messages: "3." is the third
        // thing that happens, and "insert after 3" is a sentence an agent can act on with patch_document addEdge after.
        bool timeOrdered = document.Type == DocumentType.Sequence;
        text.Append(timeOrdered ? "edges (in time order):\n" : "edges:\n");
        int number = 0;
        foreach (Edge edge in document.Edges)
        {
            text.Append("  ");
            if (timeOrdered)
            {
                text.Append(++number).Append(". ");
            }

            text.Append(edge.Id).Append(": ").Append(edge.From.Node).Append(" -> ").Append(edge.To.Node).Append(" [").Append(Escape(edge.Kind)).Append(']');
            if (!string.IsNullOrEmpty(edge.Label))
            {
                text.Append(" \"").Append(Escape(edge.Label)).Append('"');
            }

            if (timeOrdered)
            {
                if (SequenceMessages.IsSet(edge, SequenceMessages.Activate))
                {
                    text.Append(" +activate");
                }

                if (SequenceMessages.IsSet(edge, SequenceMessages.Deactivate))
                {
                    text.Append(" +deactivate");
                }

                // A message's fragment, and which operand of it, are what the frame around it is drawn from.
                if (SequenceMessages.FragmentOf(edge) is { } fragment)
                {
                    text.Append(" in ").Append(Escape(fragment));
                    if (edge.Props is not null && edge.Props.TryGetValue(SequenceMessages.Operand, out JsonElement operand) && operand.ToString() is { Length: > 0 } operandText && operandText != "0")
                    {
                        text.Append(" operand ").Append(Escape(operandText));
                    }
                }
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Every free-form string the outline prints - title, kind, label - goes through this, so one line stays one
    /// line whatever the JSON write path stored. Ids are pattern-constrained by the schema and need no escaping.
    /// </summary>
    private static string Escape(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }
}
