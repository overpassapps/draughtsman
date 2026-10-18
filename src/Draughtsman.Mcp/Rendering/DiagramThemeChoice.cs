namespace Draughtsman.Mcp.Rendering;

/// <summary>
/// An organisation's diagram theme as the server renderer needs it: one of the shipped themes as a base, palette
/// replacements, and the paper colour. The same shape as the admin Themes page's diagram block, applied the way
/// <c>web/src/theme/orgTheme.ts</c> <c>applyDiagramOverride</c> does, so a PDF carries the colours the editor and the
/// client's export do. Fonts are not part of it: the server draws <c>sans-serif</c>.
/// </summary>
/// <param name="Base">A shipped theme name (<c>overpass</c>, <c>neutral</c>, <c>mono</c>); an unknown one falls back to overpass.</param>
/// <param name="Palette">Palette name to replacement hex colour; a name the base does not have is ignored.</param>
/// <param name="ExportBackground">The paper, when the theme sets one directly.</param>
public sealed record DiagramThemeChoice(string Base, IReadOnlyDictionary<string, string> Palette, string? ExportBackground);

/// <summary>Where a host keeps the organisation's default diagram theme; null when it has none (the shipped Overpass theme applies).</summary>
public interface IDiagramThemeProvider
{
    DiagramThemeChoice? Default { get; }
}
