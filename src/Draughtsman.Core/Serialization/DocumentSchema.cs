namespace Draughtsman.Core.Serialization;

/// <summary>
/// The document JSON Schema embedded from docs/schema/document.schema.json. Shared by the document REST API (which
/// validates every body before it reaches the store) and the MCP layer's patch/get_schema tools, so there is one
/// loader for the one committed contract rather than a copy per caller. The validation itself is <see cref="SchemaValidator"/>,
/// which implements the schema's keywords here in Core (docs/adr/0004-document-schema-validator.md) rather than through a library.
/// </summary>
public static class DocumentSchema
{
    private const string ResourceName = "schemas/document.schema.json";

    private static readonly Lazy<SchemaValidator> s_validator = new(() => SchemaValidator.Compile(RawJson));

    private static readonly Lazy<string> s_rawJson = new(ReadRaw);

    /// <summary>The schema file's own bytes, for the MCP get_schema tool and anything else that wants the file itself.</summary>
    public static string RawJson => s_rawJson.Value;

    /// <summary>Returns one "location: message" line per violation; empty when the JSON is a valid document.</summary>
    public static IReadOnlyList<string> Validate(string json)
    {
        return s_validator.Value.Validate(json);
    }

    private static string ReadRaw()
    {
        using Stream stream = typeof(DocumentSchema).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
