using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Import.Drawio;

/// <summary>
/// What a draw.io shape became. <see cref="Guessed"/> is true only where the kind is a judgement (an ellipse might be a start, an end, a state or a
/// system), so the author is asked to confirm it; a plain box, a diamond and a cylinder have conventional meanings and are not guesses.
/// <see cref="Unsupported"/> names what the importer could not draw (a stencil, a picture, a triangle): the shape is then a plain box.
/// </summary>
/// <param name="Description">How the shape reads in a warning ("an ellipse").</param>
internal sealed record DrawioShape(string Kind, string Shape, bool Guessed, string Description, string? Unsupported = null);

/// <summary>
/// Maps a draw.io style to a kind and a schema shape for one document type, the way <see cref="MermaidKindMapper"/> maps a Mermaid shape. The
/// table is the shapes draw.io's flowchart, basic and UML palettes share with the ISO 5807 set and the basic shape library (<c>custom:basic/*</c>);
/// everything else degrades to a plain box and is named in <see cref="DrawioShape.Unsupported"/>. Kind names must exist in <c>src/Draughtsman.Core/Schemas/&lt;type&gt;.json</c>; the tests check that.
/// </summary>
internal static class DrawioShapeMapper
{
    public const string ContainerKind = "container";

    /// <summary>The kind every shape without a better one gets in a freeform document, which has no vocabulary: what the editor's Shapes group places.</summary>
    public const string FreeformKind = "shape";

    private const string TextKind = "text";

    private sealed record Rule(string Shape, string FlowKind, string ArchKind, bool FlowGuessed, bool ArchGuessed, string Description);

    private static readonly Dictionary<string, Rule> s_rules = BuildRules();

    /// <summary>
    /// The direction (draw.io's <c>direction=</c>) at which each shape that has one is drawn the way its basic-library twin is: draw.io draws an arrow, a step,
    /// a callout, a trapezoid, a folder and a cube facing east (the default) and a triangle pointing east, where the library's triangle points north. A shape
    /// turned any other way is not that shape here, since the box cannot be turned with it, so it stays a named plain box.
    /// </summary>
    private static readonly Dictionary<string, string> s_drawnFacing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["triangle"] = "north",
        ["step"] = "east",
        ["trapezoid"] = "east",
        ["folder"] = "east",
        ["cube"] = "east",
        ["callout"] = "east",
        ["singleArrow"] = "east",
        ["doubleArrow"] = "east",
    };

    /// <summary>Base styles and shapes draw.io has that Draughtsman has no shape for, named in the warning rather than drawn.</summary>
    private static readonly HashSet<string> s_unsupportedBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "triangle", "line", "image", "step", "trapezoid", "folder", "cube", "callout", "tape", "star", "table", "tableRow",
        "partialRectangle", "umlLifeline", "umlFrame", "corner", "waypoint", "curlyBracket", "wave", "singleArrow",
        "doubleArrow", "flexArrow", "xor", "dataStorage", "lines", "isoRectangle", "isoCube", "cylinder4", "mxgraph.basic.cloud",
    };

    private static Dictionary<string, Rule> BuildRules()
    {
        var rules = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);

        void Add(Rule rule, params string[] names)
        {
            foreach (string name in names)
            {
                rules[name] = rule;
            }
        }

        Add(new Rule("pill", "terminal", "service", true, true, "an ellipse"), "ellipse", "doubleEllipse");
        Add(new Rule("pill", "terminal", "service", false, true, "a terminator"), "mxgraph.flowchart.terminator", "mxgraph.flowchart.start_1", "mxgraph.flowchart.start_2");
        Add(new Rule("diamond", "decision", "service", false, true, "a diamond"), "rhombus", "diamond", "mxgraph.flowchart.decision");
        Add(new Rule("cylinder", "stored-data", "database", false, false, "a cylinder"), "cylinder", "cylinder2", "cylinder3", "datastore", "mxgraph.flowchart.database");
        Add(new Rule("parallelogram", "io", "queue", false, true, "a parallelogram"), "parallelogram", "mxgraph.flowchart.data");
        Add(new Rule("hexagon", "process", "queue", true, true, "a hexagon"), "hexagon", "mxgraph.flowchart.preparation");
        Add(new Rule("custom:generic/user", "process", "actor", true, false, "a stick figure (an actor)"), "actor", "umlActor");
        Add(new Rule("custom:generic/cloud", "process", "external", true, true, "a cloud"), "cloud");
        Add(new Rule("document", "io", "service", false, false, "a document"), "document", "mxgraph.flowchart.document");
        Add(new Rule("predefined-process", "subprocess", "service", false, false, "a predefined process"), "process", "mxgraph.flowchart.predefined_process");
        Add(new Rule("manual-input", "io", "service", false, false, "a manual input"), "manualInput", "mxgraph.flowchart.manual_input");
        Add(new Rule("off-page-connector", "process", "service", false, false, "an off-page connector"), "offPageConnector", "mxgraph.flowchart.off-page_reference");
        Add(new Rule("on-page-connector", "process", "service", false, false, "an on-page connector"), "mxgraph.flowchart.on-page_reference");
        Add(new Rule("delay", "delay", "service", false, false, "a delay"), "delay", "mxgraph.flowchart.delay");
        Add(new Rule("display", "display", "service", false, false, "a display"), "display", "mxgraph.flowchart.display");
        Add(new Rule("manual-operation", "manual-operation", "service", false, false, "a manual operation"), "mxgraph.flowchart.manual_operation");
        Add(new Rule("stored-data", "stored-data", "service", false, false, "stored data"), "mxgraph.flowchart.stored_data");
        Add(new Rule("internal-storage", "process", "service", false, false, "internal storage"), "internalStorage", "mxgraph.flowchart.internal_storage");
        Add(new Rule("card", "process", "service", false, false, "a card"), "card", "mxgraph.flowchart.card");
        Add(new Rule("multi-document", "process", "service", false, false, "multiple documents"), "mxgraph.flowchart.multi-document");
        Add(new Rule("or", "process", "service", false, false, "an 'or' junction"), "or", "mxgraph.flowchart.or");
        Add(new Rule("summing-junction", "process", "service", false, false, "a summing junction"), "sumEllipse", "mxgraph.flowchart.summing_function");
        Add(new Rule("collate", "process", "service", false, false, "a collate symbol"), "collate", "mxgraph.flowchart.collate");
        Add(new Rule("sort", "process", "service", false, false, "a sort symbol"), "sortShape", "mxgraph.flowchart.sort");
        Add(new Rule("rectangle", "process", "service", false, false, "a rectangle"), "mxgraph.flowchart.process");
        Add(new Rule("custom:basic/note", "note", "note", false, false, "a note"), "note");
        Add(new Rule("custom:basic/triangle", "process", "service", false, false, "a triangle"), "triangle");
        Add(new Rule("custom:basic/trapezoid", "process", "service", false, false, "a trapezoid"), "trapezoid");
        Add(new Rule("custom:basic/chevron", "process", "service", false, false, "a chevron"), "step");
        Add(new Rule("custom:basic/cross", "process", "service", false, false, "a cross"), "plus", "cross");
        Add(new Rule("custom:basic/arrow", "process", "service", false, false, "an arrow"), "singleArrow");
        Add(new Rule("custom:basic/double-arrow", "process", "service", false, false, "a double arrow"), "doubleArrow");
        Add(new Rule("custom:basic/callout", "note", "note", false, false, "a callout"), "callout");
        Add(new Rule("custom:basic/cube", "process", "service", false, false, "a cube"), "cube");
        Add(new Rule("custom:basic/folder", "process", "service", false, false, "a folder"), "folder");
        Add(new Rule("custom:basic/component", "process", "service", false, false, "a component"), "component", "module");
        return rules;
    }

    /// <summary>The kind and shape for a vertex of the given style in a document of <paramref name="type"/>.</summary>
    /// <param name="holdsChildren">Whether other shapes sit inside this one: a shape that holds shapes can only be a container.</param>
    public static DrawioShape Map(DocumentType type, DrawioStyle style, bool holdsChildren)
    {
        string? shapeKey = style.Get("shape");
        string? name = string.IsNullOrWhiteSpace(shapeKey) ? style.Name : shapeKey.Trim();

        bool containerStyle = style.HasToken("swimlane") || style.HasToken("group") || style.Flag("container") || IsOneOf(name, "swimlane", "group", "pool");
        if (containerStyle || holdsChildren)
        {
            // A folder that holds shapes is a UML package: it stays a folder, now a container, instead of becoming a plain box.
            string containerShape = holdsChildren && IsOneOf(name, "folder") && FacesAsDrawn(name!, style) ? "custom:basic/folder" : "rectangle";
            return new DrawioShape(ContainerKind, containerShape, Guessed: !containerStyle, "a container that holds other shapes");
        }

        if (IsOneOf(name, "text"))
        {
            return new DrawioShape(TextKind, "text", Guessed: false, "plain text");
        }

        string plain = style.Flag("rounded") ? "rounded" : "rectangle";
        if (name is null || IsOneOf(name, "rectangle", "rect", "label"))
        {
            return Plain(type, plain);
        }

        if (s_rules.TryGetValue(name, out Rule? rule) && FacesAsDrawn(name, style))
        {
            return type switch
            {
                DocumentType.Flowchart => new DrawioShape(rule.FlowKind, rule.Shape == "rectangle" && rule.FlowKind == "process" ? plain : rule.Shape, rule.FlowGuessed, rule.Description),
                DocumentType.Architecture => new DrawioShape(rule.ArchKind, rule.Shape == "rectangle" && rule.ArchKind == "service" ? plain : rule.Shape, rule.ArchGuessed, rule.Description),
                _ => new DrawioShape(FreeformKind, rule.Shape, Guessed: false, rule.Description),
            };
        }

        // A name draw.io (or a stencil library) has that no shape here stands for: a plain box, and the author is told which shape it was.
        // A bare word that is not a shape at all is a named style from the file's own stylesheet ("myStyle;fillColor=red"), which is nothing to warn about.
        bool stencil = shapeKey is not null && !string.IsNullOrWhiteSpace(shapeKey);
        if (stencil || s_unsupportedBases.Contains(name))
        {
            DrawioShape box = Plain(type, plain);
            return box with { Unsupported = name, Description = IsOneOf(name, "image") ? "a picture" : box.Description };
        }

        return Plain(type, plain);
    }

    /// <summary>True when <paramref name="name"/> is not a shape draw.io turns with <c>direction=</c>, or is drawn facing the way its twin here is (<see cref="s_drawnFacing"/>).</summary>
    private static bool FacesAsDrawn(string name, DrawioStyle style)
    {
        return !s_drawnFacing.TryGetValue(name, out string? facing) || string.Equals(style.Get("direction") ?? "east", facing, StringComparison.OrdinalIgnoreCase);
    }

    private static DrawioShape Plain(DocumentType type, string shape)
    {
        return type switch
        {
            DocumentType.Flowchart => new DrawioShape("process", shape, Guessed: false, "a plain box"),
            DocumentType.Architecture => new DrawioShape("service", shape, Guessed: false, "a plain box"),
            _ => new DrawioShape(FreeformKind, shape, Guessed: false, "a plain box"),
        };
    }

    private static bool IsOneOf(string? name, params string[] candidates)
    {
        return name is not null && Array.Exists(candidates, c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
    }
}
