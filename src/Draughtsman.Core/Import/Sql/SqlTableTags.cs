using System.Text.RegularExpressions;

namespace Draughtsman.Core.Import.Sql;

/// <summary>
/// Reads the bracketed tag a schema's author put in the comment block directly above a CREATE TABLE
/// (<c>-- [V2 NEW] why it exists</c>, or the same inside a <c>/* */</c> block). The block is the run of comment lines that
/// touches the statement's line: a blank line or any code ends it. The first <c>[tag]</c> in it is the table's tag.
/// </summary>
internal static partial class SqlTableTags
{
    /// <summary>The tag above the statement starting on one-based <paramref name="createLine"/>, or null when there is none.</summary>
    public static string? Find(string[] lines, int createLine)
    {
        var block = new List<string>();
        int i = createLine - 2; // the line above the statement, zero-based
        while (i >= 0)
        {
            string text = lines[i].Trim();
            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                block.Add(text[2..]);
                i--;
            }
            else if (text.EndsWith("*/", StringComparison.Ordinal))
            {
                int start = i;
                while (start >= 0 && !lines[start].Contains("/*", StringComparison.Ordinal))
                {
                    start--;
                }

                if (start < 0)
                {
                    break;
                }

                for (int j = i; j >= start; j--)
                {
                    block.Add(lines[j]);
                }

                i = start - 1;
            }
            else
            {
                break;
            }
        }

        // The block was collected bottom-up; the first tag in reading order wins.
        block.Reverse();
        Match match = TagPattern().Match(string.Join('\n', block));
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex(@"\[([^\[\]\r\n]{1,40})\]", RegexOptions.None, RegexLimits.TimeoutMilliseconds)]
    private static partial Regex TagPattern();
}
