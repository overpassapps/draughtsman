namespace Draughtsman.Mcp.Rendering;

/// <summary>
/// The rendered SVG plus what the render had to leave out - empty for an ordinary document. Only edges whose
/// endpoints the document does not contain are reported; <c>patch_document</c>'s <c>removeNode</c> warns rather
/// than blocking, so a stored document with dangling edges is a normal outcome the picture cannot show.
/// </summary>
public sealed record SvgRenderResult(string Svg, IReadOnlyList<string> Warnings);
