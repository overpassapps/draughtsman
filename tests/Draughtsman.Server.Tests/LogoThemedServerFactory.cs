using System.Text;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Tests;

/// <summary>
/// A server whose chrome theme names the Overpass logos, compact header marks and favicon. The shipped default theme names
/// none of them (the app's own Draughtsman mark stands in until an organisation supplies a brand), so a test of the logo,
/// compact-mark and favicon plumbing needs a theme that does, the way an organisation's own theme file would: written into
/// the data folder with its assets beside it, from the real files copied next to the test binary.
/// </summary>
internal sealed class LogoThemedServerFactory : DraughtsmanServerFactory
{
    public const string ThemeFile = "with-logos.yaml";

    public LogoThemedServerFactory()
    {
        string themes = Path.Combine(DataFolder, "themes");
        string assets = Path.Combine(themes, "assets");
        Directory.CreateDirectory(assets);
        foreach (string name in new[] { "overpass-logo-light.svg", "overpass-logo-compact.svg", "overpass-logo-compact-light.svg", "overpass-favicon.svg" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "themes", name), Path.Combine(assets, name));
        }

        // The shipped theme with the four lines it deliberately leaves out: same name, product name, mode and colours.
        string shipped = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "themes", "chrome.yaml"), Encoding.UTF8);
        const string logos = "logo: assets/overpass-logo-light.svg\nlogoCompact: assets/overpass-logo-compact.svg\nlogoCompactLight: assets/overpass-logo-compact-light.svg\nfavicon: assets/overpass-favicon.svg\n";
        File.WriteAllText(Path.Combine(themes, ThemeFile), logos + shipped.Replace("\r\n", "\n", StringComparison.Ordinal), Encoding.UTF8);
        File.WriteAllText(Path.Combine(DataFolder, DraughtsmanConfig.FileName), $"theme:\n  chrome: {ThemeFile}\n", Encoding.UTF8);
    }
}
