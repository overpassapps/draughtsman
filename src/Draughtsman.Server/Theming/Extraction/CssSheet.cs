using System.Text;

namespace Draughtsman.Server.Theming.Extraction;

/// <summary>Which colour scheme a rule applies in: always, or only in a dark scope (prefers-color-scheme: dark, .dark, [data-theme=dark]).</summary>
internal enum SheetScheme
{
    Default,
    Dark,
}

internal sealed record CssDeclaration(string Property, string Value);

/// <summary>One style rule: its selector text (lower-cased, never interpreted beyond keyword tests), declarations and scheme.</summary>
internal sealed record CssRule(string Selector, IReadOnlyList<CssDeclaration> Declarations, SheetScheme Scheme);

/// <summary>
/// A small purpose-built CSS reader for the colour extractor: it keeps style rules (flattening @media, @supports, @layer and
/// @container) and drops what has no colour to give (@font-face, @keyframes, @import, nested rules). It never evaluates anything and
/// holds hard limits (characters, rules, declarations, nesting) so a hostile or enormous sheet costs a bounded, linear amount of
/// work; whatever is left unread is simply not used. No regex.
/// </summary>
internal static class CssSheet
{
    public const int MaxCharacters = 2 * 1024 * 1024;

    public const int MaxRules = 40_000;

    public const int MaxDeclarationsPerRule = 2_000;

    private const int MaxDepth = 8;

    private static readonly string[] SkippedAtRules = ["font-face", "keyframes", "-webkit-keyframes", "-moz-keyframes", "page", "property", "counter-style", "font-feature-values", "namespace", "charset", "import", "view-transition"];

    private static readonly string[] TransparentAtRules = ["media", "supports", "layer", "container", "document", "scope", "starting-style"];

    public static IReadOnlyList<CssRule> Parse(string css)
    {
        string text = css.Length > MaxCharacters ? css[..MaxCharacters] : css;
        text = StripComments(text);
        List<CssRule> rules = [];
        ParseBlock(text, SheetScheme.Default, 0, rules);
        return rules;
    }

    /// <summary>True when a selector is a dark-scope one: a class, attribute or id naming dark.</summary>
    public static bool IsDarkSelector(string selector)
    {
        return selector.Contains("dark", StringComparison.Ordinal) && !selector.Contains("darkgr", StringComparison.Ordinal);
    }

    private static string StripComments(string text)
    {
        if (!text.Contains("/*", StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        int i = 0;
        char quote = '\0';
        while (i < text.Length)
        {
            char c = text[i];
            if (quote != '\0')
            {
                builder.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    builder.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == quote)
                {
                    quote = '\0';
                }

                i++;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                builder.Append(c);
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                builder.Append(' ');
            }
            else
            {
                builder.Append(c);
                i++;
            }
        }

        return builder.ToString();
    }

    private static void ParseBlock(string text, SheetScheme scheme, int depth, List<CssRule> rules)
    {
        int i = 0;
        while (i < text.Length && rules.Count < MaxRules)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                return;
            }

            int preludeStart = i;
            int stop = ScanTo(text, i, stopAtSemicolon: true);
            if (stop >= text.Length)
            {
                return;
            }

            string prelude = text[preludeStart..stop].Trim();
            if (text[stop] == ';')
            {
                i = stop + 1;
                continue;
            }

            if (text[stop] == '}')
            {
                // A stray closing brace: skip it rather than stop, so one bad rule does not hide the rest.
                i = stop + 1;
                continue;
            }

            int close = MatchingBrace(text, stop);
            string body = close < 0 ? text[(stop + 1)..] : text[(stop + 1)..close];
            i = close < 0 ? text.Length : close + 1;

            if (prelude.StartsWith('@'))
            {
                string name = AtRuleName(prelude);
                if (TransparentAtRules.Contains(name, StringComparer.Ordinal))
                {
                    if (depth < MaxDepth)
                    {
                        SheetScheme inner = scheme;
                        if (name == "media")
                        {
                            string lower = prelude.ToLowerInvariant();
                            if (lower.Contains("prefers-color-scheme", StringComparison.Ordinal))
                            {
                                inner = lower.Contains("dark", StringComparison.Ordinal) ? SheetScheme.Dark : SheetScheme.Default;
                            }
                        }

                        ParseBlock(body, inner, depth + 1, rules);
                    }
                }
                else if (!SkippedAtRules.Contains(name, StringComparer.Ordinal))
                {
                    // An unknown at-rule with a block: ignore it, its contents are not style rules.
                }

                continue;
            }

            if (prelude.Length == 0)
            {
                continue;
            }

            string selector = prelude.ToLowerInvariant();
            SheetScheme ruleScheme = scheme == SheetScheme.Dark || IsDarkSelector(selector) ? SheetScheme.Dark : SheetScheme.Default;
            rules.Add(new CssRule(selector, ParseDeclarations(body), ruleScheme));
        }
    }

    private static string AtRuleName(string prelude)
    {
        int end = 1;
        while (end < prelude.Length && (char.IsAsciiLetterOrDigit(prelude[end]) || prelude[end] == '-'))
        {
            end++;
        }

        return prelude[1..end].ToLowerInvariant();
    }

    private static List<CssDeclaration> ParseDeclarations(string body)
    {
        List<CssDeclaration> declarations = [];
        int i = 0;
        while (i < body.Length && declarations.Count < MaxDeclarationsPerRule)
        {
            int stop = ScanTo(body, i, stopAtSemicolon: true);
            string chunk = body[i..Math.Min(stop, body.Length)];
            if (stop < body.Length && body[stop] == '{')
            {
                // A nested rule (CSS nesting): not a declaration, skipped whole.
                int close = MatchingBrace(body, stop);
                i = close < 0 ? body.Length : close + 1;
                continue;
            }

            i = stop + 1;
            int colon = chunk.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string property = chunk[..colon].Trim().ToLowerInvariant();
            string value = chunk[(colon + 1)..].Trim();
            int important = value.IndexOf("!important", StringComparison.OrdinalIgnoreCase);
            if (important >= 0)
            {
                value = value[..important].Trim();
            }

            if (property.Length > 0 && value.Length > 0 && property.Length <= 80)
            {
                declarations.Add(new CssDeclaration(property, value));
            }
        }

        return declarations;
    }

    /// <summary>The index of the next top-level <c>{</c>, <c>}</c> or (optionally) <c>;</c>, skipping strings and parentheses; the text length when there is none.</summary>
    private static int ScanTo(string text, int start, bool stopAtSemicolon)
    {
        int parens = 0;
        char quote = '\0';
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(':
                    parens++;
                    break;
                case ')':
                    if (parens > 0)
                    {
                        parens--;
                    }

                    break;
                case '{' or '}' when parens == 0:
                    return i;
                case ';' when parens == 0 && stopAtSemicolon:
                    return i;
            }
        }

        return text.Length;
    }

    /// <summary>The index of the brace closing the one at <paramref name="open"/>, or -1 when the text ends first.</summary>
    private static int MatchingBrace(string text, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }
}
