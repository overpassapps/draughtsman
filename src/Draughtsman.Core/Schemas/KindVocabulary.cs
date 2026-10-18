using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Schemas;

/// <summary>
/// The allowed node and edge kinds for one document type, loaded from the JSON files under Schemas/ at runtime.
/// Agents fetch the same JSON through the MCP get_schema tool, so the file is the contract, not this class.
/// </summary>
public sealed record KindVocabulary(
    string Type,
    string Description,
    IReadOnlyList<KindDefinition> NodeKinds,
    IReadOnlyList<KindDefinition> EdgeKinds)
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Lazy<IReadOnlyDictionary<DocumentType, KindVocabulary>> s_all = new(LoadAll);

    /// <summary>Every vocabulary shipped in this build, keyed by document type. Freeform has no vocabulary of its own.</summary>
    public static IReadOnlyDictionary<DocumentType, KindVocabulary> All => s_all.Value;

    public static KindVocabulary For(DocumentType type)
    {
        return All.TryGetValue(type, out KindVocabulary? vocabulary)
            ? vocabulary
            : throw new KeyNotFoundException($"No kind vocabulary is shipped for document type '{type}'.");
    }

    public static bool TryGet(DocumentType type, out KindVocabulary? vocabulary)
    {
        return All.TryGetValue(type, out vocabulary);
    }

    public bool IsKnownNodeKind(string kind)
    {
        return NodeKinds.Any(k => string.Equals(k.Name, kind, StringComparison.Ordinal));
    }

    public bool IsKnownEdgeKind(string kind)
    {
        return EdgeKinds.Any(k => string.Equals(k.Name, kind, StringComparison.Ordinal));
    }

    /// <summary>Raw JSON of the shipped schema file, for the MCP get_schema tool and for agents that want the file itself.</summary>
    public static string ReadRawJson(DocumentType type)
    {
        using Stream stream = OpenResource(type);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyDictionary<DocumentType, KindVocabulary> LoadAll()
    {
        var result = new Dictionary<DocumentType, KindVocabulary>();
        foreach (DocumentType type in new[] { DocumentType.Flowchart, DocumentType.Architecture, DocumentType.Sequence })
        {
            using Stream stream = OpenResource(type);
            KindVocabulary? vocabulary = JsonSerializer.Deserialize<KindVocabulary>(stream, s_options);
            result[type] = vocabulary ?? throw new InvalidDataException($"Schema file for '{type}' deserialised to null.");
        }

        return result;
    }

    private static Stream OpenResource(DocumentType type)
    {
        string name = $"schemas/{type.ToString().ToLowerInvariant()}.json";
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream ?? throw new FileNotFoundException($"Embedded schema '{name}' is missing from the Draughtsman.Core assembly.");
    }
}
