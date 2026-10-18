using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Import.Sql;

/// <summary>Caller-supplied choices for a SQL import. Everything has a default so <c>new()</c> is a valid import.</summary>
public sealed record SqlImportOptions
{
    /// <summary>The dialect of the pasted SQL; <see cref="SqlDialect.Auto"/> decides from the text.</summary>
    public SqlDialect Dialect { get; init; } = SqlDialect.Auto;

    /// <summary>Architecture (the default) or freeform: the two document types that can hold an entity. Flowchart cannot.</summary>
    public DocumentType Type { get; init; } = DocumentType.Architecture;

    /// <summary>Title for the document; null falls back to "Imported schema".</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Read a <c>[TAG]</c> in the comment above a CREATE TABLE as the table's tag, colour tables by it and add a legend.
    /// Off leaves the output exactly as it is for SQL with no tags.
    /// </summary>
    public bool Tags { get; init; } = true;

    /// <summary>
    /// Read the relationships a comment declares that the database does not enforce (<c>-- -&gt; Users.Id (no FK)</c>, or a
    /// listed <c>dbo.Users.TypeId -&gt; dbo.UserTypes.Id</c>) as dashed edges labelled "column (no FK)". Off draws foreign keys
    /// only and raises no warning about those comments.
    /// </summary>
    public bool Implied { get; init; } = true;

    /// <summary>Clock for <c>meta.createdAt</c> and <c>meta.updatedAt</c>; tests pin it.</summary>
    public DateTimeOffset? Now { get; init; }
}
