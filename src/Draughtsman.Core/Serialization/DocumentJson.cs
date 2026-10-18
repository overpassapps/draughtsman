using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Draughtsman.Core.Models;
using Draughtsman.Core.Storage;

namespace Draughtsman.Core.Serialization;

/// <summary>
/// The one place JSON options for the document model are defined. camelCase names, enums as camelCase strings,
/// nulls omitted so sparse style stays sparse. Every serialiser in the server, MCP and export paths uses these.
/// </summary>
public static class DocumentJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions(indented: false);

    public static readonly JsonSerializerOptions IndentedOptions = CreateOptions(indented: true);

    public static string Serialize(Document document, bool indented = false)
    {
        return JsonSerializer.Serialize(document, indented ? IndentedOptions : Options);
    }

    /// <summary>Deserialises a document. Throws <see cref="JsonException"/> on malformed input or a missing required member.</summary>
    public static Document Deserialize(string json)
    {
        Document? document = JsonSerializer.Deserialize<Document>(json, Options);
        return document ?? throw new JsonException("Document JSON was null.");
    }

    public static Document Deserialize(Stream json)
    {
        Document? document = JsonSerializer.Deserialize<Document>(json, Options);
        return document ?? throw new JsonException("Document JSON was null.");
    }

    /// <summary>
    /// Applies the document conventions to options owned by someone else, such as ASP.NET Core's HTTP JSON options,
    /// so API responses that embed a document serialise it byte-for-byte as <see cref="Serialize"/> would.
    /// </summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new UtcTimestampConverter());
        options.Converters.Add(new ActorJsonConverter());
    }

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions { WriteIndented = indented, NewLine = "\n" };
        Configure(options);
        return options;
    }
}
