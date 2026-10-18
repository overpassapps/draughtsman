using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Draughtsman.Core.Import.Drawio;

/// <summary>
/// Reads a draw.io file into pages and cells. The file is untrusted XML, so the reader is built to refuse rather than to be clever:
/// a DOCTYPE of any kind is refused before it is read (no entity expansion, no external entity, no DTD, no resolver, so nothing is
/// fetched from the disk or the network), a compressed page is inflated through a stream that stops at
/// <see cref="DrawioLimits.MaxInflatedBytes"/>, and elements, depth, attributes, pages and cells are all counted against their limits as
/// they are read. Only the chosen page is decoded; the others are skipped without being inflated. A refusal is a
/// <see cref="SourceRejectedException"/> whose message says what to do.
/// <para>
/// Three spellings of the same thing are accepted: an <c>mxfile</c> of <c>diagram</c> pages (each plain or compressed), a bare
/// <c>mxGraphModel</c>, and the bare compressed text a <c>diagram</c> holds.
/// </para>
/// </summary>
internal static class DrawioReader
{
    private static readonly string[] s_wrapperNames = ["object", "userobject", "xmlobject"];

    /// <param name="source">The file's text.</param>
    /// <param name="page">The page to read, counted from 1.</param>
    /// <exception cref="ArgumentException"><paramref name="page"/> is not a page of the file.</exception>
    /// <exception cref="SourceRejectedException">The source is not a draw.io file, or crosses a limit.</exception>
    public static DrawioFile Read(string source, int page = 1)
    {
        string text = source.TrimStart('﻿').Trim();
        if (text.Length == 0)
        {
            throw Reject("The draw.io source is empty.");
        }

        if (page < 1)
        {
            throw new ArgumentException($"Pages are numbered from 1; {page.ToString(CultureInfo.InvariantCulture)} is not a page.");
        }

        if (text.Length > DrawioLimits.MaxXmlCharacters)
        {
            throw Reject($"The draw.io source is larger than {DrawioLimits.MaxXmlCharacters.ToString("N0", CultureInfo.InvariantCulture)} characters, which is more than Draughtsman reads. Export fewer pages, or split the diagram.");
        }

        if (text[0] != '<')
        {
            return ReadBareCompressed(text, page);
        }

        try
        {
            return ReadXml(text, page);
        }
        catch (XmlException ex)
        {
            throw Reject(Describe(ex, "The draw.io file"), ex);
        }
    }

    private static DrawioFile ReadBareCompressed(string text, int page)
    {
        if (page != 1)
        {
            throw new ArgumentException($"The source is one compressed diagram, a single page; page {page.ToString(CultureInfo.InvariantCulture)} does not exist.");
        }

        string model;
        try
        {
            model = Inflate(text, "The compressed draw.io diagram");
        }
        catch (FormatException)
        {
            throw Reject("That is not a draw.io file: it is not XML, and it is not a compressed diagram either.");
        }

        return new DrawioFile([new DrawioPageInfo(string.Empty, null)], 0, ReadModel(model, "The compressed draw.io diagram"));
    }

    private static DrawioFile ReadXml(string text, int page)
    {
        using XmlReader reader = XmlReader.Create(new StringReader(text), CreateSettings());
        if (reader.MoveToContent() != XmlNodeType.Element)
        {
            throw Reject("That is not a draw.io file: it has no root element.");
        }

        var state = new ReadState();
        switch (reader.LocalName)
        {
            case "mxGraphModel":
                return new DrawioFile([new DrawioPageInfo(string.Empty, null)], 0, ReadCells(ReadElement(reader, state, 1)));
            case "mxfile":
                return ReadMxfile(reader, state, page);
            default:
                throw Reject($"That is not a draw.io file: its root element is <{Truncate(reader.LocalName, 40)}>, not <mxfile> or <mxGraphModel>.");
        }
    }

    private static DrawioFile ReadMxfile(XmlReader reader, ReadState state, int page)
    {
        var pages = new List<DrawioPageInfo>();
        Node? chosen = null;
        bool empty = reader.IsEmptyElement;
        reader.Read();
        while (!empty && reader.NodeType != XmlNodeType.EndElement && !reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "diagram")
            {
                if (pages.Count >= DrawioLimits.MaxPages)
                {
                    throw Reject($"The draw.io file has more than {DrawioLimits.MaxPages.ToString("N0", CultureInfo.InvariantCulture)} pages, which is more than Draughtsman reads.");
                }

                pages.Add(new DrawioPageInfo(Truncate(reader.GetAttribute("name") ?? string.Empty, DrawioLimits.MaxNameLength), reader.GetAttribute("id")));
                if (pages.Count == page)
                {
                    chosen = ReadElement(reader, state, 2);
                }
                else
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Skip();
            }
        }

        if (pages.Count == 0)
        {
            return page == 1
                ? new DrawioFile([], -1, [])
                : throw new ArgumentException($"The file has no pages, so page {page.ToString(CultureInfo.InvariantCulture)} does not exist.");
        }

        if (chosen is null)
        {
            string count = pages.Count == 1 ? "1 page" : $"{pages.Count.ToString(CultureInfo.InvariantCulture)} pages";
            throw new ArgumentException($"The file has {count}; page {page.ToString(CultureInfo.InvariantCulture)} does not exist (pages are numbered from 1).");
        }

        string label = $"Page {page.ToString(CultureInfo.InvariantCulture)}{(pages[page - 1].Name.Length > 0 ? $" (\"{pages[page - 1].Name}\")" : string.Empty)} of the draw.io file";
        Node? inline = chosen.Children.Find(c => c.Name == "mxGraphModel");
        IReadOnlyList<DrawioCell> cells;
        if (inline is not null)
        {
            cells = ReadCells(inline);
        }
        else
        {
            string payload = chosen.Text?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(payload))
            {
                cells = [];
            }
            else
            {
                try
                {
                    cells = ReadModel(Inflate(payload, label), label);
                }
                catch (FormatException)
                {
                    throw Reject($"{label} could not be decompressed: it is not valid base64.");
                }
            }
        }

        return new DrawioFile(pages, page - 1, cells);
    }

    /// <summary>The cells of a page's XML text: the inflated body of a compressed page.</summary>
    private static IReadOnlyList<DrawioCell> ReadModel(string xml, string label)
    {
        if (xml.Length > DrawioLimits.MaxXmlCharacters)
        {
            throw Reject($"{label} is larger than {DrawioLimits.MaxXmlCharacters.ToString("N0", CultureInfo.InvariantCulture)} characters once decompressed, which is more than Draughtsman reads.");
        }

        try
        {
            using XmlReader reader = XmlReader.Create(new StringReader(xml), CreateSettings());
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "mxGraphModel")
            {
                throw Reject($"{label} is not a draw.io diagram: once decompressed it does not start with <mxGraphModel>.");
            }

            return ReadCells(ReadElement(reader, new ReadState(), 1));
        }
        catch (XmlException ex)
        {
            throw Reject(Describe(ex, $"{label} once decompressed"), ex);
        }
    }

    private static XmlReaderSettings CreateSettings()
    {
        return new XmlReaderSettings
        {
            // The whole defence against XML's own attacks: a DOCTYPE is refused, so there is no entity to expand, no external entity or DTD
            // to fetch, and nothing for a resolver to resolve (and none is set).
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = DrawioLimits.MaxXmlCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false,
        };
    }

    private static string Describe(XmlException ex, string subject)
    {
        // The framework words a refused DOCTYPE as "DTD is prohibited": say it in the file's own terms.
        return ex.Message.Contains("DTD", StringComparison.Ordinal)
            ? $"{subject} contains a DOCTYPE declaration. draw.io never writes one, and Draughtsman refuses it: it is how an XML file smuggles in entity bombs and reads of other files."
            : $"{subject} is not well-formed XML: {ex.Message}";
    }

    /// <summary>
    /// Inflates a compressed page: base64, then a deflate stream (draw.io's own is headerless; some tools add a zlib header), then
    /// percent-decoding (draw.io encodes the XML before compressing it; a stream that holds XML directly is taken as it is).
    /// </summary>
    private static string Inflate(string payload, string label)
    {
        byte[] data = Convert.FromBase64String(payload);
        bool zlib = data.Length > 2 && (data[0] & 0x0F) == 8 && (((data[0] << 8) | data[1]) % 31) == 0;
        byte[]? inflated = TryInflate(data, zlib, label);
        if (inflated is null)
        {
            inflated = TryInflate(data, !zlib, label) ?? throw Reject($"{label} could not be decompressed: it is not a deflate stream.");
        }

        string text = Encoding.UTF8.GetString(inflated);
        string trimmed = text.TrimStart();
        return trimmed.StartsWith('<') ? trimmed : Uri.UnescapeDataString(text).TrimStart('﻿');
    }

    private static byte[]? TryInflate(byte[] data, bool zlib, string label)
    {
        using var input = new MemoryStream(data, writable: false);
        using Stream inflater = zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        try
        {
            int read;
            while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > DrawioLimits.MaxInflatedBytes)
                {
                    // Stopped here, not after the stream ends: a deflate stream can expand a thousandfold.
                    throw Reject($"{label} expands to more than {(DrawioLimits.MaxInflatedBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MiB when decompressed, which is more than Draughtsman reads. Export fewer pages, or split the diagram.");
                }

                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return output.ToArray();
    }

    /// <summary>
    /// Reads the element the reader is on, and everything inside it, into a small tree, leaving the reader on the node after it (as
    /// <see cref="XmlReader.Skip"/> does). Depth and element count are checked before anything is stored, so recursion stays within
    /// <see cref="DrawioLimits.MaxDepth"/> frames and memory within <see cref="DrawioLimits.MaxElements"/> nodes.
    /// </summary>
    private static Node ReadElement(XmlReader reader, ReadState state, int depth)
    {
        if (depth > DrawioLimits.MaxDepth)
        {
            throw Reject($"The draw.io file is nested more than {DrawioLimits.MaxDepth.ToString(CultureInfo.InvariantCulture)} levels deep, which no draw.io file is.");
        }

        if (++state.Elements > DrawioLimits.MaxElements)
        {
            throw Reject($"The draw.io file has more than {DrawioLimits.MaxElements.ToString("N0", CultureInfo.InvariantCulture)} elements, which is more than Draughtsman reads.");
        }

        if (reader.AttributeCount > DrawioLimits.MaxAttributesPerElement)
        {
            throw Reject($"An element in the draw.io file has more than {DrawioLimits.MaxAttributesPerElement.ToString("N0", CultureInfo.InvariantCulture)} attributes, which is more than Draughtsman reads.");
        }

        var node = new Node(reader.LocalName);
        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                node.Attributes[reader.Name] = reader.Value;
            }

            reader.MoveToElement();
        }

        bool empty = reader.IsEmptyElement;
        reader.Read();
        if (empty)
        {
            return node;
        }

        while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    node.Children.Add(ReadElement(reader, state, depth + 1));
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace:
                    (node.Text ??= new StringBuilder()).Append(reader.Value);
                    reader.Read();
                    break;
                default:
                    reader.Read();
                    break;
            }
        }

        reader.Read();
        return node;
    }

    private static IReadOnlyList<DrawioCell> ReadCells(Node model)
    {
        Node? root = model.Children.Find(c => c.Name == "root");
        var cells = new List<DrawioCell>();
        if (root is null)
        {
            return cells;
        }

        int drawn = 0;
        int unnamed = 0;
        foreach (Node element in root.Children)
        {
            DrawioCell? cell = ReadCell(element, ref unnamed);
            if (cell is null)
            {
                continue;
            }

            if ((cell.Vertex || cell.Edge) && ++drawn > DrawioLimits.MaxCells)
            {
                throw Reject($"The draw.io page has more than {DrawioLimits.MaxCells.ToString("N0", CultureInfo.InvariantCulture)} shapes and connectors, which is more than Draughtsman reads. Split it into pages and import them one at a time.");
            }

            cells.Add(cell);
        }

        return cells;
    }

    private static DrawioCell? ReadCell(Node element, ref int unnamed)
    {
        bool wrapped = Array.Exists(s_wrapperNames, n => string.Equals(n, element.Name, StringComparison.OrdinalIgnoreCase));
        if (!wrapped && element.Name != "mxCell")
        {
            return null;
        }

        Node? cell = wrapped ? element.Children.Find(c => c.Name == "mxCell") : element;
        if (cell is null)
        {
            return null;
        }

        string id = element.Attribute("id") ?? cell.Attribute("id") ?? $"cell-{(++unnamed).ToString(CultureInfo.InvariantCulture)}";
        string value = (wrapped ? element.Attribute("label") : null) ?? cell.Attribute("value") ?? string.Empty;
        IReadOnlyDictionary<string, string> attributes = wrapped
            ? element.Attributes.Where(a => a.Key is not ("id" or "label")).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal)
            : new Dictionary<string, string>();

        return new DrawioCell
        {
            Id = id,
            Parent = cell.Attribute("parent"),
            Source = cell.Attribute("source"),
            Target = cell.Attribute("target"),
            Style = cell.Attribute("style"),
            Value = value,
            Vertex = IsOn(cell.Attribute("vertex")),
            Edge = IsOn(cell.Attribute("edge")),
            Visible = cell.Attribute("visible") != "0",
            Geometry = ReadGeometry(cell),
            Attributes = attributes,
        };
    }

    private static DrawioGeometry? ReadGeometry(Node cell)
    {
        Node? geometry = cell.Children.Find(c => c.Name == "mxGeometry" && c.Attribute("as") == "geometry")
            ?? cell.Children.Find(c => c.Name == "mxGeometry" && c.Attribute("as") is null);
        if (geometry is null)
        {
            return null;
        }

        var points = new List<DrawioPoint>();
        Node? array = geometry.Children.Find(c => c.Name == "Array" && c.Attribute("as") == "points");
        if (array is not null)
        {
            foreach (Node point in array.Children.Where(c => c.Name == "mxPoint"))
            {
                points.Add(new DrawioPoint(Number(point, "x"), Number(point, "y")));
            }
        }

        return new DrawioGeometry(Number(geometry, "x"), Number(geometry, "y"), Number(geometry, "width"), Number(geometry, "height"), IsOn(geometry.Attribute("relative")), points);
    }

    private static double Number(Node node, string name)
    {
        return double.TryParse(node.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : 0;
    }

    private static bool IsOn(string? value)
    {
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string text, int length)
    {
        return text.Length <= length ? text : text[..length];
    }

    private static SourceRejectedException Reject(string message, Exception? inner = null)
    {
        return new SourceRejectedException(message, inner);
    }

    private sealed class ReadState
    {
        public int Elements { get; set; }
    }

    private sealed class Node(string name)
    {
        public string Name { get; } = name;

        public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

        public List<Node> Children { get; } = [];

        public StringBuilder? Text { get; set; }

        public string? Attribute(string key)
        {
            return Attributes.GetValueOrDefault(key);
        }
    }
}
