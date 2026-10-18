namespace Draughtsman.Core.Import.Visio;

/// <summary>
/// A shape the way Visio resolves it: what the shape itself set, then what the master's shape it is an instance of sets. A dropped master
/// shape stores only the cells it changed (its position, its text), so its size, its outline and its line ends are read from the master. A
/// style cell (a colour, a line weight) is inherited from the master only when the master wrote it down; a master that asks the theme for it
/// (<c>THEMEVAL</c>) wrote a value that was worked out for the stencil's own theme, which is not the page's, so it is left to the diagram theme.
/// Visio styles (the named line, fill and text styles of the document) are not applied.
/// </summary>
internal sealed class VisioShapeView
{
    private VisioShapeView(VisioShape shape, VisioShape? inherited, VisioMaster? master)
    {
        Shape = shape;
        Inherited = inherited;
        Master = master;
    }

    public VisioShape Shape { get; }

    /// <summary>The master's shape this shape inherits from, or null for a shape that is not an instance of a master.</summary>
    public VisioShape? Inherited { get; }

    /// <summary>The master this shape, or the group it sits in, is an instance of.</summary>
    public VisioMaster? Master { get; }

    /// <summary>True when the shape is an instance of a master itself (the top of an instance), not just a part of one.</summary>
    public bool IsMasterInstance => Shape.Master is not null;

    /// <summary>The shape's own name (<c>NameU</c>), or the master's when the shape is an instance of one: the master's is the stable one.</summary>
    public string Name => IsMasterInstance && Master is { NameU.Length: > 0 } ? Master.NameU : Shape.NameU.Length > 0 ? Shape.NameU : Inherited?.NameU ?? string.Empty;

    public string Type => Shape.Type;

    public string? Text => Shape.Text;

    public IReadOnlyList<VisioGeometrySection> Geometry => Shape.Geometry.Count > 0 ? Shape.Geometry : Inherited?.Geometry ?? [];

    public bool GeometryTruncated => Shape.GeometryTruncated || (Shape.Geometry.Count == 0 && Inherited?.GeometryTruncated == true);

    /// <summary>The view of <paramref name="shape"/>, a child of a shape whose master is <paramref name="parentMaster"/> (null for a top-level shape).</summary>
    public static VisioShapeView Of(VisioShape shape, VisioMaster? parentMaster, IReadOnlyDictionary<string, VisioMaster> masters)
    {
        VisioMaster? master = parentMaster;
        if (shape.Master is { } masterId && masters.TryGetValue(masterId, out VisioMaster? own))
        {
            master = own;
        }

        VisioShape? inherited = null;
        if (master is not null)
        {
            if (shape.MasterShape is { } id)
            {
                master.Shapes.TryGetValue(id, out inherited);
            }
            else if (shape.Master is not null)
            {
                inherited = master.Root;
            }
        }

        return new VisioShapeView(shape, inherited, master);
    }

    /// <summary>A cell's value as the shape or, failing that, its master's shape wrote it; null when neither did.</summary>
    public string? Cell(string name)
    {
        return Shape.Cells.TryGetValue(name, out string? own) ? own : Inherited is not null && Inherited.Cells.TryGetValue(name, out string? inherited) ? inherited : null;
    }

    /// <summary>A style cell's value: the shape's own, or the master's when the master wrote it down rather than asking the theme.</summary>
    public string? StyleCell(string name)
    {
        if (Shape.Cells.TryGetValue(name, out string? own))
        {
            return own;
        }

        return Inherited is not null && !Inherited.ThemedCells.Contains(name) && Inherited.Cells.TryGetValue(name, out string? inherited) ? inherited : null;
    }

    /// <summary>True when any of the cells takes its value from the theme, on the shape or on its master's shape.</summary>
    public bool IsThemed(params string[] names)
    {
        return names.Any(n => Shape.ThemedCells.Contains(n) || Inherited?.ThemedCells.Contains(n) == true);
    }

    public double? Number(string name)
    {
        return VisioXml.Number(Cell(name));
    }

    public bool Has(string name)
    {
        return Cell(name) is not null;
    }
}
