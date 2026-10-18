using Draughtsman.Mcp.Rendering;

namespace Draughtsman.Server.Theming;

/// <summary>
/// The organisation's default diagram theme (admin Themes page) for the server renderer, read live so a change on the
/// page reaches the next PDF with no restart. Null while no theme is the default: the shipped Overpass theme draws.
/// </summary>
internal sealed class OrgDiagramThemeProvider(OrgThemeService themes) : IDiagramThemeProvider
{
    public DiagramThemeChoice? Default =>
        themes.Default is { } theme
            ? new DiagramThemeChoice(theme.Diagram.Base, theme.Diagram.Palette, theme.Diagram.ExportBackground)
            : null;
}
