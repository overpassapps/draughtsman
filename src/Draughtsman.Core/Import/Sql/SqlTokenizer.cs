using System.Text;

namespace Draughtsman.Core.Import.Sql;

internal enum SqlTokenKind
{
    /// <summary>A bare identifier or keyword; the only kind a keyword test may match, so a quoted <c>[Key]</c> is always a name.</summary>
    Word,

    /// <summary>A <c>[bracket]</c>, <c>"double quote"</c> or <c>`backtick`</c> identifier, unescaped.</summary>
    Quoted,
    Number,

    /// <summary>A string literal (or dollar-quoted body), unescaped.</summary>
    String,
    Symbol,
}

/// <param name="Kind">What the token is.</param>
/// <param name="Text">The token's text: a quoted identifier or string without its quotes.</param>
/// <param name="Line">One-based source line.</param>
/// <param name="LineStart">Whether it is the first token on its line, which is how a statement without a terminator is told from a continuation.</param>
internal readonly record struct SqlToken(SqlTokenKind Kind, string Text, int Line, bool LineStart)
{
    public bool IsWord(string keyword)
    {
        return Kind == SqlTokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsSymbol(string symbol)
    {
        return Kind == SqlTokenKind.Symbol && Text == symbol;
    }

    public bool IsName => Kind is SqlTokenKind.Word or SqlTokenKind.Quoted;
}

/// <summary>One line of a comment: the text after <c>--</c> or <c>#</c>, or one line of a <c>/* */</c> body with its leading <c>*</c> dropped.</summary>
/// <param name="Line">One-based source line the text is on.</param>
/// <param name="Text">The comment text, trimmed.</param>
/// <param name="Trailing">Whether SQL precedes it on the same line (<c>Id int, -- note</c>) rather than the line holding only the comment.</param>
internal readonly record struct SqlComment(int Line, string Text, bool Trailing);

/// <summary>
/// Turns SQL text into tokens without understanding it: comments (<c>--</c>, nested <c>/* */</c>, and <c>#</c> for MySQL)
/// vanish from the token list (a caller that wants their text passes a list to collect it), strings and quoted identifiers become single tokens whatever they contain, and everything else is a word, a
/// number or one symbol character (<c>::</c> is the one two-character symbol). It never throws: an unterminated string or
/// comment runs to the end of the text. Linear in the length of the source.
/// </summary>
internal static class SqlTokenizer
{
    public static List<SqlToken> Tokenize(string source, SqlDialect dialect, List<SqlComment>? comments = null)
    {
        var tokens = new List<SqlToken>();
        int i = 0;
        int line = 1;
        int lastTokenLine = 0;
        int n = source.Length;

        void Add(SqlTokenKind kind, string text, int tokenLine)
        {
            tokens.Add(new SqlToken(kind, text, tokenLine, tokenLine != lastTokenLine));
            lastTokenLine = tokenLine;
        }

        while (i < n)
        {
            char c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c) || c == '﻿')
            {
                i++;
                continue;
            }

            if ((c == '-' && i + 1 < n && source[i + 1] == '-') || (c == '#' && dialect == SqlDialect.MySql))
            {
                int end = SkipLine(source, i);
                comments?.Add(new SqlComment(line, source[(c == '-' ? i + 2 : i + 1)..end].Trim(), lastTokenLine == line));
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                int first = line;
                int end = SkipBlockComment(source, i, ref line);
                if (comments is not null)
                {
                    bool closed = end - 2 >= i + 2 && source[end - 2] == '*' && source[end - 1] == '/';
                    AddBlockComment(comments, source[(i + 2)..(closed ? end - 2 : end)], first, lastTokenLine == first);
                }

                i = end;
                continue;
            }

            int startLine = line;
            switch (c)
            {
                case '\'':
                    Add(SqlTokenKind.String, ReadQuoted(source, ref i, ref line, '\'', dialect == SqlDialect.MySql), startLine);
                    continue;
                case '"':
                    Add(SqlTokenKind.Quoted, ReadQuoted(source, ref i, ref line, '"', false), startLine);
                    continue;
                case '`':
                    Add(SqlTokenKind.Quoted, ReadQuoted(source, ref i, ref line, '`', false), startLine);
                    continue;
                case '[' when StartsBracketIdentifier(source, i, dialect):
                    Add(SqlTokenKind.Quoted, ReadBracketed(source, ref i, ref line), startLine);
                    continue;
                case '$' when TryReadDollarQuoted(source, ref i, ref line, out string? body):
                    Add(SqlTokenKind.String, body!, startLine);
                    continue;
                case ':' when i + 1 < n && source[i + 1] == ':':
                    Add(SqlTokenKind.Symbol, "::", startLine);
                    i += 2;
                    continue;
                default:
                    break;
            }

            if (IsWordStart(c))
            {
                int start = i;
                while (i < n && IsWordPart(source[i]))
                {
                    i++;
                }

                // N'..', E'..', X'..', B'..': a one-letter prefix glued to a string is part of the literal.
                if (i - start == 1 && i < n && source[i] == '\'' && "NnEeXxBb".Contains(source[start]))
                {
                    Add(SqlTokenKind.String, ReadQuoted(source, ref i, ref line, '\'', dialect == SqlDialect.MySql || char.ToUpperInvariant(source[start]) == 'E'), startLine);
                    continue;
                }

                Add(SqlTokenKind.Word, source[start..i], startLine);
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(source[i + 1])))
            {
                int start = i;
                i++;
                while (i < n && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_' || (source[i] == '.' && i + 1 < n && char.IsAsciiDigit(source[i + 1]))))
                {
                    i++;
                }

                Add(SqlTokenKind.Number, source[start..i], startLine);
                continue;
            }

            Add(SqlTokenKind.Symbol, c.ToString(), startLine);
            i++;
        }

        return tokens;
    }

    private static bool IsWordStart(char c)
    {
        return char.IsLetter(c) || c is '_' or '@' or '#';
    }

    private static bool IsWordPart(char c)
    {
        return char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';
    }

    /// <summary>Emits one <see cref="SqlComment"/> per line of a block comment's body; only the first can trail code.</summary>
    private static void AddBlockComment(List<SqlComment> comments, string body, int firstLine, bool trailing)
    {
        string[] lines = body.Split('\n');
        for (int k = 0; k < lines.Length; k++)
        {
            string text = lines[k].Trim().TrimStart('*').Trim();
            if (text.Length > 0)
            {
                comments.Add(new SqlComment(firstLine + k, text, k == 0 && trailing));
            }
        }
    }

    private static int SkipLine(string source, int i)
    {
        int end = source.IndexOf('\n', i);
        return end < 0 ? source.Length : end;
    }

    /// <summary>Skips a block comment (they nest in T-SQL and Postgres), counting the lines it spans; an unterminated one ends the text.</summary>
    private static int SkipBlockComment(string source, int i, ref int line)
    {
        int depth = 0;
        int n = source.Length;
        while (i < n)
        {
            if (source[i] == '/' && i + 1 < n && source[i + 1] == '*')
            {
                depth++;
                i += 2;
            }
            else if (source[i] == '*' && i + 1 < n && source[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                {
                    return i;
                }
            }
            else
            {
                if (source[i] == '\n')
                {
                    line++;
                }

                i++;
            }
        }

        return n;
    }

    /// <summary>Reads a string or quoted identifier whose quote is doubled to escape it (and, for some strings, escaped by a backslash).</summary>
    private static string ReadQuoted(string source, ref int i, ref int line, char quote, bool backslashEscapes)
    {
        var text = new StringBuilder();
        int n = source.Length;
        i++;
        while (i < n)
        {
            char c = source[i];
            if (c == quote)
            {
                if (i + 1 < n && source[i + 1] == quote)
                {
                    text.Append(quote);
                    i += 2;
                    continue;
                }

                i++;
                return text.ToString();
            }

            if (backslashEscapes && c == '\\' && i + 1 < n)
            {
                text.Append(source[i + 1]);
                if (source[i + 1] == '\n')
                {
                    line++;
                }

                i += 2;
                continue;
            }

            if (c == '\n')
            {
                line++;
            }

            text.Append(c);
            i++;
        }

        return text.ToString();
    }

    /// <summary>
    /// A <c>[</c> opens a bracketed identifier, except where it is an array bound. T-SQL and SQLite always read it as one
    /// (bar the empty <c>[]</c> and <c>[3]</c>). PostgreSQL and MySQL use brackets only for array types and subscripts, so there it is an
    /// identifier only when a plain name closes on the same line (<c>[dbo].[Users]</c>, not <c>int[]</c> or <c>[3]</c>),
    /// which keeps a pasted SSMS script readable even when the dialect hint is wrong.
    /// </summary>
    private static bool StartsBracketIdentifier(string source, int i, SqlDialect dialect)
    {
        int next = i + 1;
        while (next < source.Length && source[next] == ' ')
        {
            next++;
        }

        if (next >= source.Length || source[next] == ']')
        {
            return false;
        }

        if (dialect is not (SqlDialect.Postgres or SqlDialect.MySql))
        {
            // "[3]" is an array bound (the dialect may have been guessed); a name made only of digits is not worth the risk.
            int digits = next;
            while (digits < source.Length && char.IsAsciiDigit(source[digits]))
            {
                digits++;
            }

            return !(digits > next && digits < source.Length && source[digits] == ']');
        }

        if (!char.IsLetter(source[next]) && source[next] != '_')
        {
            return false;
        }

        for (int k = next; k < source.Length; k++)
        {
            char c = source[k];
            if (c == ']')
            {
                return true;
            }

            if (!(char.IsLetterOrDigit(c) || c is '_' or ' ' or '.' or '$' or '#' or '@' or '-'))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Reads <c>[name]</c>, where <c>]]</c> is a literal bracket.</summary>
    private static string ReadBracketed(string source, ref int i, ref int line)
    {
        var text = new StringBuilder();
        int n = source.Length;
        i++;
        while (i < n)
        {
            char c = source[i];
            if (c == ']')
            {
                if (i + 1 < n && source[i + 1] == ']')
                {
                    text.Append(']');
                    i += 2;
                    continue;
                }

                i++;
                return text.ToString();
            }

            if (c == '\n')
            {
                line++;
            }

            text.Append(c);
            i++;
        }

        return text.ToString();
    }

    /// <summary>Reads a Postgres <c>$tag$ ... $tag$</c> body, so a function definition is one token whatever it contains.</summary>
    private static bool TryReadDollarQuoted(string source, ref int i, ref int line, out string? body)
    {
        body = null;
        int n = source.Length;
        int tagEnd = i + 1;
        while (tagEnd < n && (char.IsLetterOrDigit(source[tagEnd]) || source[tagEnd] == '_'))
        {
            tagEnd++;
        }

        if (tagEnd >= n || source[tagEnd] != '$')
        {
            return false;
        }

        string delimiter = source[i..(tagEnd + 1)];
        int close = source.IndexOf(delimiter, tagEnd + 1, StringComparison.Ordinal);
        int bodyEnd = close < 0 ? n : close;
        body = source[(tagEnd + 1)..bodyEnd];
        for (int k = tagEnd + 1; k < bodyEnd; k++)
        {
            if (source[k] == '\n')
            {
                line++;
            }
        }

        i = close < 0 ? n : close + delimiter.Length;
        return true;
    }
}
