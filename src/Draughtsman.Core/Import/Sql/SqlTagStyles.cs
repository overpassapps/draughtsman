using Draughtsman.Core.Models;

namespace Draughtsman.Core.Import.Sql;

/// <summary>
/// Maps table tags to colours. A tag naming or containing "shared" is neutral; every other distinct tag (compared without
/// case) takes the next colour of a small fixed palette in order of first appearance, so the same SQL always draws the
/// same way. Fill, stroke and text colour are all explicit, so an export reads without the theme.
/// </summary>
internal static class SqlTagStyles
{
    public static readonly NodeStyle Neutral = new() { Fill = "#f1f5f9", Stroke = "#64748b", TextColor = "#0f172a" };

    // Pale fill, saturated stroke, dark text: every text/fill pair is well past 7:1.
    private static readonly NodeStyle[] Palette =
    [
        new() { Fill = "#dbeafe", Stroke = "#1d4ed8", TextColor = "#1e3a8a" },
        new() { Fill = "#fef3c7", Stroke = "#b45309", TextColor = "#78350f" },
        new() { Fill = "#dcfce7", Stroke = "#15803d", TextColor = "#14532d" },
        new() { Fill = "#f3e8ff", Stroke = "#7e22ce", TextColor = "#581c87" },
        new() { Fill = "#ffe4e6", Stroke = "#be123c", TextColor = "#881337" },
        new() { Fill = "#ccfbf1", Stroke = "#0f766e", TextColor = "#134e4a" },
    ];

    public static bool IsShared(string tag)
    {
        return tag.Contains("shared", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The style of each distinct tag (keyed without case), given the tags in order of first appearance.</summary>
    public static Dictionary<string, NodeStyle> Assign(IEnumerable<string> tagsInOrder)
    {
        var styles = new Dictionary<string, NodeStyle>(StringComparer.OrdinalIgnoreCase);
        int next = 0;
        foreach (string tag in tagsInOrder)
        {
            if (styles.ContainsKey(tag))
            {
                continue;
            }

            styles[tag] = IsShared(tag) ? Neutral : Palette[next++ % Palette.Length];
        }

        return styles;
    }
}
