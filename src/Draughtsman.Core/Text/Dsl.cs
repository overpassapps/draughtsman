using Draughtsman.Core.Models;

namespace Draughtsman.Core.Text;

/// <summary>
/// The Draughtsman text format: the diagram an agent reads and writes. Parse and Print are inverses:
/// <c>Parse(Print(doc)).Document</c> is structurally equal to <c>doc</c>. The grammar is docs/spec/dsl.grammar
/// and the format is described in docs/spec/dsl.md.
/// </summary>
public static class Dsl
{
    /// <summary>
    /// Parses DSL text into a document. Throws <see cref="DslParseException"/> with a line and column on the first
    /// syntax error; lint-level problems (unknown kinds, references to undeclared nodes) come back as warnings.
    /// Nodes written without <c>@ x,y</c> have null geometry so the layout step can place them.
    /// </summary>
    public static ParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new DslParser(text).Parse();
    }

    /// <summary>
    /// Prints a document in canonical form: header, then nodes and containers in document order, then edges, one
    /// line each. Geometry is always written when present, style only when set, unknown props verbatim.
    /// </summary>
    public static string Print(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return DslPrinter.Print(document);
    }
}
