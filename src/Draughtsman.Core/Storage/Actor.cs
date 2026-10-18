using System.Diagnostics.CodeAnalysis;
using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Storage;

/// <summary>
/// The identity behind a change. Serialised as <c>user:&lt;name&gt;</c> or <c>agent:&lt;name&gt;/&lt;model&gt;</c>, which is
/// the form the X-Draughtsman-Actor header, the audit table and the update log all use, so one parser covers them all.
/// Until local accounts land the default actor is <see cref="Local"/>.
/// </summary>
public sealed record Actor(ActorKind Kind, string Name, string? Model = null)
{
    public static readonly Actor Local = new(ActorKind.User, "local");

    /// <summary>Parses the wire form. Returns false for an empty string, an unknown kind, or an agent without a model.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Actor? actor)
    {
        actor = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        string kind = text[..colon];
        string rest = text[(colon + 1)..];
        if (string.Equals(kind, "user", StringComparison.OrdinalIgnoreCase))
        {
            if (rest.Contains('/', StringComparison.Ordinal))
            {
                return false;
            }

            actor = new Actor(ActorKind.User, rest);
            return true;
        }

        if (string.Equals(kind, "agent", StringComparison.OrdinalIgnoreCase))
        {
            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == rest.Length - 1)
            {
                return false;
            }

            actor = new Actor(ActorKind.Agent, rest[..slash], rest[(slash + 1)..]);
            return true;
        }

        return false;
    }

    public static Actor Parse(string text)
    {
        return TryParse(text, out Actor? actor)
            ? actor
            : throw new FormatException($"'{text}' is not an actor. Expected user:<name> or agent:<name>/<model>.");
    }

    public override string ToString()
    {
        return Kind == ActorKind.Agent ? $"agent:{Name}/{Model}" : $"user:{Name}";
    }
}
