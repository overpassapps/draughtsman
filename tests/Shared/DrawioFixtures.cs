namespace Draughtsman.Tests.Shared;

/// <summary>
/// The hand-written draw.io files in <c>tests/Draughtsman.Core.Tests/Import/DrawioFixtures</c>, copied next to each test project's binary
/// (Core, MCP and Server tests all import the same files). They are LF text (.gitattributes), and every line break inside a label is the
/// character reference <c>&amp;#xa;</c> rather than a literal one, so what they import is the same on every platform.
/// </summary>
internal static class DrawioFixtures
{
    public static string Read(string name)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "drawio-fixtures", name));
    }
}
