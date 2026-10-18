using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Import;

/// <summary>Caller-supplied choices for a Mermaid import. Everything has a default so <c>new()</c> is a valid import.</summary>
public sealed record MermaidImportOptions
{
    /// <summary>Document type that selects the kind vocabulary the shapes are mapped into. Freeform has no vocabulary and is rejected.</summary>
    public DocumentType Type { get; init; } = DocumentType.Flowchart;

    /// <summary>Title for the document. Null falls back to the Mermaid front-matter title, then to "Imported diagram".</summary>
    public string? Title { get; init; }

    /// <summary>Clock for <c>meta.createdAt</c> and <c>meta.updatedAt</c>; tests pin it.</summary>
    public DateTimeOffset? Now { get; init; }
}
