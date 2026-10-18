namespace Draughtsman.Server.Theming.Extraction;

/// <summary>
/// Reads the three things the colour extractor wants from a page's HTML: the text of its inline <c>&lt;style&gt;</c> blocks, its
/// <c>&lt;meta name="theme-color"&gt;</c> colours and its <c>&lt;link rel="stylesheet"&gt;</c> addresses. A forward scan with no regex
/// and no DOM: it walks tags, skips comments and the bodies of script, style, textarea and title elements so markup inside them is
/// never read as a tag, and stops at <see cref="MaxCharacters"/>. Nothing is executed or fetched here.
/// </summary>
internal static class HtmlScanner
{
    public const int MaxCharacters = 2 * 1024 * 1024;

    private const int MaxTags = 50_000;

    /// <summary>True when the text looks like an HTML page rather than a stylesheet: it has a tag the scanner is for.</summary>
    public static bool LooksLikeHtml(string text)
    {
        return text.Contains("<style", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<meta", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<link", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<!doctype", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> StyleBlocks(string html)
    {
        List<string> blocks = [];
        Scan(html, (tag, inner) =>
        {
            if (tag.Name == "style" && inner is not null)
            {
                blocks.Add(inner);
            }
        });
        return blocks;
    }

    /// <summary>The theme-color values, in document order.</summary>
    public static IReadOnlyList<string> ThemeColours(string html)
    {
        List<string> colours = [];
        Scan(html, (tag, _) =>
        {
            if (tag.Name == "meta" && tag.Attribute("name") is { } name && name.Trim().Equals("theme-color", StringComparison.OrdinalIgnoreCase) && tag.Attribute("content") is { } content)
            {
                colours.Add(content);
            }
        });
        return colours;
    }

    /// <summary>The <c>href</c> of each stylesheet link in document order, print-only sheets and empty values left out. Relative addresses are returned as written.</summary>
    public static IReadOnlyList<string> StylesheetLinks(string html)
    {
        List<string> links = [];
        Scan(html, (tag, _) =>
        {
            if (tag.Name != "link" || tag.Attribute("href") is not { } href || string.IsNullOrWhiteSpace(href))
            {
                return;
            }

            string rel = tag.Attribute("rel") ?? string.Empty;
            bool stylesheet = rel.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Contains("stylesheet", StringComparer.OrdinalIgnoreCase);
            string media = (tag.Attribute("media") ?? string.Empty).Trim();
            if (stylesheet && !media.Equals("print", StringComparison.OrdinalIgnoreCase))
            {
                links.Add(href.Trim());
            }
        });
        return links;
    }

    private sealed record Tag(string Name, IReadOnlyDictionary<string, string> Attributes)
    {
        public string? Attribute(string name)
        {
            return Attributes.TryGetValue(name, out string? value) ? value : null;
        }
    }

    private static readonly string[] RawTextElements = ["script", "style", "textarea", "title"];

    private static void Scan(string html, Action<Tag, string?> visit)
    {
        string text = html.Length > MaxCharacters ? html[..MaxCharacters] : html;
        int i = 0;
        int tags = 0;
        while (i < text.Length && tags < MaxTags)
        {
            int open = text.IndexOf('<', i);
            if (open < 0 || open + 1 >= text.Length)
            {
                return;
            }

            if (text.AsSpan(open).StartsWith("<!--", StringComparison.Ordinal))
            {
                int endComment = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
                i = endComment < 0 ? text.Length : endComment + 3;
                continue;
            }

            char first = text[open + 1];
            if (!char.IsAsciiLetter(first))
            {
                i = open + 1;
                continue;
            }

            int end = TagEnd(text, open);
            if (end < 0)
            {
                return;
            }

            tags++;
            Tag tag = ParseTag(text, open + 1, end);
            string? inner = null;
            i = end + 1;
            if (RawTextElements.Contains(tag.Name, StringComparer.Ordinal))
            {
                int close = text.IndexOf("</" + tag.Name, i, StringComparison.OrdinalIgnoreCase);
                inner = close < 0 ? text[i..] : text[i..close];
                i = close < 0 ? text.Length : close;
            }

            visit(tag, inner);
        }
    }

    /// <summary>The index of the <c>&gt;</c> that ends the tag opened at <paramref name="open"/>, respecting quoted attribute values; -1 when there is none.</summary>
    private static int TagEnd(string text, int open)
    {
        char quote = '\0';
        for (int i = open + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static Tag ParseTag(string text, int start, int end)
    {
        int i = start;
        while (i < end && !char.IsWhiteSpace(text[i]) && text[i] != '/')
        {
            i++;
        }

        string name = text[start..i].ToLowerInvariant();
        Dictionary<string, string> attributes = new(StringComparer.Ordinal);
        while (i < end)
        {
            while (i < end && (char.IsWhiteSpace(text[i]) || text[i] == '/'))
            {
                i++;
            }

            int nameStart = i;
            while (i < end && !char.IsWhiteSpace(text[i]) && text[i] != '=' && text[i] != '/')
            {
                i++;
            }

            if (i == nameStart)
            {
                i++;
                continue;
            }

            string attribute = text[nameStart..i].ToLowerInvariant();
            while (i < end && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            string value = string.Empty;
            if (i < end && text[i] == '=')
            {
                i++;
                while (i < end && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (i < end && text[i] is '"' or '\'')
                {
                    char quote = text[i];
                    int close = text.IndexOf(quote, i + 1, end - i - 1);
                    close = close < 0 ? end : close;
                    value = text[(i + 1)..close];
                    i = close + 1;
                }
                else
                {
                    int valueStart = i;
                    while (i < end && !char.IsWhiteSpace(text[i]))
                    {
                        i++;
                    }

                    value = text[valueStart..i];
                }
            }

            attributes.TryAdd(attribute, value);
        }

        return new Tag(name, attributes);
    }
}
