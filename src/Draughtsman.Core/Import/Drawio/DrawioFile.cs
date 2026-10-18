namespace Draughtsman.Core.Import.Drawio;

/// <summary>One page of a draw.io file: the name its tab shows (empty for a bare model) and the id draw.io gave it.</summary>
internal sealed record DrawioPageInfo(string Name, string? Id);

internal readonly record struct DrawioPoint(double X, double Y);

/// <summary>
/// A cell's <c>mxGeometry</c>. Every number draw.io leaves out is 0, as draw.io reads it, and a number that is not finite reads as 0 too.
/// A <see cref="Relative"/> geometry is an edge's, or an edge label's, whose x and y are not a position.
/// </summary>
internal sealed record DrawioGeometry(double X, double Y, double Width, double Height, bool Relative, IReadOnlyList<DrawioPoint> Points);

/// <summary>
/// One cell of the model as the file wrote it: a vertex (a shape), an edge (a connector), or one of the two structural cells every file starts
/// with (the root and the default layer, which are neither). A cell wrapped in <c>object</c> or <c>UserObject</c> carries that wrapper's custom
/// data in <see cref="Attributes"/> (its <c>id</c> and <c>label</c> are not in it: they are <see cref="Id"/> and <see cref="Value"/>).
/// </summary>
internal sealed class DrawioCell
{
    public required string Id { get; init; }

    public string? Parent { get; init; }

    public string? Source { get; init; }

    public string? Target { get; init; }

    public string? Style { get; init; }

    /// <summary>The label as written: HTML when the style says <c>html=1</c>, plain text otherwise.</summary>
    public string Value { get; init; } = string.Empty;

    public bool Vertex { get; init; }

    public bool Edge { get; init; }

    /// <summary>False for a cell the file hides (<c>visible="0"</c>).</summary>
    public bool Visible { get; init; } = true;

    public DrawioGeometry? Geometry { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}

/// <summary>The pages of a file and the cells of the one page that was read.</summary>
/// <param name="Pages">Every page, in file order; empty when the file has none.</param>
/// <param name="SelectedPage">Zero-based index of the page <paramref name="Cells"/> came from; -1 when the file has no pages.</param>
/// <param name="Cells">The cells of the selected page, in file order, structural cells included.</param>
internal sealed record DrawioFile(IReadOnlyList<DrawioPageInfo> Pages, int SelectedPage, IReadOnlyList<DrawioCell> Cells);
