using System.Text.Json;
using System.Text.Json.Serialization;
using Draughtsman.Core.Storage;

namespace Draughtsman.Core.Serialization;

/// <summary>
/// Actors travel as their wire string (<c>user:alice</c>, <c>agent:claude-code/claude-fable-5-1</c>), the same form
/// as the X-Draughtsman-Actor header, so API responses and the header agree.
/// </summary>
public sealed class ActorJsonConverter : JsonConverter<Actor>
{
    public override Actor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();
        return Actor.TryParse(text, out Actor? actor)
            ? actor
            : throw new JsonException($"'{text}' is not an actor. Expected user:<name> or agent:<name>/<model>.");
    }

    public override void Write(Utf8JsonWriter writer, Actor value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(value.ToString());
    }
}
