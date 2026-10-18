using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Text;

/// <summary>
/// The lexical rules the parser and printer share, so a token the printer writes bare is always one the parser
/// reads back as the same value. Mirrors docs/spec/dsl.grammar.
/// </summary>
internal static partial class DslSyntax
{
    public const string Magic = "draughtsman";

    public const string ContainerKind = "container";

    /// <summary>The statement that opens the tour-level settings, and the one that writes each stop of the camera path.</summary>
    public const string FlythroughStatement = "flythrough";

    public const string KeyframeStatement = "keyframe";

    /// <summary>The longest a keyframe may take to arrive or stay, as docs/schema/document.schema.json caps it: ten minutes.</summary>
    public const double MaxKeyframeMilliseconds = 600000;

    public const string DefaultShape = "rectangle";

    /// <summary>Size given to a node written as <c>@ x,y</c> with no <c>wxh</c>.</summary>
    public const double DefaultWidth = 160;

    public const double DefaultHeight = 80;

    /// <summary>Author recorded when the DSL carries no <c>meta</c> line.</summary>
    public const string DefaultCreatedBy = "dsl";

    public const string StylePrefix = "style.";

    /// <summary>Same form as <see cref="Serialization.UtcTimestampConverter"/>: UTC, milliseconds, Z suffix. The only form the parser accepts.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public const string TimestampExample = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public const string TimestampSample = "2026-09-20T22:00:00.000Z";

    /// <summary>Bare keys the parser interprets itself; a prop with one of these names must be written quoted.</summary>
    public static readonly IReadOnlySet<string> ReservedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "kind", "label", "shape", "parent", "rotation", "ports", "routing", "waypoints", "style",
    };

    private static readonly JsonSerializerOptions s_compactJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]*\\z", RegexOptions.None, RegexLimits.TimeoutMilliseconds)]
    public static partial Regex IdPattern();

    /// <summary>A derived column port on an ER entity: <c>col:</c> then the column name with every byte outside letters, digits, '_' and '-' as <c>%XX</c> (<see cref="Schemas.ErShapeLibrary.ColumnPortId"/>).</summary>
    [GeneratedRegex("^col:[A-Za-z0-9_%-]+\\z", RegexOptions.None, RegexLimits.TimeoutMilliseconds)]
    public static partial Regex ColumnPortPattern();

    /// <summary>An edge endpoint token: a node id, optionally followed by <c>.port</c> where the port is a column port (any other port is an id and matches <see cref="IdPattern"/>'s own dots).</summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]*(\\.col:[A-Za-z0-9_%-]+)?\\z", RegexOptions.None, RegexLimits.TimeoutMilliseconds)]
    public static partial Regex EndpointPattern();

    /// <summary>A JSON number. Used for geometry, port fractions, waypoints and numeric prop values alike.</summary>
    [GeneratedRegex("^-?(0|[1-9][0-9]*)(\\.[0-9]+)?([eE][+-]?[0-9]+)?\\z", RegexOptions.None, RegexLimits.TimeoutMilliseconds)]
    public static partial Regex NumberPattern();

    /// <summary>Mirrors the schema's node shape pattern; kept in step with docs/schema/document.schema.json.</summary>
    [GeneratedRegex("^(rectangle|rounded|diamond|cylinder|pill|hexagon|parallelogram|document|predefined-process|manual-input|off-page-connector|delay|manual-operation|display|stored-data|multi-document|merge|extract|on-page-connector|summing-junction|or|collate|sort|loop-limit|internal-storage|card|text|image|custom:[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+)\\z", RegexOptions.None, RegexLimits.TimeoutMilliseconds)]
    public static partial Regex ShapePattern();

    public static bool IsValidId(string text)
    {
        return text.Length is > 0 and <= 128 && IdPattern().IsMatch(text);
    }

    /// <summary>The id of a port an edge endpoint names: a declared port's id, or a column port (<c>col:&lt;name&gt;</c>), at most 128 characters.</summary>
    public static bool IsValidPortId(string text)
    {
        return text.Length is > 0 and <= 128 && (IdPattern().IsMatch(text) || ColumnPortPattern().IsMatch(text));
    }

    public static bool IsReservedKey(string key)
    {
        return ReservedKeys.Contains(key) || key.StartsWith(StylePrefix, StringComparison.Ordinal);
    }

    /// <summary>A key the printer may write without quotes: identifier characters plus dots, and not reserved.</summary>
    public static bool IsBareKey(string key)
    {
        return IdPattern().IsMatch(key) && !IsReservedKey(key);
    }

    /// <summary>A positional token (kind, shape, author) the printer may write without quotes.</summary>
    public static bool IsBareToken(string text)
    {
        return text.Length > 0 && text[0] != '#' && text.All(IsWordChar);
    }

    /// <summary>A value after <c>=</c> the printer may write without quotes. A leading # is fine there: comments start only after whitespace.</summary>
    public static bool IsBareValue(string text)
    {
        return text.Length > 0 && text.All(IsWordChar);
    }

    /// <summary>Characters a bare word may contain: anything printable except the punctuation the grammar gives meaning.</summary>
    public static bool IsWordChar(char c)
    {
        return !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not ('"' or '{' or '}' or '[' or ']' or '=');
    }

    public static bool IsNumber(string text)
    {
        return NumberPattern().IsMatch(text);
    }

    /// <summary>The shapes docs/schema/document.schema.json admits; any other value makes a document it rejects.</summary>
    public static bool IsValidShape(string text)
    {
        return ShapePattern().IsMatch(text);
    }

    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentException("The DSL cannot represent NaN or infinite numbers.", nameof(value));
        }

        // Shortest round-trippable form, so parse(print(x)) == x exactly.
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The value a DSL number denotes, or false when the text is not a number or is out of range for a double.
    /// The grammar admits any exponent, so <c>1e400</c> matches <see cref="NumberPattern"/> and <c>double.Parse</c>
    /// answers Infinity rather than throwing; the document cannot carry that and <see cref="FormatNumber"/> refuses
    /// to print it, so it is rejected here, where text becomes a number, rather than anywhere downstream.
    /// </summary>
    public static bool TryParseNumber(string text, out double value)
    {
        if (NumberPattern().IsMatch(text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed))
        {
            value = parsed;
            return true;
        }

        value = 0;
        return false;
    }

    public static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }

    public static bool TryParseTimestamp(string text, out DateTimeOffset value)
    {
        return DateTimeOffset.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    /// <summary>Enum member as it appears in the DSL and in JSON: camelCase.</summary>
    public static string EnumName<T>(T value)
        where T : struct, Enum
    {
        return JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    }

    public static bool TryParseEnum<T>(string text, out T value)
        where T : struct, Enum
    {
        foreach (T candidate in Enum.GetValues<T>())
        {
            if (string.Equals(EnumName(candidate), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static string EnumNames<T>()
        where T : struct, Enum
    {
        return string.Join(", ", Enum.GetValues<T>().Select(EnumName));
    }

    /// <summary>The shape a node of this kind takes when none is written: the vocabulary default, else rectangle.</summary>
    public static string DefaultShapeFor(DocumentType type, string kind)
    {
        if (KindVocabulary.TryGet(type, out KindVocabulary? vocabulary))
        {
            KindDefinition? definition = vocabulary!.NodeKinds.FirstOrDefault(k => string.Equals(k.Name, kind, StringComparison.Ordinal));
            if (definition?.DefaultShape is { } shape)
            {
                return shape;
            }
        }

        return DefaultShape;
    }

    /// <summary>A JSON string literal. The DSL's quoted strings are exactly JSON strings, so labels with quotes, backslashes or newlines survive.</summary>
    public static string Quote(string text)
    {
        return JsonSerializer.Serialize(text, s_compactJson);
    }

    /// <summary>Arrays and objects in props are written as compact JSON so a node stays on one line.</summary>
    public static string CompactJson(JsonElement element)
    {
        return JsonSerializer.Serialize(element, s_compactJson);
    }

    /// <summary>Document id derived from the title when the DSL has no <c>id</c> line: "Order service" becomes doc_order_service.</summary>
    public static string IdFromTitle(string title)
    {
        var slug = new System.Text.StringBuilder("doc");
        bool pendingSeparator = true;
        foreach (char c in title)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingSeparator)
                {
                    slug.Append('_');
                    pendingSeparator = false;
                }

                slug.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSeparator = true;
            }

            if (slug.Length >= 64)
            {
                break;
            }
        }

        return slug.ToString();
    }
}
