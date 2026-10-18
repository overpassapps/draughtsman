using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Import.Visio;

/// <summary>Caller-supplied choices for a Visio import. Everything has a default so <c>new()</c> is a valid import.</summary>
public sealed record VisioImportOptions
{
    /// <summary>
    /// The document type a drawing is read into, which decides the kind each shape is given: <c>flowchart</c> (the default), <c>architecture</c>,
    /// or <c>freeform</c>, which has no kind vocabulary and so guesses none (every shape is a plain <c>shape</c>). A sequence document is made from
    /// a Mermaid <c>sequenceDiagram</c>, not from a drawing, and is rejected.
    /// </summary>
    public DocumentType Type { get; init; } = DocumentType.Flowchart;

    /// <summary>Title for the document. Null falls back to the page's name (unless it is Visio's own <c>Page-1</c>), then to "Imported diagram".</summary>
    public string? Title { get; init; }

    /// <summary>The page to import from a multi-page file, counted from 1. A file's other pages are not imported.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Clock for <c>meta.createdAt</c> and <c>meta.updatedAt</c>; tests pin it.</summary>
    public DateTimeOffset? Now { get; init; }
}
