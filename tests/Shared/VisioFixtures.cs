using static Draughtsman.Tests.Shared.VisioDocBuilder;

namespace Draughtsman.Tests.Shared;

/// <summary>
/// The .vsdx files the Visio tests read, built here with System.IO.Compression instead of checked in as binaries (<see cref="VisioDocBuilder"/>
/// writes them the way Visio lays a package out). Every page is 10 by 8 inches; a shape is placed by its centre in inches from the page's
/// bottom-left, so a shape at (2, 7) of 1.5 by 0.75 lands on the canvas at x = 200 - 75, y = (8 - 7) * 100 - 37.5. Each fixture says what it is for;
/// Core, MCP and server tests all link this file, so they import the same bytes.
/// </summary>
internal static class VisioFixtures
{
    /// <summary>
    /// One page of single shapes, one for each outline the importer recognises and one it does not (ids 1 to 10): a plain box, a rounded box, an
    /// ellipse, a diamond, a text label, a triangle, a parallelogram, a hexagon, a styled box and a box turned 30 degrees.
    /// </summary>
    public static byte[] Shapes()
    {
        string shapes =
            Shape(1, 2, 7, 1.5, 0.75, "Plain box")
            + Shape(2, 4.5, 7, 1.5, 0.75, "Rounded", cells: Rounding(0.1))
            + Shape(3, 7, 7, 1.2, 0.8, "Oval", geometry: Ellipse)
            + Shape(4, 2, 5, 1.4, 1, "Is it?", geometry: Diamond)
            + Shape(5, 4.5, 5, 2, 0.4, "Just text", geometry: Invisible)
            + Shape(6, 7, 5, 1, 1, "Pointy", geometry: Triangle)
            + Shape(7, 2, 3, 1.5, 0.8, "Read input", geometry: Parallelogram)
            + Shape(8, 4.5, 3, 1.2, 0.8, "Prepare", geometry: Hexagon)
            + Shape(9, 7, 3, 1.5, 0.75, "Styled", cells: TextCell("FillForegnd", "#DAE8FC") + TextCell("LineColor", "#6C8EBF") + Cell("LineWeight", 0.02) + Cell("LinePattern", 2))
            + Shape(10, 2, 1, 1.5, 0.75, "Tilted", angle: 0.5235987755983);
        return new VisioDocBuilder().Page("Shapes", shapes).Build();
    }

    /// <summary>
    /// A group that is a container (id 1, "Backend", centred at (5, 4), 6 by 4, so its own origin is (2, 2)), holding two boxes and, in a nested group
    /// (id 4), a third: the children's positions are in their group's space, so the grandchild "DB" lands at page (3.75, 3).
    /// </summary>
    public static byte[] Group()
    {
        string database = Shape(5, 1.25, 0.6, 1.5, 0.8, "DB", geometry: Diamond);
        string inner = Shape(4, 3, 1, 5, 1.2, "Data tier", type: "Group", geometry: null, children: database);
        string children = Shape(2, 1.5, 2.8, 2, 1, "API") + Shape(3, 4.5, 2.8, 2, 1, "Worker") + inner;
        string group = Shape(1, 5, 4, 6, 4, "Backend", type: "Group", geometry: null, children: children);
        return new VisioDocBuilder().Page("Group", group).Build();
    }

    /// <summary>
    /// Three boxes A (id 1), B (id 2) and C (id 3) and four connectors: 4 is glued A to B, labelled "yes", with an arrow at its end (EndArrow 13); 5 is
    /// glued B to C, dashed, with a hollow triangle (EndArrow 14); 6 is glued to A at its start only (a loose end); 7 is glued to a shape (99) the page does not have.
    /// </summary>
    public static byte[] Connectors()
    {
        string shapes =
            Shape(1, 2, 6, 1.5, 0.75, "A")
            + Shape(2, 6, 6, 1.5, 0.75, "B")
            + Shape(3, 4, 3, 1.5, 0.75, "C")
            + Connector(4, 2.75, 6, 5.25, 6, "yes", cells: Cell("EndArrow", 13))
            + Connector(5, 6, 5.625, 4.75, 3, cells: Cell("EndArrow", 14) + Cell("LinePattern", 2))
            + Connector(6, 2, 5.625, 2, 4)
            + Connector(7, 6, 6.375, 9, 7, cells: Cell("EndArrow", 13));
        string glue = Glue(4, 1, 2) + Glue(5, 2, 3) + Glue(6, 1, null) + Glue(7, 2, 99);
        return new VisioDocBuilder().Page("Connectors", shapes, glue).Build();
    }

    /// <summary>Three pages: "Overview" (two boxes and a connector), "Details" (one box) and "Page-3" (nothing), with the sizes in the page sheets.</summary>
    public static byte[] MultiPage()
    {
        string overview = Shape(1, 2, 6, 1.5, 0.75, "Start") + Shape(2, 6, 6, 1.5, 0.75, "Finish") + Connector(3, 2.75, 6, 5.25, 6, cells: Cell("EndArrow", 13));
        return new VisioDocBuilder()
            .Page("Overview", overview, Glue(3, 1, 2))
            .Page("Details", Shape(1, 5, 4, 3, 1, "Only on page two"))
            .Page("Page-3", string.Empty)
            .Build();
    }

    /// <summary>
    /// A drawing with no masters at all, as a tool that writes Visio files without Visio's stencils (Lucidchart's export) does: every shape carries its own
    /// geometry, hex colours and explicit line ends, and nothing is named after a master. A box (id 1), a diamond (id 2) and an arrow between them (id 3, no
    /// ShapeRouteStyle, EndArrow 13).
    /// </summary>
    public static byte[] MasterLess()
    {
        string shapes =
            Shape(1, 2, 6, 1.5, 0.75, "Check stock", cells: TextCell("FillForegnd", "#FFF2CC") + TextCell("LineColor", "#D6B656") + Character("#333333", 0.1667, 1))
            + Shape(2, 6, 6, 1.5, 1, "In stock?", geometry: Diamond)
            + Connector(3, 2.75, 6, 5.25, 6, cells: Cell("BeginArrow", 0) + Cell("EndArrow", 13), attributes: string.Empty);
        return new VisioDocBuilder().Page("Page-1", shapes, Glue(3, 1, 2)).Build();
    }

    /// <summary>
    /// Instances of masters, which store only a position and their text: "Process" (id 3 on the page: a 1.5 by 0.75 box), "Decision" (id 4: a diamond), an
    /// unknown stencil master "Core Switch" that is a group of two shapes (id 5), and Visio's dynamic connector (master 2).
    /// </summary>
    public static byte[] Masters()
    {
        string connectorMaster = Shape(1, 1, 1, 1, 0, geometry: null, cells: Cell("BeginX", 0) + Cell("BeginY", 0) + Cell("EndX", 1) + Cell("EndY", 0), attributes: string.Empty);
        string processMaster = Shape(1, 1, 1, 1.5, 0.75, geometry: Rect, cells: TextCell("FillForegnd", "#FFFFFF"));
        string decisionMaster = Shape(1, 1, 1, 1.5, 1, geometry: Diamond);
        string switchMaster = Shape(1, 1, 1, 1, 1, type: "Group", geometry: null, children: Shape(2, 0.5, 0.5, 1, 1, geometry: Triangle) + Shape(3, 0.5, 0.5, 0.5, 0.5, geometry: Ellipse));

        string page =
            Instance(1, 3, 2, 6, "Start")
            + Instance(2, 4, 6, 6, "OK?")
            + Instance(3, 5, 4, 3, "Core switch", type: "Group", children: MasterChild(4, 2) + MasterChild(5, 3))
            + Connector(6, 2.75, 6, 5.25, 6, "go", attributes: "Master='2'")
            + Connector(7, 6, 5.5, 4.5, 3.5, attributes: "Master='2'");
        return new VisioDocBuilder()
            .Master(2, "Dynamic connector", connectorMaster)
            .Master(3, "Process", processMaster)
            .Master(4, "Decision", decisionMaster)
            .Master(5, "Core Switch", switchMaster)
            .Page("Masters", page, Glue(6, 1, 2) + Glue(7, 2, 4))
            .Build();
    }
}
