using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Import;

/// <summary>
/// Maps a Mermaid shape to a kind and a schema shape for one document type. <see cref="KindMapping.Guessed"/> is true
/// only when the choice is genuinely uncertain (a circle or hexagon could mean several things), so the UI asks the author
/// to confirm; a plain rectangle or rounded box is the type's ordinary box (a process, a service), not a guess, and a
/// shape with a conventional meaning (a diamond is a decision) is not one either.
/// Kind names must exist in <c>src/Draughtsman.Core/Schemas/&lt;type&gt;.json</c>; the tests check that.
/// </summary>
internal static class MermaidKindMapper
{
    public static KindMapping MapNode(DocumentType type, MermaidShape? shape)
    {
        return type switch
        {
            DocumentType.Flowchart => MapFlowchart(shape),
            DocumentType.Architecture => MapArchitecture(shape),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Mermaid import needs a document type with a kind vocabulary."),
        };
    }

    public static string ContainerKind => "container";

    private static KindMapping MapFlowchart(MermaidShape? shape)
    {
        return shape switch
        {
            null => new KindMapping("process", "rectangle", Guessed: false),
            MermaidShape.Rectangle => new KindMapping("process", "rectangle", Guessed: false),
            MermaidShape.Rounded => new KindMapping("process", "rounded", Guessed: false),
            MermaidShape.Stadium => new KindMapping("terminal", "pill", Guessed: false),
            MermaidShape.Subroutine => new KindMapping("subprocess", "rectangle", Guessed: false),
            // The flowchart vocabulary has no database kind; stored-data (data held in a store) is the nearest, and Mermaid's
            // cylinder is its database symbol, so the shape is kept and the kind is not a guess (QA-08: it used to be io).
            MermaidShape.Cylinder => new KindMapping("stored-data", "cylinder", Guessed: false),
            MermaidShape.Circle => new KindMapping("terminal", "pill", Guessed: true),
            MermaidShape.DoubleCircle => new KindMapping("terminal", "pill", Guessed: true),
            MermaidShape.Asymmetric => new KindMapping("note", "rectangle", Guessed: true),
            MermaidShape.Rhombus => new KindMapping("decision", "diamond", Guessed: false),
            MermaidShape.Hexagon => new KindMapping("process", "hexagon", Guessed: true),
            MermaidShape.Parallelogram => new KindMapping("io", "parallelogram", Guessed: false),
            _ => new KindMapping("process", "rectangle", Guessed: true),
        };
    }

    private static KindMapping MapArchitecture(MermaidShape? shape)
    {
        return shape switch
        {
            null => new KindMapping("service", "rectangle", Guessed: false),
            MermaidShape.Rectangle => new KindMapping("service", "rectangle", Guessed: false),
            MermaidShape.Rounded => new KindMapping("service", "rounded", Guessed: false),
            MermaidShape.Stadium => new KindMapping("actor", "pill", Guessed: true),
            MermaidShape.Subroutine => new KindMapping("external", "rectangle", Guessed: true),
            MermaidShape.Cylinder => new KindMapping("database", "cylinder", Guessed: false),
            MermaidShape.Circle => new KindMapping("actor", "pill", Guessed: true),
            MermaidShape.DoubleCircle => new KindMapping("actor", "pill", Guessed: true),
            MermaidShape.Asymmetric => new KindMapping("note", "rectangle", Guessed: true),
            MermaidShape.Rhombus => new KindMapping("service", "diamond", Guessed: true),
            MermaidShape.Hexagon => new KindMapping("queue", "hexagon", Guessed: true),
            MermaidShape.Parallelogram => new KindMapping("queue", "parallelogram", Guessed: true),
            _ => new KindMapping("service", "rectangle", Guessed: true),
        };
    }
}

internal sealed record KindMapping(string Kind, string Shape, bool Guessed);
