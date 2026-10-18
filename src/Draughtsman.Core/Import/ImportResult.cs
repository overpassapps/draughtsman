using System.Text.Json.Serialization;
using Draughtsman.Core.Models;

namespace Draughtsman.Core.Import;

/// <summary>
/// Output of an import: the document plus everything the caller should know about how faithfully it was produced.
/// Geometry is present only where the source supplied it: the Mermaid importer never sets it, so every node's
/// geometry is null, while DSL text keeps the position a node was written with (<c>@ x,y</c>) and a draw.io file
/// carries the position and size of every shape. A caller must still null-check geometry and run layout to place the rest.
/// </summary>
/// <param name="Document">The produced document.</param>
/// <param name="Warnings">Zero or more warnings, in source order.</param>
public sealed record ImportResult(Document Document, IReadOnlyList<ImportWarning> Warnings)
{
    /// <summary>Layout direction hint from the source (TB, LR, RL, BT), or null when the source gave none.</summary>
    public string? Direction { get; init; }

    /// <summary>
    /// The pages of a source that has them (a draw.io file), in file order, with the one that was imported marked; null for a source that
    /// is one diagram. The document is only ever one page, so a caller that wants another asks again with that page's number.
    /// </summary>
    public IReadOnlyList<ImportPage>? Pages { get; init; }

    /// <summary>True when the source declared a diagram type this importer cannot read; the document is then empty.</summary>
    [JsonIgnore]
    public bool UnsupportedDiagram => Warnings.Any(w => w.Code == ImportWarningCodes.UnsupportedDiagram);
}

/// <summary>One page of a multi-page source. <see cref="Index"/> counts from 1, as a caller names a page; <see cref="Name"/> is empty when the source did not name it.</summary>
public sealed record ImportPage(int Index, string Name, bool Selected);
