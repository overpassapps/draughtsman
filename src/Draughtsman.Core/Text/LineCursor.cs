using System.Globalization;
using System.Text;

namespace Draughtsman.Core.Text;

/// <summary>
/// Character-level scanner over one DSL line with the comment already removed. The parser pulls tokens on demand,
/// so the same characters can be a label or a quoted key depending on what follows them.
/// </summary>
internal sealed class LineCursor
{
    private readonly string _text;

    public LineCursor(string text, int line)
    {
        _text = text;
        Line = line;
    }

    public int Line { get; }

    public int Position { get; private set; }

    /// <summary>1-based column of the next character.</summary>
    public int Column => Position + 1;

    /// <summary>1-based column of the next token, skipping any whitespace first. Use this to record where a token starts.</summary>
    public int TokenColumn
    {
        get
        {
            SkipSpaces();
            return Column;
        }
    }

    public bool AtEnd => Position >= _text.Length;

    public char Current => _text[Position];

    /// <summary>
    /// Removes a trailing comment. A # starts a comment when it begins the line or follows whitespace and is not inside
    /// a quoted string, so <c>style.fill=#FFF</c> keeps its colour and <c>"C# service"</c> keeps its label.
    /// </summary>
    public static string StripComment(string line)
    {
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuote)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    i++;
                }
                else if (c == '"')
                {
                    inQuote = false;
                }
            }
            else if (c == '"')
            {
                inQuote = true;
            }
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i];
            }
        }

        return line;
    }

    public void SkipSpaces()
    {
        while (!AtEnd && char.IsWhiteSpace(Current))
        {
            Position++;
        }
    }

    public void Reset(int position)
    {
        Position = position;
    }

    public bool TryConsume(char c)
    {
        if (!AtEnd && Current == c)
        {
            Position++;
            return true;
        }

        return false;
    }

    public void Expect(char c, string context)
    {
        SkipSpaces();
        if (!TryConsume(c))
        {
            throw Error(AtEnd ? $"expected '{c}' {context}" : $"expected '{c}' {context} but found '{Current}'");
        }
    }

    /// <summary>The next bare word without consuming it, or null at end of line.</summary>
    public string? PeekWord()
    {
        int start = Position;
        SkipSpaces();
        int wordStart = Position;
        while (!AtEnd && DslSyntax.IsWordChar(Current))
        {
            Position++;
        }

        string word = _text[wordStart..Position];
        Position = start;
        return word.Length == 0 ? null : word;
    }

    /// <summary>A maximal run of word characters. Errors when there is none.</summary>
    public string ReadWord(string what)
    {
        SkipSpaces();
        int start = Position;
        while (!AtEnd && DslSyntax.IsWordChar(Current))
        {
            Position++;
        }

        if (Position == start)
        {
            throw Error(AtEnd ? $"expected {what}" : $"expected {what} but found '{Current}'");
        }

        return _text[start..Position];
    }

    /// <summary>A bare word or a quoted string, whichever is next.</summary>
    public string ReadWordOrQuoted(string what)
    {
        SkipSpaces();
        return !AtEnd && Current == '"' ? ReadQuoted(what) : ReadWord(what);
    }

    /// <summary>A JSON-style string literal, decoded. The cursor must be on the opening quote.</summary>
    public string ReadQuoted(string what)
    {
        SkipSpaces();
        if (AtEnd || Current != '"')
        {
            throw Error(AtEnd ? $"expected {what} in double quotes" : $"expected {what} in double quotes but found '{Current}'");
        }

        int start = Position;
        Position++;
        var value = new StringBuilder();
        while (true)
        {
            if (AtEnd)
            {
                throw new DslParseException(Line, start + 1, $"unterminated string for {what}");
            }

            char c = Current;
            Position++;
            if (c == '"')
            {
                return value.ToString();
            }

            if (c != '\\')
            {
                value.Append(c);
                continue;
            }

            if (AtEnd)
            {
                throw new DslParseException(Line, start + 1, $"unterminated string for {what}");
            }

            char escape = Current;
            Position++;
            switch (escape)
            {
                case '"':
                case '\\':
                case '/':
                    value.Append(escape);
                    break;
                case 'b':
                    value.Append('\b');
                    break;
                case 'f':
                    value.Append('\f');
                    break;
                case 'n':
                    value.Append('\n');
                    break;
                case 'r':
                    value.Append('\r');
                    break;
                case 't':
                    value.Append('\t');
                    break;
                case 'u':
                    if (Position + 4 > _text.Length || !int.TryParse(_text.AsSpan(Position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                    {
                        throw new DslParseException(Line, Position - 1, "\\u must be followed by four hex digits");
                    }

                    value.Append((char)code);
                    Position += 4;
                    break;
                default:
                    throw new DslParseException(Line, Position - 1, $"unknown escape '\\{escape}'; use \\\" \\\\ \\n \\t \\r or \\uXXXX");
            }
        }
    }

    /// <summary>A balanced bracket group, returned with its brackets. Nested brackets and quoted strings inside are respected.</summary>
    public string ReadGroup(char open, char close, string what)
    {
        SkipSpaces();
        if (AtEnd || Current != open)
        {
            throw Error(AtEnd ? $"expected '{open}' to start {what}" : $"expected '{open}' to start {what} but found '{Current}'");
        }

        int start = Position;
        int depth = 0;
        bool inQuote = false;
        while (!AtEnd)
        {
            char c = Current;
            Position++;
            if (inQuote)
            {
                if (c == '\\' && !AtEnd)
                {
                    Position++;
                }
                else if (c == '"')
                {
                    inQuote = false;
                }
            }
            else if (c == '"')
            {
                inQuote = true;
            }
            else if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return _text[start..Position];
                }
            }
        }

        throw new DslParseException(Line, start + 1, $"missing '{close}' to end {what}");
    }

    /// <summary>Errors if anything but whitespace remains.</summary>
    public void ExpectEnd()
    {
        SkipSpaces();
        if (!AtEnd)
        {
            throw Error($"unexpected '{Current}' at the end of the line");
        }
    }

    public DslParseException Error(string message)
    {
        return new DslParseException(Line, Column, message);
    }

    public DslParseException ErrorAt(int column, string message)
    {
        return new DslParseException(Line, column, message);
    }
}
