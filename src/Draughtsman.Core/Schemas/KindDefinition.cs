using Draughtsman.Core.Models;

namespace Draughtsman.Core.Schemas;

/// <summary>One entry in a kind vocabulary: the kind name, a plain-English meaning, the default shape and the props convention.</summary>
public sealed record KindDefinition(string Name, string Description)
{
    /// <summary>Default shape when a node of this kind has none. Null for edge kinds.</summary>
    public string? DefaultShape { get; init; }

    /// <summary>Conventional prop names agents may set, with a one-line meaning each.</summary>
    public IReadOnlyDictionary<string, string>? Props { get; init; }

    /// <summary>
    /// The style an edge of this kind draws with when the diagram theme is not in play (the server renderer, which does
    /// not resolve theme tokens). Sequence message kinds carry one (dashed return, open async head); the theme files
    /// carry the same values for the web canvas, and a test keeps the two aligned. Null for kinds that have none.
    /// </summary>
    public EdgeStyle? DefaultStyle { get; init; }
}
