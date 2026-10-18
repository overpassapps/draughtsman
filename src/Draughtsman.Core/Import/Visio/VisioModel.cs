namespace Draughtsman.Core.Import.Visio;

/// <summary>A layer of a page's sheet (<c>Section N="Layer"</c>); only whether it is shown matters, since Draughtsman has no layers.</summary>
internal sealed record VisioLayer(int Index, string Name, bool Visible);

/// <summary>
/// One page as <c>pages.xml</c> lists it. <see cref="Width"/> and <see cref="Height"/> are the page's size in inches (null when the sheet does not
/// give it); Visio's origin is the page's bottom-left, so the height is what turns its y axis (up) into the canvas's (down).
/// </summary>
internal sealed record VisioPageInfo(string Id, string Name, string NameU, bool Background, string? BackPage, double? Width, double? Height, string? PartName, IReadOnlyList<VisioLayer> Layers);

/// <summary>One glue record of a page: the 1-D shape <see cref="FromSheet"/> has its <see cref="FromCell"/> (<c>BeginX</c>, <c>EndX</c>) glued to the shape <see cref="ToSheet"/>.</summary>
internal sealed record VisioConnect(string FromSheet, string FromCell, string ToSheet, string ToCell);

/// <summary>One row of a geometry section: its type (<c>MoveTo</c>, <c>RelLineTo</c>, <c>Ellipse</c> ...) and the cells it carries (NaN where it has none).</summary>
internal readonly record struct VisioGeometryRow(string Type, double X, double Y, double A, double B, double C, double D);

/// <summary>
/// One geometry section of a shape: a path, and the flags that say whether it is filled, stroked and shown. Rows <c>Rel*</c> are fractions of the
/// shape's width and height; the others are inches in the shape's own space.
/// </summary>
internal sealed class VisioGeometrySection
{
    public bool NoFill { get; set; }

    public bool NoLine { get; set; }

    public bool NoShow { get; set; }

    /// <summary>True when the section had more rows than are kept, so its outline is incomplete and must not be classified.</summary>
    public bool Truncated { get; set; }

    public List<VisioGeometryRow> Rows { get; } = [];
}

/// <summary>
/// A shape as the file wrote it, with only the cells the importer reads. A shape on a page is often a master's instance that overrides a few cells
/// (its position and its text) and inherits the rest, so <see cref="Cells"/> holds only what this shape itself set; the importer looks a missing
/// cell up in the master's shape (<see cref="VisioMaster"/>).
/// </summary>
internal sealed class VisioShape
{
    public required string Id { get; init; }

    public string NameU { get; init; } = string.Empty;

    public string Type { get; init; } = "Shape";

    /// <summary>The id of the master this shape is an instance of (a top-level shape of a page).</summary>
    public string? Master { get; init; }

    /// <summary>The id, inside the master, of the sub-shape this shape is the instance of (a child of a master's group).</summary>
    public string? MasterShape { get; init; }

    /// <summary>True for a shape the page marks deleted (<c>Del="1"</c>): it exists only to hide the master's shape of that id.</summary>
    public bool Deleted { get; init; }

    public Dictionary<string, string> Cells { get; } = new(StringComparer.Ordinal);

    /// <summary>The cells whose formula asks the theme for the value (<c>THEMEVAL</c>); their cached value was worked out for the theme they were written under.</summary>
    public HashSet<string> ThemedCells { get; } = new(StringComparer.Ordinal);

    public List<VisioGeometrySection> Geometry { get; } = [];

    /// <summary>True when the shape had more geometry sections than are kept.</summary>
    public bool GeometryTruncated { get; set; }

    /// <summary>The text of the shape, as written (null when it has none).</summary>
    public string? Text { get; set; }

    /// <summary>True when the shape carries shape data (custom properties), which is not imported.</summary>
    public bool HasShapeData { get; set; }

    /// <summary>The shape's <c>User.msvStructureType</c> (<c>Container</c>, <c>List</c> ...), which Visio sets on its container and list shapes.</summary>
    public string? StructureType { get; set; }

    /// <summary>True for a picture or embedded object (<c>Type="Foreign"</c>, or a shape with foreign data).</summary>
    public bool IsForeign { get; set; }

    public List<VisioShape> Children { get; } = [];
}

/// <summary>A master: what its instances inherit. <see cref="Shapes"/> indexes every shape of the master, nested ones too, by id.</summary>
internal sealed class VisioMaster(string id, string name, string nameU)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string NameU { get; } = nameU;

    /// <summary>The master's top-level shape: the one a page's instance of the master inherits from.</summary>
    public VisioShape? Root { get; set; }

    public Dictionary<string, VisioShape> Shapes { get; } = new(StringComparer.Ordinal);
}

/// <summary>The pages of a file and the contents of the one page that was read, with the masters its shapes use.</summary>
/// <param name="Pages">Every page, in file order; empty when the file has none.</param>
/// <param name="SelectedPage">Zero-based index of the page the rest came from; -1 when the file has no pages.</param>
/// <param name="Shapes">The top-level shapes of the selected page, in file order.</param>
/// <param name="Connects">The glue records of the selected page.</param>
/// <param name="Masters">The masters the selected page's shapes use, by master id.</param>
/// <param name="Palette">The document's colour table: Visio's 24 standard colours and any the document adds, by index.</param>
internal sealed record VisioFile(
    IReadOnlyList<VisioPageInfo> Pages,
    int SelectedPage,
    IReadOnlyList<VisioShape> Shapes,
    IReadOnlyList<VisioConnect> Connects,
    IReadOnlyDictionary<string, VisioMaster> Masters,
    IReadOnlyDictionary<int, string> Palette);
