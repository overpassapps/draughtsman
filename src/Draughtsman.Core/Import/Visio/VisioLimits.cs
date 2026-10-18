namespace Draughtsman.Core.Import.Visio;

/// <summary>
/// What the Visio reader will take. A .vsdx file is untrusted: a ZIP of XML, so it can lie about its own sizes and it can
/// inflate a thousandfold, and the XML in it can nest and repeat without end. Every dimension has a ceiling, and crossing one
/// refuses the source (<see cref="SourceRejectedException"/>) rather than reading on. The ceilings are far above any real
/// drawing (a 10,000-shape page is a few megabytes of XML) and far below anything that hurts.
/// </summary>
internal static class VisioLimits
{
    /// <summary>
    /// The package itself, in bytes: the figure the other importers allow a request (1,000,000 characters at up to three bytes
    /// each). Larger files are refused before the first byte is read as a ZIP.
    /// </summary>
    public const int MaxFileBytes = 3_000_000;

    /// <summary>Entries in the ZIP. A drawing with hundreds of masters has a few thousand parts; a ZIP of a million empty files has a million.</summary>
    public const int MaxEntries = 5_000;

    /// <summary>One entry's uncompressed size as the ZIP declares it, and as it actually inflates (the two must agree).</summary>
    public const long MaxEntryBytes = 64L * 1024 * 1024;

    /// <summary>The declared uncompressed sizes of every entry added up, checked from the directory before anything is inflated.</summary>
    public const long MaxDeclaredTotalBytes = 256L * 1024 * 1024;

    /// <summary>What every part the reader actually inflates may add up to, counted as the bytes come out of the decompressor.</summary>
    public const long MaxInflatedBytes = 128L * 1024 * 1024;

    /// <summary>A ZIP entry name, in characters.</summary>
    public const int MaxEntryNameLength = 260;

    /// <summary>Relationships in one .rels part.</summary>
    public const int MaxRelationships = 2_000;

    /// <summary>Elements read across every part of one import, so a part of nothing but <c>&lt;a/&gt;</c> costs a bounded amount.</summary>
    public const int MaxElements = 2_000_000;

    /// <summary>
    /// How deep XML may nest. A shape inside a group inside a group is two levels per group, and Visio's own nesting of
    /// <c>Shapes/Shape/Section/Row/Cell</c> is five, so this allows a group nested about twenty-eight deep, which no drawing is.
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>A cell carries a handful of attributes; this is generous.</summary>
    public const int MaxAttributesPerElement = 100;

    /// <summary>Shapes on the chosen page, groups' children included (a master's shapes are counted against the same figure).</summary>
    public const int MaxShapes = 10_000;

    /// <summary>Shapes across every master the page's shapes use. A stencil's masters are a few shapes each; this is for a hostile file, not a real one.</summary>
    public const int MaxMasterShapes = 50_000;

    public const int MaxPages = 500;

    /// <summary>Masters listed in <c>masters.xml</c>.</summary>
    public const int MaxMasters = 5_000;

    /// <summary>Connections (glue records) on one page.</summary>
    public const int MaxConnects = 50_000;

    /// <summary>Layers a page's sheet may list, and the colours a document may define.</summary>
    public const int MaxLayers = 256;

    public const int MaxColours = 1_000;

    /// <summary>Geometry sections on one shape and rows in one section: a drawn outline has a handful; a shape past these is a custom drawing.</summary>
    public const int MaxGeometrySections = 16;

    public const int MaxGeometryRows = 128;

    /// <summary>A page's or a master's name, cut to this.</summary>
    public const int MaxNameLength = 200;

    /// <summary>One label, in characters.</summary>
    public const int MaxLabelLength = 10_000;

    /// <summary>Warnings listed in a result; past this a last one says how many more there were.</summary>
    public const int MaxWarnings = 500;

    /// <summary>Coordinates beyond this (in either direction) are not a position on any real canvas.</summary>
    public const double MaxCoordinate = 10_000_000;

    /// <summary>
    /// Canvas units in one inch of Visio's page: the factor draw.io's own Visio importer uses, and five of the editor's 20-unit grid steps,
    /// so a Visio inch grid lands on the editor's grid. Fonts and line weights are scaled by it too (a Visio point is 100/72 of a unit).
    /// </summary>
    public const double UnitsPerInch = 100;
}
