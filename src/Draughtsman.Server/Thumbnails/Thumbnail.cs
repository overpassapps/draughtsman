namespace Draughtsman.Server.Thumbnails;

/// <summary>What a thumbnail turned out to be, reported in the <c>X-Draughtsman-Thumbnail</c> response header.</summary>
public enum ThumbnailKind
{
    /// <summary>The whole picture.</summary>
    Full,

    /// <summary>Boxes and lines only: the diagram was too large to draw in full.</summary>
    Simplified,

    /// <summary>A dashed empty frame: the document has no nodes or edges.</summary>
    Empty,

    /// <summary>A neutral placeholder: the diagram is larger than even the simplified drawing allows.</summary>
    TooLarge,

    /// <summary>A neutral placeholder: the document could not be drawn (an id used twice, or the layout engine failed).</summary>
    Unavailable,
}

/// <summary>
/// A finished thumbnail: the SVG as UTF-8 bytes, the version of the document it was drawn from, and what it is.
/// <paramref name="Degraded"/> marks one that is worse than it would normally be because something failed on the way (the layout
/// engine was down), so it is neither cached nor given an ETag: the next request tries again.
/// </summary>
public sealed record Thumbnail(byte[] Svg, string Version, string Variant, ThumbnailKind Kind, bool Degraded);
