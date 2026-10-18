namespace Draughtsman.Core.Import.Drawio;

/// <summary>
/// What the draw.io reader will take. A .drawio file is untrusted XML, usually compressed, so every dimension of it has a
/// ceiling and crossing one refuses the source (<see cref="SourceRejectedException"/>) rather than reading on: the
/// ceilings are far above any real diagram (a 10,000 shape file is a few megabytes) and far below anything that hurts.
/// </summary>
internal static class DrawioLimits
{
    /// <summary>The XML text of one page (or of the whole file when it is plain), in characters.</summary>
    public const int MaxXmlCharacters = 8 * 1024 * 1024;

    /// <summary>
    /// What a compressed page may inflate to, in bytes. draw.io URL-encodes the XML before it deflates it, which roughly doubles
    /// ordinary XML (every quote, angle bracket and space becomes three characters), so this is about twice the XML limit's worth of
    /// ordinary text. A deflate stream can expand about a thousandfold, so without it a megabyte of base64 is a gigabyte of memory.
    /// </summary>
    public const int MaxInflatedBytes = 16 * 1024 * 1024;

    /// <summary>Elements (not characters) in one read, so a file of nothing but <c>&lt;a/&gt;</c> costs a bounded amount of memory.</summary>
    public const int MaxElements = 150_000;

    /// <summary>A draw.io file nests nine deep at most (<c>mxfile/diagram/mxGraphModel/root/object/mxCell/mxGeometry/Array/mxPoint</c>).</summary>
    public const int MaxDepth = 32;

    /// <summary>A shape with custom data carries a few dozen attributes; this is generous.</summary>
    public const int MaxAttributesPerElement = 1_000;

    /// <summary>Shapes and connectors together (the root and layers do not count).</summary>
    public const int MaxCells = 10_000;

    public const int MaxPages = 500;

    /// <summary>A page's name, cut to this.</summary>
    public const int MaxNameLength = 200;

    /// <summary>One label, in characters, after it has been reduced to text.</summary>
    public const int MaxLabelLength = 10_000;

    /// <summary>Custom data on one shape: how many entries and how long each value may be.</summary>
    public const int MaxProps = 50;

    public const int MaxPropLength = 2_000;

    /// <summary>Points one connector is bent through.</summary>
    public const int MaxWaypoints = 500;

    /// <summary>Warnings listed in a result; past this a last one says how many more there were.</summary>
    public const int MaxWarnings = 500;

    /// <summary>
    /// The smallest box an icon shape (a stick figure, a cloud) is given, in canvas units. draw.io draws such a shape small with its label underneath at
    /// any width; here the label sits in a band of the node's own width, so a 30-wide actor would stack its label one letter per line.
    /// </summary>
    public const double IconMinSize = 96;

    /// <summary>Coordinates beyond this (in either direction) are not a position on any real canvas.</summary>
    public const double MaxCoordinate = 10_000_000;
}
