using System.Text.Json;
using Draughtsman.Core.Models;

namespace Draughtsman.Core.Schemas;

/// <summary>
/// The message props of a sequence diagram (Schemas/sequence.json): <c>activate</c> opens an activation bar on the
/// receiver and <c>deactivate</c> closes the sender's innermost open one. The drawing side is
/// web/src/model/sequenceScene.ts (<c>isSet</c>); both accept exactly the same two spellings of "on".
/// </summary>
public static class SequenceMessages
{
    public const string Activate = "activate";
    public const string Deactivate = "deactivate";

    /// <summary>The node kind of a combined fragment, the edge kinds that are not plain messages, and the props a message names its fragment with.</summary>
    public const string FragmentKind = "fragment";
    public const string NoteKind = "note";
    public const string InteractionUseKind = "interaction-use";
    public const string CreateKind = "create";
    public const string DestroyKind = "destroy";
    public const string In = "in";
    public const string Operand = "operand";
    public const string OperatorProp = "operator";
    public const string OperandsProp = "operands";

    /// <summary>
    /// The operators a fragment's tab may say without a critique warning (UML's interaction operators): a fragment's
    /// <c>operator</c> is any string, so an unknown one is a heads-up, not an error.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "alt", "opt", "loop", "par", "break", "critical", "neg", "assert", "ref", "seq", "strict", "ignore", "consider",
    };

    /// <summary>
    /// The prop is on when it is JSON <c>true</c>; the string "true" counts too, since a model quotes every value it is
    /// unsure of. Anything else, and an absent prop, is off.
    /// </summary>
    public static bool IsSet(Edge edge, string key)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (edge.Props is null || !edge.Props.TryGetValue(key, out JsonElement value))
        {
            return false;
        }

        return value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The id a message names as its fragment (<c>in</c>), or null when it names none or the value is not a string.</summary>
    public static string? FragmentOf(Edge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.Props is not null && edge.Props.TryGetValue(In, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>A fragment's operator: its <c>operator</c> prop when that is a non-blank string, else its label (the drawing's tab text).</summary>
    public static string OperatorOf(Node fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (fragment.Props is not null && fragment.Props.TryGetValue(OperatorProp, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!.Trim();
        }

        return fragment.Label;
    }

    /// <summary>One level of the fragments a message sits in: the fragment and the operand of it the message is under.</summary>
    public readonly record struct FragmentLevel(string Fragment, int Operand);

    /// <summary>
    /// The fragments each message sits in, by edge id, outermost first: its own <c>in</c> fragment (under its own
    /// <c>operand</c>) and the fragments that one nests in through <c>parent</c>. The operand a nested fragment sits under in its
    /// parent is its own <c>operand</c> prop, else that of the parent's nearest earlier direct member (0 when there is none), fixed at
    /// its first use. The same reading as <c>sequenceMemberships</c> in web/src/model/sequenceScene.ts, which draws the frames.
    /// A message that names no fragment node has no entry.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<FragmentLevel>> Memberships(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var fragments = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes.Where(n => string.Equals(n.Kind, FragmentKind, StringComparison.Ordinal)))
        {
            fragments.TryAdd(node.Id, node);
        }

        var lastOperand = new Dictionary<string, int>(StringComparer.Ordinal);
        var nestedOperand = new Dictionary<string, int>(StringComparer.Ordinal);
        var found = new Dictionary<string, IReadOnlyList<FragmentLevel>>(StringComparer.Ordinal);
        foreach (Edge edge in document.Edges)
        {
            if (FragmentOf(edge) is not { } inside || !fragments.ContainsKey(inside))
            {
                continue;
            }

            var chain = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? current = inside;
            while (current is not null && fragments.TryGetValue(current, out Node? node) && seen.Add(current))
            {
                chain.Insert(0, current);
                current = node.Parent;
            }

            int own = OperandIndex(edge.Props, Operand);
            var levels = new List<FragmentLevel>();
            for (int level = 0; level < chain.Count; level++)
            {
                if (level == chain.Count - 1)
                {
                    levels.Add(new FragmentLevel(chain[level], own));
                    continue;
                }

                string child = chain[level + 1];
                if (!nestedOperand.TryGetValue(child, out int operand))
                {
                    operand = fragments[child].Props is { } props && props.ContainsKey(Operand)
                        ? OperandIndex(props, Operand)
                        : lastOperand.GetValueOrDefault(chain[level]);
                    nestedOperand[child] = operand;
                }

                levels.Add(new FragmentLevel(chain[level], operand));
            }

            lastOperand[inside] = own;
            found[edge.Id] = levels;
        }

        return found;
    }

    /// <summary>The guard strings of a fragment (<c>operands</c>); a non-string entry is an empty guard.</summary>
    public static IReadOnlyList<string> OperandsOf(Node fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (fragment.Props is null || !fragment.Props.TryGetValue(OperandsProp, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray().Select(guard => guard.ValueKind == JsonValueKind.String ? guard.GetString() ?? string.Empty : string.Empty)];
    }

    private static int OperandIndex(IReadOnlyDictionary<string, JsonElement>? props, string key)
    {
        if (props is null || !props.TryGetValue(key, out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return Math.Max(number, 0) == number ? number : 0;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
    }
}
