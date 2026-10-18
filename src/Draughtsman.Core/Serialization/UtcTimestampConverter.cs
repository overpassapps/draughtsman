using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Draughtsman.Core.Serialization;

/// <summary>
/// Writes timestamps in the canonical form JavaScript's Date.prototype.toISOString() produces: UTC, exactly three
/// fractional digits and a Z suffix ("2026-09-20T22:00:00.000Z"), so JSON authored by the SPA or the sidecar
/// round-trips through C# byte-stable. Reads any ISO 8601 offset.
/// </summary>
public sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    private const string WireFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>The canonical text form, for callers that write timestamps into JSON by hand (the server stamping meta).</summary>
    public static string Format(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString(WireFormat, CultureInfo.InvariantCulture);
    }

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetDateTimeOffset();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(Format(value));
    }
}
