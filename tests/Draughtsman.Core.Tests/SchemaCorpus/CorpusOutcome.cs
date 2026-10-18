using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Tests.SchemaCorpus;

/// <summary>One JSON text of the corpus and the name that identifies it in the recorded expectations.</summary>
internal sealed record CorpusCase(string Id, string Json)
{
    /// <summary>First eight hex digits of the SHA-256 of the text: pins that the generator still builds the document the outcome was recorded for.</summary>
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Json)))[..8];
}

/// <summary>
/// What <see cref="DocumentSchema.Validate"/> did with one text: said it is a valid document, returned violation lines (verbatim, in the order it
/// returned them), or threw (the exception's type name).
/// </summary>
internal sealed record CorpusOutcome(string Kind, IReadOnlyList<string> Violations, string? Exception)
{
    public const string Valid = "valid";
    public const string Invalid = "invalid";
    public const string Throws = "throws";

    private static readonly JsonWriterOptions s_writerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static CorpusOutcome Observe(string json)
    {
        try
        {
            IReadOnlyList<string> lines = DocumentSchema.Validate(json);
            return lines.Count == 0 ? new CorpusOutcome(Valid, [], null) : new CorpusOutcome(Invalid, [.. lines], null);
        }
        catch (Exception error)
        {
            return new CorpusOutcome(Throws, [], error.GetType().Name);
        }
    }

    /// <summary>The instance location of each violation line ("(document)" or a JSON pointer), in order.</summary>
    public IEnumerable<string> Locations => Violations.Select(Location);

    public static string Location(string line)
    {
        int colon = line.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? line : line[..colon];
    }

    public string Describe()
    {
        return Kind switch
        {
            Valid => "valid",
            Throws => $"throws {Exception}",
            _ => "invalid: " + string.Join(" | ", Violations),
        };
    }

    public bool SameAs(CorpusOutcome other)
    {
        return Kind == other.Kind && Exception == other.Exception && Violations.SequenceEqual(other.Violations, StringComparer.Ordinal);
    }

    public string ToLine(CorpusCase source, string? why = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, s_writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", source.Id);
            writer.WriteString("sha", source.Fingerprint);
            writer.WriteString("o", Kind);
            if (why is not null)
            {
                writer.WriteString("why", why);
            }

            if (Kind == Invalid)
            {
                writer.WriteStartArray("v");
                foreach (string line in Violations)
                {
                    writer.WriteStringValue(line);
                }

                writer.WriteEndArray();
            }

            if (Exception is not null)
            {
                writer.WriteString("e", Exception);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static (string Id, string Sha, CorpusOutcome Outcome, string? Why) FromLine(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        string kind = root.GetProperty("o").GetString()!;
        string[] violations = root.TryGetProperty("v", out JsonElement v) ? [.. v.EnumerateArray().Select(x => x.GetString()!)] : [];
        string? exception = root.TryGetProperty("e", out JsonElement e) ? e.GetString() : null;
        string? why = root.TryGetProperty("why", out JsonElement w) ? w.GetString() : null;
        return (root.GetProperty("id").GetString()!, root.GetProperty("sha").GetString()!, new CorpusOutcome(kind, violations, exception), why);
    }
}
