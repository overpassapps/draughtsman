namespace Draughtsman.Ai.Prompting;

/// <summary>The three documents "Diagram to document" can produce (design/atlas/ai-panel.md: Design doc, Runbook, Plain explanation).</summary>
public enum DiagramOutputKind
{
    DesignDoc,
    Runbook,
    Explanation,

    /// <summary>The request named no supported kind; the service refuses it rather than guessing.</summary>
    Unknown,
}

/// <summary>
/// Builds the prompt for "Diagram to document" (build-spec-v0.1.md section 9, MVP AI feature 2): a diagram's outline
/// becomes prose that cites the diagram's elements by id. The outline is <see cref="Draughtsman.Mcp.Tools.Outline"/> -
/// read_document's compact form: ids, kinds, labels, parent and edge endpoints, and nothing else. No geometry, style
/// or image is ever put in this prompt (build-spec-v0.1.md section 9: only the outline form is sent).
/// </summary>
public static class DiagramToDocumentPrompt
{
    /// <summary>How a citation is written. The web panel turns a bracketed id that matches a node back into a click.</summary>
    public const string CitationRule = """
        Whenever you name or describe a specific element of the diagram, cite its id in square brackets immediately
        after it, exactly like "The checkout step [n_checkout] hands the order to the payment service [n_payment]".
        Name an element in plain words, by its label, and never write an id anywhere except inside those brackets:
        a reader sees the label in place of the bracketed id. Use only ids that appear in the outline below, and
        cite a node at least once per section where it is discussed. Do not cite an id for an element that has no
        line in the outline.
        """;

    /// <summary>The system prompt for a document of the given kind, with the diagram's outline embedded verbatim.</summary>
    public static string BuildSystemPrompt(DiagramOutputKind kind, string outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        return $"""
            You write clear documentation about a diagram. You are given only the diagram's outline: one line per
            node and edge, each carrying the element's id, its kind, its label, its container (if any) and, for an
            edge, the two nodes it connects. You cannot see the diagram's picture, and you must not invent any part
            of it - no component, step, connection or property that the outline does not state.

            {CitationRule}

            {KindInstruction(kind)}

            Reply with Markdown only, no code fences around the whole answer, and no preamble. Use headings, short
            paragraphs and lists as the document needs. This is a first draft a person will edit; be concise and
            concrete rather than exhaustive.

            The diagram's outline:
            {outline}
            """;
    }

    /// <summary>
    /// Parses the request's <c>outputKind</c>: the three names the panel offers, so the wire vocabulary and the
    /// prompt cannot drift apart. Anything else is <see cref="DiagramOutputKind.Unknown"/> and the service rejects it.
    /// </summary>
    public static bool TryParseKind(string? value, out DiagramOutputKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "design-doc":
                kind = DiagramOutputKind.DesignDoc;
                return true;
            case "runbook":
                kind = DiagramOutputKind.Runbook;
                return true;
            case "explanation":
                kind = DiagramOutputKind.Explanation;
                return true;
            default:
                kind = DiagramOutputKind.Unknown;
                return false;
        }
    }

    private static string KindInstruction(DiagramOutputKind kind)
    {
        return kind switch
        {
            DiagramOutputKind.DesignDoc => """
                Write a design document: a short purpose statement, the context the diagram sits in, each component
                and its responsibility, how data or control flows between them, the notable decisions and trade-offs
                the structure implies, and any open questions.
                """,
            DiagramOutputKind.Runbook => """
                Write an operational runbook: the prerequisites, then numbered step-by-step procedures, each step
                naming the component it acts on, followed by how to verify each step worked, how to roll back, and a
                short troubleshooting section keyed to the components that are most likely to fail.
                """,
            DiagramOutputKind.Explanation => """
                Write a plain-language explanation for someone who has never seen this diagram: what it is for, how
                the pieces fit together, and a walkthrough of its main flows from start to finish, avoiding jargon
                where a plain word will do.
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The output kind must be design-doc, runbook or explanation."),
        };
    }
}
