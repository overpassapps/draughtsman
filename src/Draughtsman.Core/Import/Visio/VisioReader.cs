using System.Globalization;
using System.Text;
using System.Xml;

namespace Draughtsman.Core.Import.Visio;

/// <summary>
/// Reads a .vsdx file into pages, shapes and the masters those shapes use. The file is untrusted, so the work is in <see cref="VisioPackage"/>
/// (the ZIP) and <see cref="VisioXml"/> (the XML); this class follows the package's relationships from the root to the document, its pages and
/// masters, and reads only what the chosen page needs: the pages' names and sizes, that one page's shapes and glue, and the masters those shapes
/// are instances of. The other pages are not read, and a part nothing points at is never opened. A refusal is a <see cref="SourceRejectedException"/>.
/// <para>
/// The reader is a stream over the XML that builds only <see cref="VisioShape"/> records of the few cells the importer uses (a page's XML is
/// dozens of cells per shape, and holding all of it as a tree would cost a hundred times what the shapes need). Visio's other sections (shape data,
/// controls, connection points, actions, fields, tabs, paragraphs) and every cell it has no use for are counted and skipped.
/// </para>
/// </summary>
internal static class VisioReader
{
    private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string DefaultDocumentPart = "visio/document.xml";

    /// <summary>The cells of a shape the importer reads. Everything else a shape sets is skipped as it is read.</summary>
    private static readonly HashSet<string> s_cells = new(StringComparer.Ordinal)
    {
        "PinX", "PinY", "Width", "Height", "LocPinX", "LocPinY", "Angle", "FlipX", "FlipY", "BeginX", "BeginY", "EndX", "EndY",
        "FillForegnd", "FillPattern", "LineColor", "LinePattern", "LineWeight", "Rounding", "BeginArrow", "EndArrow",
        "ShapeRouteStyle", "ConLineRouteExt", "LayerMember",
    };

    /// <summary>Visio's own 24 colours, which a cell may name by index; a document may add more from index 24 (its <c>Colors</c> table).</summary>
    private static readonly string[] s_standardColours =
    [
        "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF", "#800000", "#008000", "#000080", "#808000",
        "#800080", "#008080", "#C0C0C0", "#E6E6E6", "#CDCDCD", "#B3B3B3", "#9A9A9A", "#808080", "#666666", "#4D4D4D", "#333333", "#1A1A1A",
    ];

    /// <param name="file">The file's bytes.</param>
    /// <param name="page">The page to read, counted from 1.</param>
    /// <exception cref="ArgumentException"><paramref name="page"/> is not a page of the file.</exception>
    /// <exception cref="SourceRejectedException">The source is not a Visio file, or crosses a limit.</exception>
    public static VisioFile Read(byte[] file, int page)
    {
        if (page < 1)
        {
            throw new ArgumentException($"Pages are numbered from 1; {page.ToString(CultureInfo.InvariantCulture)} is not a page.");
        }

        var budget = new VisioBudget();
        using VisioPackage package = VisioPackage.Open(file, budget);
        string documentPart = FindDocumentPart(package);
        Dictionary<int, string> palette = ReadDocument(package, documentPart, budget);

        IReadOnlyList<VisioRelationship> documentRelationships = package.Relationships(documentPart);
        string? pagesPart = PartOf(package, documentPart, documentRelationships, "/pages");
        string? mastersPart = PartOf(package, documentPart, documentRelationships, "/masters");
        List<VisioPageInfo> pages = pagesPart is null ? [] : ReadPages(package, pagesPart, budget);

        if (pages.Count == 0)
        {
            return page == 1
                ? new VisioFile([], -1, [], [], new Dictionary<string, VisioMaster>(), palette)
                : throw new ArgumentException($"The file has no pages, so page {page.ToString(CultureInfo.InvariantCulture)} does not exist.");
        }

        if (page > pages.Count)
        {
            string count = pages.Count == 1 ? "1 page" : $"{pages.Count.ToString(CultureInfo.InvariantCulture)} pages";
            throw new ArgumentException($"The file has {count}; page {page.ToString(CultureInfo.InvariantCulture)} does not exist (pages are numbered from 1).");
        }

        VisioPageInfo selected = pages[page - 1];
        var shapes = new List<VisioShape>();
        var connects = new List<VisioConnect>();
        if (selected.PartName is not null && package.Has(selected.PartName))
        {
            string label = $"Page {page.ToString(CultureInfo.InvariantCulture)}{(selected.Name.Length > 0 ? $" (\"{VisioPackage.Printable(selected.Name)}\")" : string.Empty)}";
            package.ReadXml(selected.PartName, label, reader => ReadPageContents(reader, budget, shapes, connects));
        }

        Dictionary<string, VisioMaster> masters = mastersPart is null ? [] : ReadMasters(package, mastersPart, UsedMasters(shapes), budget);
        return new VisioFile(pages, page - 1, shapes, connects, masters, palette);
    }

    // ---- the package's chain of relationships ------------------------------------------------------------------------------------------

    private static string FindDocumentPart(VisioPackage package)
    {
        string? part = PartOf(package, string.Empty, package.Relationships(string.Empty), "/document");
        if (part is null && package.Has(DefaultDocumentPart))
        {
            part = DefaultDocumentPart;
        }

        return part ?? throw VisioPackage.Reject("That is a ZIP package but not a Visio drawing: it has no Visio document part. A .docx, .xlsx or .pptx file sent by mistake looks like this.");
    }

    /// <summary>The part the first non-external relationship of <paramref name="typeSuffix"/> from <paramref name="source"/> names, if that part is in the package.</summary>
    private static string? PartOf(VisioPackage package, string source, IReadOnlyList<VisioRelationship> relationships, string typeSuffix)
    {
        foreach (VisioRelationship relationship in relationships)
        {
            if (!relationship.External && relationship.Type.EndsWith(typeSuffix, StringComparison.Ordinal))
            {
                string part = VisioPackage.Resolve(source, relationship.Target);
                return package.Has(part) ? part : null;
            }
        }

        return null;
    }

    /// <summary>Reads <c>document.xml</c>: confirms it is a Visio document and collects its colour table. Everything else in it (styles, fonts, settings) is skipped.</summary>
    private static Dictionary<int, string> ReadDocument(VisioPackage package, string part, VisioBudget budget)
    {
        var palette = new Dictionary<int, string>();
        for (int i = 0; i < s_standardColours.Length; i++)
        {
            palette[i] = s_standardColours[i];
        }

        package.ReadXml(part, "The document part", reader =>
        {
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "VisioDocument")
            {
                throw VisioPackage.Reject("That is a ZIP package but not a Visio drawing: its document part is not a VisioDocument.");
            }

            budget.Element(reader);
            VisioXml.Children(reader, budget, name =>
            {
                if (name != "Colors")
                {
                    VisioXml.Skip(reader, budget);
                    return;
                }

                int entries = 0;
                VisioXml.Children(reader, budget, colour =>
                {
                    if (colour == "ColorEntry" && ++entries <= VisioLimits.MaxColours
                        && int.TryParse(reader.GetAttribute("IX"), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && reader.GetAttribute("RGB") is { } rgb)
                    {
                        palette[index] = rgb;
                    }

                    VisioXml.Skip(reader, budget);
                });
            });
        });
        return palette;
    }

    // ---- pages -----------------------------------------------------------------------------------------------------------------------

    private static List<VisioPageInfo> ReadPages(VisioPackage package, string pagesPart, VisioBudget budget)
    {
        Dictionary<string, VisioRelationship> relationships = package.Relationships(pagesPart).Where(r => !r.External).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var pages = new List<VisioPageInfo>();
        package.ReadXml(pagesPart, "The list of pages", reader =>
        {
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "Pages")
            {
                throw VisioPackage.Reject("The page list in the Visio file is not a Pages part.");
            }

            budget.Element(reader);
            VisioXml.Children(reader, budget, name =>
            {
                if (name != "Page")
                {
                    VisioXml.Skip(reader, budget);
                    return;
                }

                if (pages.Count >= VisioLimits.MaxPages)
                {
                    throw VisioPackage.Reject($"The Visio file has more than {VisioLimits.MaxPages.ToString("N0", CultureInfo.InvariantCulture)} pages, which is more than Draughtsman reads.");
                }

                pages.Add(ReadPage(reader, budget, relationships, pagesPart));
            });
        });
        return pages;
    }

    private static VisioPageInfo ReadPage(XmlReader reader, VisioBudget budget, Dictionary<string, VisioRelationship> relationships, string pagesPart)
    {
        string id = reader.GetAttribute("ID") ?? string.Empty;
        string nameU = Truncate(reader.GetAttribute("NameU") ?? string.Empty, VisioLimits.MaxNameLength);
        string name = Truncate(reader.GetAttribute("Name") ?? nameU, VisioLimits.MaxNameLength);
        bool background = VisioXml.IsOn(reader.GetAttribute("Background"));
        string? backPage = reader.GetAttribute("BackPage");
        double? width = null;
        double? height = null;
        var layers = new List<VisioLayer>();
        string? relationshipId = null;

        VisioXml.Children(reader, budget, child =>
        {
            switch (child)
            {
                case "PageSheet":
                    VisioXml.Children(reader, budget, sheet =>
                    {
                        if (sheet == "Cell")
                        {
                            switch (reader.GetAttribute("N"))
                            {
                                case "PageWidth":
                                    width = VisioXml.Number(reader.GetAttribute("V"));
                                    break;
                                case "PageHeight":
                                    height = VisioXml.Number(reader.GetAttribute("V"));
                                    break;
                            }
                        }
                        else if (sheet == "Section" && reader.GetAttribute("N") == "Layer")
                        {
                            ReadLayers(reader, budget, layers);
                            return;
                        }

                        VisioXml.Skip(reader, budget);
                    });
                    break;
                case "Rel":
                    relationshipId = reader.GetAttribute("id", RelationshipsNamespace);
                    VisioXml.Skip(reader, budget);
                    break;
                default:
                    VisioXml.Skip(reader, budget);
                    break;
            }
        });

        string? part = relationshipId is not null && relationships.TryGetValue(relationshipId, out VisioRelationship? relationship)
            ? VisioPackage.Resolve(pagesPart, relationship.Target)
            : null;
        return new VisioPageInfo(id, name, nameU, background, backPage, width, height, part, layers);
    }

    private static void ReadLayers(XmlReader reader, VisioBudget budget, List<VisioLayer> layers)
    {
        VisioXml.Children(reader, budget, row =>
        {
            if (row != "Row" || layers.Count >= VisioLimits.MaxLayers)
            {
                VisioXml.Skip(reader, budget);
                return;
            }

            int index = int.TryParse(reader.GetAttribute("IX"), NumberStyles.None, CultureInfo.InvariantCulture, out int ix) ? ix : layers.Count;
            string name = string.Empty;
            bool visible = true;
            VisioXml.Children(reader, budget, cell =>
            {
                if (cell == "Cell")
                {
                    switch (reader.GetAttribute("N"))
                    {
                        case "Name":
                            name = Truncate(reader.GetAttribute("V") ?? string.Empty, VisioLimits.MaxNameLength);
                            break;
                        case "Visible":
                            visible = VisioXml.IsOn(reader.GetAttribute("V"));
                            break;
                    }
                }

                VisioXml.Skip(reader, budget);
            });
            layers.Add(new VisioLayer(index, name, visible));
        });
    }

    // ---- page contents: shapes and glue ------------------------------------------------------------------------------------------

    private static void ReadPageContents(XmlReader reader, VisioBudget budget, List<VisioShape> shapes, List<VisioConnect> connects)
    {
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "PageContents")
        {
            throw VisioPackage.Reject("The page in the Visio file is not a PageContents part.");
        }

        budget.Element(reader);
        var count = new ShapeCount(VisioLimits.MaxShapes, "The page");
        VisioXml.Children(reader, budget, name =>
        {
            switch (name)
            {
                case "Shapes":
                    ReadShapes(reader, budget, count, shapes);
                    break;
                case "Connects":
                    VisioXml.Children(reader, budget, connect =>
                    {
                        if (connect == "Connect")
                        {
                            if (connects.Count >= VisioLimits.MaxConnects)
                            {
                                throw VisioPackage.Reject($"The page has more than {VisioLimits.MaxConnects.ToString("N0", CultureInfo.InvariantCulture)} connections, which is more than Draughtsman reads.");
                            }

                            connects.Add(new VisioConnect(
                                reader.GetAttribute("FromSheet") ?? string.Empty,
                                reader.GetAttribute("FromCell") ?? string.Empty,
                                reader.GetAttribute("ToSheet") ?? string.Empty,
                                reader.GetAttribute("ToCell") ?? string.Empty));
                        }

                        VisioXml.Skip(reader, budget);
                    });
                    break;
                default:
                    VisioXml.Skip(reader, budget);
                    break;
            }
        });
    }

    /// <summary>Reads the <c>Shapes</c> element the reader is on: each <c>Shape</c> in it, recursively. Shapes the file marks deleted are left out.</summary>
    private static void ReadShapes(XmlReader reader, VisioBudget budget, ShapeCount count, List<VisioShape> into)
    {
        VisioXml.Children(reader, budget, name =>
        {
            if (name != "Shape")
            {
                VisioXml.Skip(reader, budget);
                return;
            }

            if (++count.Count > count.Limit)
            {
                throw VisioPackage.Reject($"{count.What} has more than {count.Limit.ToString("N0", CultureInfo.InvariantCulture)} shapes, which is more than Draughtsman reads. Split the drawing into pages and import them one at a time.");
            }

            VisioShape shape = ReadShape(reader, budget, count);
            if (!shape.Deleted)
            {
                into.Add(shape);
            }
        });
    }

    private static VisioShape ReadShape(XmlReader reader, VisioBudget budget, ShapeCount count)
    {
        var shape = new VisioShape
        {
            Id = reader.GetAttribute("ID") ?? string.Empty,
            NameU = Truncate(reader.GetAttribute("NameU") ?? string.Empty, VisioLimits.MaxNameLength),
            Type = reader.GetAttribute("Type") ?? "Shape",
            Master = reader.GetAttribute("Master"),
            MasterShape = reader.GetAttribute("MasterShape"),
            Deleted = VisioXml.IsOn(reader.GetAttribute("Del")),
        };
        if (shape.Type == "Foreign")
        {
            shape.IsForeign = true;
        }

        VisioXml.Children(reader, budget, name =>
        {
            switch (name)
            {
                case "Cell":
                    ReadCell(reader, shape);
                    VisioXml.Skip(reader, budget);
                    break;
                case "Section":
                    ReadSection(reader, budget, shape);
                    break;
                case "Text":
                    shape.Text = ReadText(reader, budget);
                    break;
                case "Shapes":
                    ReadShapes(reader, budget, count, shape.Children);
                    break;
                case "ForeignData":
                    shape.IsForeign = true;
                    VisioXml.Skip(reader, budget);
                    break;
                default:
                    VisioXml.Skip(reader, budget);
                    break;
            }
        });
        return shape;
    }

    /// <summary>A <c>Cell</c> element's value, kept when the importer reads that cell. The reader stays on the element.</summary>
    private static void ReadCell(XmlReader reader, VisioShape shape)
    {
        string? name = reader.GetAttribute("N");
        string? value = reader.GetAttribute("V");
        if (name is null || value is null || !s_cells.Contains(name))
        {
            return;
        }

        shape.Cells[name] = Truncate(value, 64);
        if (reader.GetAttribute("F") is { } formula && formula.Contains("THEMEVAL", StringComparison.Ordinal))
        {
            shape.ThemedCells.Add(name);
        }
    }

    private static void ReadSection(XmlReader reader, VisioBudget budget, VisioShape shape)
    {
        switch (reader.GetAttribute("N"))
        {
            case "Geometry":
                ReadGeometry(reader, budget, shape);
                break;
            case "Character":
                ReadCharacter(reader, budget, shape);
                break;
            case "User":
                VisioXml.Children(reader, budget, row =>
                {
                    if (row == "Row" && reader.GetAttribute("N") == "msvStructureType")
                    {
                        VisioXml.Children(reader, budget, cell =>
                        {
                            if (cell == "Cell" && reader.GetAttribute("N") == "Value")
                            {
                                shape.StructureType = Truncate(reader.GetAttribute("V") ?? string.Empty, 40);
                            }

                            VisioXml.Skip(reader, budget);
                        });
                    }
                    else
                    {
                        VisioXml.Skip(reader, budget);
                    }
                });
                break;
            case "Property":
                VisioXml.Children(reader, budget, row =>
                {
                    shape.HasShapeData |= row == "Row";
                    VisioXml.Skip(reader, budget);
                });
                break;
            default:
                VisioXml.Skip(reader, budget);
                break;
        }
    }

    private static void ReadGeometry(XmlReader reader, VisioBudget budget, VisioShape shape)
    {
        var section = new VisioGeometrySection();
        VisioXml.Children(reader, budget, child =>
        {
            if (child == "Cell")
            {
                bool on = VisioXml.IsOn(reader.GetAttribute("V"));
                switch (reader.GetAttribute("N"))
                {
                    case "NoFill":
                        section.NoFill = on;
                        break;
                    case "NoLine":
                        section.NoLine = on;
                        break;
                    case "NoShow":
                        section.NoShow = on;
                        break;
                }

                VisioXml.Skip(reader, budget);
            }
            else if (child == "Row" && reader.GetAttribute("T") is { } type)
            {
                if (section.Rows.Count >= VisioLimits.MaxGeometryRows)
                {
                    section.Truncated = true;
                    VisioXml.Skip(reader, budget);
                    return;
                }

                double x = double.NaN, y = double.NaN, a = double.NaN, b = double.NaN, c = double.NaN, d = double.NaN;
                VisioXml.Children(reader, budget, cell =>
                {
                    if (cell == "Cell" && VisioXml.Number(reader.GetAttribute("V")) is { } value)
                    {
                        switch (reader.GetAttribute("N"))
                        {
                            case "X":
                                x = value;
                                break;
                            case "Y":
                                y = value;
                                break;
                            case "A":
                                a = value;
                                break;
                            case "B":
                                b = value;
                                break;
                            case "C":
                                c = value;
                                break;
                            case "D":
                                d = value;
                                break;
                        }
                    }

                    VisioXml.Skip(reader, budget);
                });
                section.Rows.Add(new VisioGeometryRow(Truncate(type, 40), x, y, a, b, c, d));
            }
            else
            {
                VisioXml.Skip(reader, budget);
            }
        });

        if (shape.Geometry.Count >= VisioLimits.MaxGeometrySections)
        {
            shape.GeometryTruncated = true;
        }
        else
        {
            shape.Geometry.Add(section);
        }
    }

    /// <summary>The character formatting of a shape's first run (colour, size, style): the one the whole label is drawn in unless it has several.</summary>
    private static void ReadCharacter(XmlReader reader, VisioBudget budget, VisioShape shape)
    {
        bool first = true;
        VisioXml.Children(reader, budget, row =>
        {
            if (row != "Row" || !first)
            {
                VisioXml.Skip(reader, budget);
                return;
            }

            first = false;
            VisioXml.Children(reader, budget, cell =>
            {
                if (cell == "Cell" && reader.GetAttribute("V") is { } value)
                {
                    switch (reader.GetAttribute("N"))
                    {
                        case "Color":
                            shape.Cells["Char.Color"] = Truncate(value, 64);
                            break;
                        case "Size":
                            shape.Cells["Char.Size"] = Truncate(value, 64);
                            break;
                        case "Style":
                            shape.Cells["Char.Style"] = Truncate(value, 64);
                            break;
                    }
                }

                VisioXml.Skip(reader, budget);
            });
        });
    }

    /// <summary>
    /// The text of a <c>Text</c> element: its text nodes in order, with the run markers (<c>cp</c>, <c>pp</c>, <c>tp</c>) dropped and a field's
    /// cached text (<c>fld</c>) kept as the text it displays. Kept to about twice a label's limit; the rest is read and dropped.
    /// </summary>
    private static string? ReadText(XmlReader reader, VisioBudget budget)
    {
        int depth = reader.Depth;
        bool empty = reader.IsEmptyElement;
        reader.Read();
        if (empty)
        {
            return null;
        }

        var text = new StringBuilder();
        int room = VisioLimits.MaxLabelLength * 2;
        while (!reader.EOF && reader.Depth > depth)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    string value = reader.Value;
                    int take = Math.Min(room - text.Length, value.Length);
                    if (take > 0)
                    {
                        text.Append(value, 0, take);
                    }

                    break;
                case XmlNodeType.Element:
                    budget.Element(reader);
                    break;
            }

            reader.Read();
        }

        reader.Read();
        return text.ToString();
    }

    // ---- masters ---------------------------------------------------------------------------------------------------------------------

    private static HashSet<string> UsedMasters(IReadOnlyList<VisioShape> shapes)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<VisioShape>(shapes);
        while (pending.Count > 0)
        {
            VisioShape shape = pending.Pop();
            if (shape.Master is not null)
            {
                used.Add(shape.Master);
            }

            foreach (VisioShape child in shape.Children)
            {
                pending.Push(child);
            }
        }

        return used;
    }

    /// <summary>Reads <c>masters.xml</c> and, of the masters it lists, only those a shape on the page is an instance of.</summary>
    private static Dictionary<string, VisioMaster> ReadMasters(VisioPackage package, string mastersPart, HashSet<string> used, VisioBudget budget)
    {
        var masters = new Dictionary<string, VisioMaster>(StringComparer.Ordinal);
        if (used.Count == 0)
        {
            return masters;
        }

        Dictionary<string, VisioRelationship> relationships = package.Relationships(mastersPart).Where(r => !r.External).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var listed = new List<(VisioMaster Master, string? Part)>();
        package.ReadXml(mastersPart, "The list of masters", reader =>
        {
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "Masters")
            {
                throw VisioPackage.Reject("The master list in the Visio file is not a Masters part.");
            }

            budget.Element(reader);
            VisioXml.Children(reader, budget, name =>
            {
                if (name != "Master")
                {
                    VisioXml.Skip(reader, budget);
                    return;
                }

                if (listed.Count >= VisioLimits.MaxMasters)
                {
                    throw VisioPackage.Reject($"The Visio file lists more than {VisioLimits.MaxMasters.ToString("N0", CultureInfo.InvariantCulture)} masters, which is more than Draughtsman reads.");
                }

                string id = reader.GetAttribute("ID") ?? string.Empty;
                string nameU = Truncate(reader.GetAttribute("NameU") ?? string.Empty, VisioLimits.MaxNameLength);
                var master = new VisioMaster(id, Truncate(reader.GetAttribute("Name") ?? nameU, VisioLimits.MaxNameLength), nameU);
                string? relationshipId = null;
                VisioXml.Children(reader, budget, child =>
                {
                    if (child == "Rel")
                    {
                        relationshipId = reader.GetAttribute("id", RelationshipsNamespace);
                    }

                    VisioXml.Skip(reader, budget);
                });
                string? part = relationshipId is not null && relationships.TryGetValue(relationshipId, out VisioRelationship? relationship)
                    ? VisioPackage.Resolve(mastersPart, relationship.Target)
                    : null;
                listed.Add((master, part));
            });
        });

        var count = new ShapeCount(VisioLimits.MaxMasterShapes, "The masters");
        foreach ((VisioMaster master, string? part) in listed)
        {
            if (!used.Contains(master.Id) || masters.ContainsKey(master.Id))
            {
                continue;
            }

            masters[master.Id] = master;
            if (part is null || !package.Has(part))
            {
                continue;
            }

            var shapes = new List<VisioShape>();
            package.ReadXml(part, $"The master \"{VisioPackage.Printable(master.Name)}\"", reader =>
            {
                if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "MasterContents")
                {
                    throw VisioPackage.Reject($"A master in the Visio file is not a MasterContents part.");
                }

                budget.Element(reader);
                VisioXml.Children(reader, budget, name =>
                {
                    if (name == "Shapes")
                    {
                        ReadShapes(reader, budget, count, shapes);
                    }
                    else
                    {
                        VisioXml.Skip(reader, budget);
                    }
                });
            });

            master.Root = shapes.Count > 0 ? shapes[0] : null;
            var pending = new Stack<VisioShape>(shapes);
            while (pending.Count > 0)
            {
                VisioShape shape = pending.Pop();
                master.Shapes.TryAdd(shape.Id, shape);
                foreach (VisioShape child in shape.Children)
                {
                    pending.Push(child);
                }
            }
        }

        return masters;
    }

    private static string Truncate(string text, int length)
    {
        return text.Length <= length ? text : text[..length];
    }

    private sealed class ShapeCount(int limit, string what)
    {
        public int Count { get; set; }

        public int Limit { get; } = limit;

        public string What { get; } = what;
    }
}
