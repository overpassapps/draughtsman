using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Draughtsman.Tests.Shared;

/// <summary>
/// Builds .vsdx packages in test code, the way Visio lays them out (a ZIP of XML: the document, a page list, one part per page, the masters),
/// so no binary fixture is checked in and every byte of a test file can be read where the test is. The XML is written the way real Visio files
/// are (single-quoted attributes, <c>Cell N= V=</c>, <c>RelMoveTo</c> geometry, glue in a <c>Connects</c> element); the structure was checked
/// against real files saved by Visio. Everything is deterministic: entries carry a fixed timestamp.
/// </summary>
internal sealed class VisioDocBuilder
{
    public const string MainNamespace = "http://schemas.microsoft.com/office/visio/2012/main";
    public const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string VisioRelationshipBase = "http://schemas.microsoft.com/visio/2010/relationships/";

    private static readonly DateTimeOffset s_stamp = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private readonly List<(string Name, string Shapes, string Connects, string SheetExtra, string PageAttrs)> _pages = [];
    private readonly List<(int Id, string NameU, string Shapes)> _masters = [];

    /// <summary>The page size in inches, written into every page's sheet. A negative height leaves <c>PageHeight</c> out.</summary>
    public double PageWidth { get; set; } = 10;

    public double PageHeight { get; set; } = 8;

    /// <summary>Extra <c>ColorEntry</c> elements for the document's colour table.</summary>
    public string Colours { get; set; } = string.Empty;

    public VisioDocBuilder Page(string name, string shapes, string connects = "", string sheetExtra = "", string pageAttrs = "")
    {
        _pages.Add((name, shapes, connects, sheetExtra, pageAttrs));
        return this;
    }

    public VisioDocBuilder Master(int id, string nameU, string shapes)
    {
        _masters.Add((id, nameU, shapes));
        return this;
    }

    /// <summary>The package's parts in the order a real one stores them, as UTF-8 text without a byte order mark.</summary>
    public List<KeyValuePair<string, byte[]>> Parts()
    {
        var parts = new List<KeyValuePair<string, string>>
        {
            new("[Content_Types].xml", "<?xml version='1.0' encoding='utf-8'?><Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/xml'/><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Override PartName='/visio/document.xml' ContentType='application/vnd.ms-visio.drawing.main+xml'/></Types>"),
            new("_rels/.rels", Relationships(("rId1", "document", "visio/document.xml"))),
        };

        var documentRelationships = new List<(string Id, string Type, string Target)> { ("rId1", "pages", "pages/pages.xml") };
        if (_masters.Count > 0)
        {
            documentRelationships.Add(("rId2", "masters", "masters/masters.xml"));
        }

        parts.Add(new("visio/document.xml", $"<?xml version='1.0' encoding='utf-8' ?><VisioDocument xmlns='{MainNamespace}' xmlns:r='{RelationshipsNamespace}' xml:space='preserve'><DocumentSettings TopPage='0' DefaultTextStyle='3' DefaultLineStyle='3' DefaultFillStyle='3' DefaultGuideStyle='4'><GlueSettings>9</GlueSettings></DocumentSettings><Colors>{Colours}</Colors><StyleSheets><StyleSheet ID='0' NameU='No Style'><Cell N='LineWeight' V='0.01'/></StyleSheet></StyleSheets></VisioDocument>"));
        parts.Add(new("visio/_rels/document.xml.rels", Relationships(documentRelationships.ToArray())));

        var pageList = new StringBuilder($"<?xml version='1.0' encoding='utf-8' ?><Pages xmlns='{MainNamespace}' xmlns:r='{RelationshipsNamespace}' xml:space='preserve'>");
        var pageRelationships = new List<(string Id, string Type, string Target)>();
        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            string height = PageHeight >= 0 ? $"<Cell N='PageHeight' V='{Num(PageHeight)}'/>" : string.Empty;
            pageList.Append($"<Page ID='{i * 4}' NameU='{Esc(page.Name)}' Name='{Esc(page.Name)}' {page.PageAttrs}><PageSheet LineStyle='0' FillStyle='0' TextStyle='0'><Cell N='PageWidth' V='{Num(PageWidth)}'/>{height}{page.SheetExtra}</PageSheet><Rel r:id='rId{i + 1}'/></Page>");
            pageRelationships.Add(($"rId{i + 1}", "page", $"page{i + 1}.xml"));
            parts.Add(new($"visio/pages/page{i + 1}.xml", $"<?xml version='1.0' encoding='utf-8' ?><PageContents xmlns='{MainNamespace}' xmlns:r='{RelationshipsNamespace}' xml:space='preserve'><Shapes>{page.Shapes}</Shapes>{(page.Connects.Length > 0 ? $"<Connects>{page.Connects}</Connects>" : string.Empty)}</PageContents>"));
        }

        pageList.Append("</Pages>");
        parts.Add(new("visio/pages/pages.xml", pageList.ToString()));
        parts.Add(new("visio/pages/_rels/pages.xml.rels", Relationships(pageRelationships.ToArray())));

        if (_masters.Count > 0)
        {
            var masterList = new StringBuilder($"<?xml version='1.0' encoding='utf-8' ?><Masters xmlns='{MainNamespace}' xmlns:r='{RelationshipsNamespace}' xml:space='preserve'>");
            var masterRelationships = new List<(string Id, string Type, string Target)>();
            for (int i = 0; i < _masters.Count; i++)
            {
                (int id, string nameU, string shapes) = _masters[i];
                masterList.Append($"<Master ID='{id}' NameU='{Esc(nameU)}' Name='{Esc(nameU)}' MasterType='2'><PageSheet LineStyle='0' FillStyle='0' TextStyle='0'><Cell N='PageWidth' V='3.9'/></PageSheet><Icon>AAABAAEAICAQ</Icon><Rel r:id='rId{i + 1}'/></Master>");
                masterRelationships.Add(($"rId{i + 1}", "master", $"master{i + 1}.xml"));
                parts.Add(new($"visio/masters/master{i + 1}.xml", $"<?xml version='1.0' encoding='utf-8' ?><MasterContents xmlns='{MainNamespace}' xmlns:r='{RelationshipsNamespace}' xml:space='preserve'><Shapes>{shapes}</Shapes></MasterContents>"));
            }

            masterList.Append("</Masters>");
            parts.Add(new("visio/masters/masters.xml", masterList.ToString()));
            parts.Add(new("visio/masters/_rels/masters.xml.rels", Relationships(masterRelationships.ToArray())));
        }

        return parts.Select(p => new KeyValuePair<string, byte[]>(p.Key, Utf8(p.Value))).ToList();
    }

    public byte[] Build()
    {
        return Zip(Parts());
    }

    // ---- the ZIP -----------------------------------------------------------------------------------------------------------------

    public static byte[] Zip(IEnumerable<KeyValuePair<string, byte[]>> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, level);
                entry.LastWriteTime = s_stamp;
                using Stream stream = entry.Open();
                stream.Write(content);
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Like <see cref="Zip"/>, but the entries named in <paramref name="padded"/> are written as <c>prefix</c>, then <c>padBytes</c> of the letter a, then <c>suffix</c>,
    /// streamed through the compressor a megabyte at a time: a part that expands to tens of megabytes, as a decompression bomb does, without the test ever holding it.
    /// </summary>
    public static byte[] ZipPadded(IEnumerable<KeyValuePair<string, byte[]>> entries, IReadOnlyDictionary<string, (string Prefix, long PadBytes, string Suffix)> padded)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            byte[] chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)'a');
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
                entry.LastWriteTime = s_stamp;
                using Stream stream = entry.Open();
                if (!padded.TryGetValue(name, out var pad))
                {
                    stream.Write(content);
                    continue;
                }

                stream.Write(Utf8(pad.Prefix));
                for (long left = pad.PadBytes; left > 0; left -= chunk.Length)
                {
                    stream.Write(chunk, 0, (int)Math.Min(left, chunk.Length));
                }

                stream.Write(Utf8(pad.Suffix));
            }
        }

        return output.ToArray();
    }

    /// <summary>The package's own parts with one replaced (or added), for a test that needs one part to say something else.</summary>
    public static byte[] Replace(IEnumerable<KeyValuePair<string, byte[]>> parts, string name, string content)
    {
        var list = parts.Where(p => p.Key != name).ToList();
        list.Add(new KeyValuePair<string, byte[]>(name, Utf8(content)));
        return Zip(list);
    }

    public static byte[] Utf8(string text)
    {
        return new UTF8Encoding(false).GetBytes(text);
    }

    public static string Relationships(params (string Id, string Type, string Target)[] relationships)
    {
        return $"<?xml version='1.0' encoding='UTF-8' standalone='yes'?><Relationships xmlns='{PackageRelationshipsNamespace}'>"
            + string.Concat(relationships.Select(r => $"<Relationship Id='{r.Id}' Type='{(r.Type.StartsWith("http", StringComparison.Ordinal) ? r.Type : VisioRelationshipBase + r.Type)}' Target='{Esc(r.Target)}'/>"))
            + "</Relationships>";
    }

    // ---- shapes ------------------------------------------------------------------------------------------------------------------

    public static string Num(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string Esc(string text)
    {
        return text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("'", "&apos;", StringComparison.Ordinal);
    }

    public static string Cell(string name, double value, string? formula = null)
    {
        return $"<Cell N='{name}' V='{Num(value)}'{(formula is null ? string.Empty : $" F='{Esc(formula)}'")}/>";
    }

    public static string TextCell(string name, string value, string? formula = null)
    {
        return $"<Cell N='{name}' V='{Esc(value)}'{(formula is null ? string.Empty : $" F='{Esc(formula)}'")}/>";
    }

    /// <summary>
    /// A shape positioned by its centre (<paramref name="x"/>, <paramref name="y"/>, in inches from the page's bottom-left) with the size
    /// <paramref name="w"/> by <paramref name="h"/>, its pin at the middle as Visio's default. <paramref name="cells"/> and
    /// <paramref name="attributes"/> are written as they are; <paramref name="geometry"/> is a whole <c>Section N='Geometry'</c> (null for none, the default a plain rectangle).
    /// </summary>
    public static string Shape(int id, double x, double y, double w, double h, string? text = null, string attributes = "", string cells = "", string? geometry = "rect", string children = "", string type = "Shape", double angle = 0, string? nameU = null)
    {
        return $"<Shape ID='{id}' NameU='{Esc(nameU ?? $"Sheet.{id}")}' Type='{type}' {attributes}>"
            + Cell("PinX", x) + Cell("PinY", y) + Cell("Width", w) + Cell("Height", h)
            + Cell("LocPinX", w / 2, "Width*0.5") + Cell("LocPinY", h / 2, "Height*0.5") + Cell("Angle", angle)
            + cells + (geometry == "rect" ? Rect : geometry)
            + (text is null ? string.Empty : $"<Text>{Esc(text)}</Text>")
            + (children.Length > 0 ? $"<Shapes>{children}</Shapes>" : string.Empty)
            + "</Shape>";
    }

    /// <summary>A master's instance on a page: only the position and the text, as Visio stores a shape that overrides nothing else.</summary>
    public static string Instance(int id, int master, double x, double y, string? text = null, string cells = "", string type = "Shape", string children = "")
    {
        return $"<Shape ID='{id}' NameU='Sheet.{id}' Type='{type}' Master='{master}'>"
            + Cell("PinX", x) + Cell("PinY", y) + cells
            + (text is null ? string.Empty : $"<Text>{Esc(text)}</Text>")
            + (children.Length > 0 ? $"<Shapes>{children}</Shapes>" : string.Empty)
            + "</Shape>";
    }

    /// <summary>A child of an instance of a group master: it names which shape of the master it is, and inherits everything.</summary>
    public static string MasterChild(int id, int masterShape, string? text = null, string cells = "")
    {
        return $"<Shape ID='{id}' Type='Shape' MasterShape='{masterShape}'>{cells}{(text is null ? string.Empty : $"<Text>{Esc(text)}</Text>")}</Shape>";
    }

    /// <summary>A 1-D shape (a connector) between two points, as a dynamic connector writes it: no size to speak of, its ends in <c>BeginX</c> to <c>EndY</c>.</summary>
    public static string Connector(int id, double beginX, double beginY, double endX, double endY, string? text = null, string cells = "", string attributes = "Master='2'")
    {
        return $"<Shape ID='{id}' NameU='Dynamic connector.{id}' Type='Shape' {attributes}>"
            + Cell("PinX", (beginX + endX) / 2) + Cell("PinY", (beginY + endY) / 2)
            + Cell("BeginX", beginX) + Cell("BeginY", beginY) + Cell("EndX", endX) + Cell("EndY", endY)
            + cells
            + (text is null ? string.Empty : $"<Text>{Esc(text)}</Text>")
            + "</Shape>";
    }

    public static string Glue(int connector, int? begin, int? end)
    {
        string one(string cell, int part, int? to)
        {
            return to is null ? string.Empty : $"<Connect FromSheet='{connector}' FromCell='{cell}' FromPart='{part}' ToSheet='{to}' ToCell='PinX' ToPart='3'/>";
        }

        return one("BeginX", 9, begin) + one("EndX", 12, end);
    }

    // ---- geometry ----------------------------------------------------------------------------------------------------------------

    private static string Rows(params (string Type, double X, double Y)[] rows)
    {
        return string.Concat(rows.Select((r, i) => $"<Row T='{r.Type}' IX='{i + 1}'><Cell N='X' V='{Num(r.X)}'/><Cell N='Y' V='{Num(r.Y)}'/></Row>"));
    }

    public static string Section(string rows, bool noFill = false, bool noLine = false, bool noShow = false)
    {
        return $"<Section N='Geometry' IX='0'><Cell N='NoFill' V='{(noFill ? 1 : 0)}'/><Cell N='NoLine' V='{(noLine ? 1 : 0)}'/><Cell N='NoShow' V='{(noShow ? 1 : 0)}'/><Cell N='NoSnap' V='0'/>{rows}</Section>";
    }

    public static string Polygon(params (double X, double Y)[] points)
    {
        return Section(Rows(points.Select((p, i) => (i == 0 ? "RelMoveTo" : "RelLineTo", p.X, p.Y)).ToArray()));
    }

    public static string Rect => Polygon((0, 0), (1, 0), (1, 1), (0, 1), (0, 0));

    public static string Diamond => Polygon((0.5, 0), (1, 0.5), (0.5, 1), (0, 0.5), (0.5, 0));

    public static string Parallelogram => Polygon((0.2, 0), (1, 0), (0.8, 1), (0, 1), (0.2, 0));

    public static string Hexagon => Polygon((0.25, 0), (0.75, 0), (1, 0.5), (0.75, 1), (0.25, 1), (0, 0.5), (0.25, 0));

    public static string Triangle => Polygon((0, 0), (1, 0), (0.5, 1), (0, 0));

    public static string Ellipse => Section("<Row T='Ellipse' IX='1'><Cell N='X' V='0.5'/><Cell N='Y' V='0.5'/><Cell N='A' V='1'/><Cell N='B' V='0.5'/><Cell N='C' V='0.5'/><Cell N='D' V='1'/></Row>");

    /// <summary>The rectangle written with absolute rows in inches (Visio's own <c>MoveTo</c> and <c>LineTo</c>), for a shape of width <paramref name="w"/> and height <paramref name="h"/>.</summary>
    public static string AbsoluteRect(double w, double h)
    {
        return Section(Rows(("MoveTo", 0, 0), ("LineTo", w, 0), ("LineTo", w, h), ("LineTo", 0, h), ("LineTo", 0, 0)));
    }

    /// <summary>No outline at all: neither filled nor stroked, as a text box is.</summary>
    public static string Invisible => Section(Rows(("RelMoveTo", 0, 0), ("RelLineTo", 1, 0), ("RelLineTo", 1, 1), ("RelLineTo", 0, 1), ("RelLineTo", 0, 0)), noFill: true, noLine: true);

    /// <summary>A rounded rectangle the way Visio draws one: a plain rectangle with its <c>Rounding</c> cell set.</summary>
    public static string Rounding(double inches)
    {
        return Cell("Rounding", inches);
    }

    public static string Character(string? color = null, double? sizeInches = null, int? style = null)
    {
        return "<Section N='Character'><Row IX='0'>"
            + (color is null ? string.Empty : TextCell("Color", color))
            + (sizeInches is null ? string.Empty : Cell("Size", sizeInches.Value))
            + (style is null ? string.Empty : Cell("Style", style.Value))
            + "</Row></Section>";
    }

    public static string ShapeData()
    {
        return "<Section N='Property'><Row N='Owner'><Cell N='Value' V='Team A' U='STR'/><Cell N='Label' V='Owner'/></Row></Section>";
    }

    public static string Layers(params (int Index, string Name, bool Visible)[] layers)
    {
        return "<Section N='Layer'>" + string.Concat(layers.Select(l => $"<Row IX='{l.Index}'><Cell N='Name' V='{Esc(l.Name)}'/><Cell N='Visible' V='{(l.Visible ? 1 : 0)}'/></Row>")) + "</Section>";
    }
}
