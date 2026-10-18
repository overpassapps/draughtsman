using Draughtsman.Core.Models;

namespace Draughtsman.Core.Text;

/// <summary>A parsed document plus the lint warnings raised while reading it. Warnings never stop a parse.</summary>
public sealed record ParseResult(Document Document, IReadOnlyList<DslWarning> Warnings);
