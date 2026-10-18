namespace Draughtsman.Core.Import;

/// <summary>
/// Something the importer could not map faithfully. Never fatal: the document is still produced and the UI
/// uses <see cref="Code"/> to decide how to flag the element (for example a review badge on a guessed kind).
/// </summary>
/// <param name="Code">Stable machine code from <see cref="ImportWarningCodes"/>.</param>
/// <param name="Message">Plain-English explanation for a person or an agent.</param>
/// <param name="Line">One-based line number in the source text, when the warning is tied to a line.</param>
/// <param name="ElementId">Id of the produced node or edge the warning concerns, when there is one.</param>
public sealed record ImportWarning(string Code, string Message, int? Line = null, string? ElementId = null);

/// <summary>Warning codes shared by every importer. Strings, not an enum, so new codes never break the wire contract.</summary>
public static class ImportWarningCodes
{
    /// <summary>The node's kind was inferred from a presentation hint and should be reviewed.</summary>
    public const string KindGuessed = "kind-guessed";

    /// <summary>A statement the importer understands but does not carry into the model (styles, clicks, class assignments).</summary>
    public const string UnsupportedSyntax = "unsupported-syntax";

    /// <summary>A line the importer could not parse at all; it was skipped.</summary>
    public const string ParseError = "parse-error";

    /// <summary>The source declares a diagram type this importer does not handle.</summary>
    public const string UnsupportedDiagram = "unsupported-diagram";

    /// <summary>No diagram header was found; a default was assumed.</summary>
    public const string MissingHeader = "missing-header";

    /// <summary>A source id was changed to satisfy the document id pattern or to avoid a collision.</summary>
    public const string IdRenamed = "id-renamed";

    /// <summary>An arrowhead or line style that the edge style model cannot express; the nearest was used.</summary>
    public const string StyleApproximated = "style-approximated";

    /// <summary>The source text produced no nodes at all.</summary>
    public const string EmptyDiagram = "empty-diagram";

    /// <summary>A foreign key (or ALTER TABLE) names a table that is not in the source; nothing was drawn for it.</summary>
    public const string MissingReference = "missing-reference";

    /// <summary>A comment declares a relationship the database does not enforce (<c>-- -&gt; Users.Id (no FK)</c>) that could not be drawn: it names a table or column that is not in the source.</summary>
    public const string ImpliedLinkSkipped = "implied-link-skipped";

    /// <summary>The source defines the same table more than once; the first definition was kept.</summary>
    public const string DuplicateTable = "duplicate-table";

    /// <summary>The source has several pages and only one was imported; the message says which, and how many were not.</summary>
    public const string PageSelected = "page-selected";

    /// <summary>A DSL lint warning (unknown kind, a reference to an undeclared node or port) surfaced through /api/import/dsl.</summary>
    public const string DslLint = "dsl-lint";
}
