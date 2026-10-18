using System.Text;

namespace Draughtsman.Core.Import.Drawio;

/// <summary>
/// Reduces a draw.io label to plain text. With <c>html=1</c> in its style a label is HTML a stranger wrote, so this keeps only
/// what the HTML says: text, with line breaks where a break or a block element was. No tag, attribute, script or style content,
/// comment or declaration reaches the result, and an entity is decoded exactly once (what <c>&amp;lt;script&amp;gt;</c> spells is
/// the author's text, not a tag, and is never read again). It is a single forward pass with no recursion and no look-ahead longer
/// than one tag, so hostile markup costs time linear in its length.
/// </summary>
internal static class DrawioText
{
    /// <summary>A tag longer than this (opening bracket to closing bracket) is not a tag; its opening bracket is text.</summary>
    public const int MaxTagLength = 4096;

    /// <summary>The longest entity (<c>&amp;#x10FFFF;</c> is 10 characters) plus a margin: past this a <c>&amp;</c> is just an ampersand.</summary>
    private const int MaxEntityLength = 12;

    private static readonly HashSet<string> s_blocks = new(StringComparer.Ordinal)
    {
        "div", "p", "li", "ul", "ol", "dl", "dt", "dd", "h1", "h2", "h3", "h4", "h5", "h6", "table", "thead", "tbody", "tfoot", "tr",
        "blockquote", "pre", "hr", "section", "article", "header", "footer", "nav", "aside", "figure", "figcaption", "address", "form", "fieldset",
    };

    private static readonly Dictionary<string, string> s_entities = new(StringComparer.Ordinal)
    {
        ["amp"] = "&",
        ["lt"] = "<",
        ["gt"] = ">",
        ["quot"] = "\"",
        ["apos"] = "'",
        ["nbsp"] = " ",
        ["copy"] = "©",
        ["reg"] = "®",
        ["trade"] = "™",
        ["hellip"] = "…",
        ["ndash"] = "–",
        ["mdash"] = "—",
        ["lsquo"] = "‘",
        ["rsquo"] = "’",
        ["ldquo"] = "“",
        ["rdquo"] = "”",
        ["bull"] = "•",
        ["middot"] = "·",
        ["times"] = "×",
        ["divide"] = "÷",
        ["deg"] = "°",
        ["plusmn"] = "±",
        ["euro"] = "€",
        ["pound"] = "£",
        ["yen"] = "¥",
        ["cent"] = "¢",
        ["sect"] = "§",
        ["para"] = "¶",
        ["larr"] = "←",
        ["rarr"] = "→",
        ["uarr"] = "↑",
        ["darr"] = "↓",
        ["harr"] = "↔",
        ["eacute"] = "é",
        ["egrave"] = "è",
        ["agrave"] = "à",
        ["aacute"] = "á",
        ["uuml"] = "ü",
        ["ouml"] = "ö",
        ["auml"] = "ä",
        ["szlig"] = "ß",
        ["ntilde"] = "ñ",
        ["ccedil"] = "ç",
        ["iexcl"] = "¡",
        ["iquest"] = "¿",
        ["laquo"] = "«",
        ["raquo"] = "»",
    };

    /// <summary>The text of an HTML label.</summary>
    public static string FromHtml(string html)
    {
        var text = new StringBuilder(html.Length);
        int nextClose = -2; // index of the next '>' at or after the last search start; -2 not looked for yet, -1 there is none
        int i = 0;
        while (i < html.Length)
        {
            char c = html[i];
            if (c == '<' && TryReadMarkup(html, ref i, ref nextClose, text))
            {
                continue;
            }

            if (c == '&' && TryDecodeEntity(html, i, out string? decoded, out int length))
            {
                text.Append(decoded);
                i += length;
                continue;
            }

            text.Append(c);
            i++;
        }

        return Collapse(text.ToString());
    }

    /// <summary>The text of a label that is not HTML: as written, with its line breaks normalised and control characters dropped.</summary>
    public static string FromPlain(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                result.Append('\n');
            }
            else if (c == '\t')
            {
                result.Append(' ');
            }
            else if (c == '\n' || !char.IsControl(c))
            {
                result.Append(c);
            }
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// Handles the markup that starts at <paramref name="i"/> (a '&lt;'): a comment, a declaration or a tag. Returns false, leaving
    /// <paramref name="i"/> alone, when it is not markup at all (<c>a &lt; b</c>, a tag with no closing bracket in reach), so the bracket is text.
    /// </summary>
    private static bool TryReadMarkup(string html, ref int i, ref int nextClose, StringBuilder text)
    {
        if (i + 1 >= html.Length)
        {
            return false;
        }

        char next = html[i + 1];
        if (next == '!' && string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
        {
            int end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
            i = end < 0 ? html.Length : end + 3;
            return true;
        }

        bool closing = next == '/';
        int nameStart = closing ? i + 2 : i + 1;
        bool declaration = next is '!' or '?';
        if (!declaration && !(nameStart < html.Length && char.IsAsciiLetter(html[nameStart])))
        {
            return false;
        }

        // The closing bracket is looked for once per run of brackets, not once per bracket: "<a<a<a<a..." must not rescan to the end each time.
        if (nextClose == -2 || (nextClose >= 0 && nextClose < i))
        {
            nextClose = html.IndexOf('>', i);
        }

        if (nextClose < 0 || nextClose - i + 1 > MaxTagLength)
        {
            return false;
        }

        int close = nextClose;
        i = close + 1;
        if (declaration)
        {
            return true;
        }

        int nameEnd = nameStart;
        while (nameEnd < close && nameEnd - nameStart < 16 && char.IsAsciiLetterOrDigit(html[nameEnd]))
        {
            nameEnd++;
        }

        string name = html.Substring(nameStart, nameEnd - nameStart).ToLowerInvariant();
        bool selfClosing = html[close - 1] == '/';
        if (name == "br")
        {
            text.Append('\n');
        }
        else if (s_blocks.Contains(name))
        {
            EnsureLineBreak(text);
            if (name == "li" && !closing)
            {
                text.Append("- ");
            }
        }
        else if (closing && name is "td" or "th")
        {
            text.Append(' ');
        }
        else if (!closing && !selfClosing && name is "script" or "style")
        {
            i = SkipElementContent(html, name, i);
        }

        return true;
    }

    /// <summary>Past the closing tag of a script or style whose content is code, not text; to the end of the label when it is never closed.</summary>
    private static int SkipElementContent(string html, string name, int from)
    {
        int end = html.IndexOf("</" + name, from, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return html.Length;
        }

        int close = html.IndexOf('>', end);
        return close < 0 ? html.Length : close + 1;
    }

    private static void EnsureLineBreak(StringBuilder text)
    {
        if (text.Length > 0 && text[^1] != '\n')
        {
            text.Append('\n');
        }
    }

    private static bool TryDecodeEntity(string html, int start, out string? decoded, out int length)
    {
        decoded = null;
        length = 0;
        int limit = Math.Min(html.Length, start + MaxEntityLength + 1);
        int semicolon = -1;
        for (int j = start + 1; j < limit; j++)
        {
            if (html[j] == ';')
            {
                semicolon = j;
                break;
            }
        }

        if (semicolon < 0 || semicolon == start + 1)
        {
            return false;
        }

        ReadOnlySpan<char> body = html.AsSpan(start + 1, semicolon - start - 1);
        if (body[0] == '#')
        {
            bool hex = body.Length > 1 && body[1] is 'x' or 'X';
            ReadOnlySpan<char> digits = body[(hex ? 2 : 1)..];
            if (digits.IsEmpty
                || !int.TryParse(digits, hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int code)
                || code < 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
            {
                return false;
            }

            decoded = char.ConvertFromUtf32(code);
        }
        else if (!s_entities.TryGetValue(body.ToString(), out decoded))
        {
            return false;
        }

        length = semicolon - start + 1;
        return true;
    }

    /// <summary>
    /// Whitespace the way a label reads: runs of spaces and tabs become one space, each line is trimmed, three or more line breaks become
    /// two (one blank line), control characters are dropped and the ends are trimmed.
    /// </summary>
    private static string Collapse(string raw)
    {
        var result = new StringBuilder(raw.Length);
        int breaks = 0;
        bool space = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\r')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '\n')
                {
                    continue;
                }

                c = '\n';
            }

            if (c == '\n')
            {
                breaks++;
                space = false;
            }
            else if (char.IsWhiteSpace(c))
            {
                space = result.Length > 0 && breaks == 0;
            }
            else if (!char.IsControl(c))
            {
                if (breaks > 0)
                {
                    if (result.Length > 0)
                    {
                        result.Append('\n', Math.Min(breaks, 2));
                    }

                    breaks = 0;
                }
                else if (space)
                {
                    result.Append(' ');
                }

                space = false;
                result.Append(c);
            }
        }

        return result.ToString();
    }
}
