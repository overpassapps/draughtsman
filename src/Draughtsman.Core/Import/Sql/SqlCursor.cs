using System.Text;

namespace Draughtsman.Core.Import.Sql;

/// <summary>A read position over a slice of the token list: one statement, or one item of a column list.</summary>
internal sealed class SqlCursor
{
    private static readonly SqlToken s_end = new(SqlTokenKind.Symbol, string.Empty, 0, false);

    private readonly IReadOnlyList<SqlToken> _tokens;

    public SqlCursor(IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        _tokens = tokens;
        Start = start;
        Pos = start;
        End = end;
    }

    /// <summary>Index of the first token of the slice; <see cref="Describe"/> reads from here however far the cursor has moved.</summary>
    public int Start { get; }

    public int Pos { get; private set; }

    public int End { get; }

    public bool Done => Pos >= End;

    /// <summary>The source line of the slice's first token.</summary>
    public int Line => Start < End ? _tokens[Start].Line : 0;

    /// <summary>The source line of the last token of the slice.</summary>
    public int LastLine => Start < End ? _tokens[End - 1].Line : 0;

    /// <summary>The source line of the token at <paramref name="index"/> of the token list (the slice's own line when out of range).</summary>
    public int LineAt(int index)
    {
        return index >= 0 && index < _tokens.Count ? _tokens[index].Line : Line;
    }

    /// <summary>The token <paramref name="offset"/> places ahead, or an empty symbol past the end of the slice.</summary>
    public SqlToken Peek(int offset = 0)
    {
        int at = Pos + offset;
        return at >= Start && at < End ? _tokens[at] : s_end;
    }

    public SqlToken Next()
    {
        SqlToken token = Peek();
        if (!Done)
        {
            Pos++;
        }

        return token;
    }

    public void Skip(int count = 1)
    {
        Pos = Math.Min(End, Pos + count);
    }

    public bool IsWord(string keyword, int offset = 0)
    {
        return Peek(offset).IsWord(keyword);
    }

    public bool TryWord(string keyword)
    {
        if (!IsWord(keyword))
        {
            return false;
        }

        Pos++;
        return true;
    }

    /// <summary>Takes the keywords only if all of them follow one another.</summary>
    public bool TryWords(params string[] keywords)
    {
        for (int i = 0; i < keywords.Length; i++)
        {
            if (!IsWord(keywords[i], i))
            {
                return false;
            }
        }

        Pos += keywords.Length;
        return true;
    }

    public bool TrySymbol(string symbol)
    {
        if (!Peek().IsSymbol(symbol))
        {
            return false;
        }

        Pos++;
        return true;
    }

    /// <summary>The index of the <c>)</c> matching the <c>(</c> at <paramref name="open"/>, or <see cref="End"/> when it never closes.</summary>
    public int MatchingClose(int open)
    {
        int depth = 0;
        for (int i = open; i < End; i++)
        {
            if (_tokens[i].IsSymbol("("))
            {
                depth++;
            }
            else if (_tokens[i].IsSymbol(")") && --depth == 0)
            {
                return i;
            }
        }

        return End;
    }

    /// <summary>When the next token is <c>(</c>, moves past its matching <c>)</c>; otherwise does nothing.</summary>
    public void SkipGroup()
    {
        if (Peek().IsSymbol("("))
        {
            Pos = Math.Min(End, MatchingClose(Pos) + 1);
        }
    }

    /// <summary>When the next token is <c>[</c>, moves past the next <c>]</c> (an array bound; they do not nest).</summary>
    public void SkipBrackets()
    {
        if (!Peek().IsSymbol("["))
        {
            return;
        }

        while (!Done && !Next().IsSymbol("]"))
        {
        }
    }

    /// <summary>A possibly qualified name (<c>dbo.Users</c>, <c>[db]..[t]</c>): its last two parts, or null when the next token is not a name.</summary>
    public SqlName? ReadName()
    {
        if (!Peek().IsName)
        {
            return null;
        }

        var parts = new List<string> { Next().Text };
        while (Peek().IsSymbol("."))
        {
            Skip();
            if (Peek().IsSymbol("."))
            {
                Skip();
            }

            if (!Peek().IsName)
            {
                break;
            }

            parts.Add(Next().Text);
        }

        return new SqlName(parts.Count > 1 ? parts[^2] : null, parts[^1]);
    }

    /// <summary>
    /// Reads <c>( a, b DESC, c(10) )</c> as the names <c>a</c>, <c>b</c>, <c>c</c>: the first token of each item, which
    /// is the column whatever sort order or prefix length follows it. Nothing is read unless the next token is <c>(</c>.
    /// </summary>
    public List<string> ReadNameList()
    {
        var names = new List<string>();
        if (!Peek().IsSymbol("("))
        {
            return names;
        }

        int close = MatchingClose(Pos);
        Skip();
        bool atItemStart = true;
        int depth = 0;
        while (Pos < close)
        {
            SqlToken token = Next();
            if (token.IsSymbol("("))
            {
                depth++;
            }
            else if (token.IsSymbol(")"))
            {
                depth--;
            }
            else if (token.IsSymbol(",") && depth == 0)
            {
                atItemStart = true;
            }
            else if (atItemStart && depth == 0)
            {
                atItemStart = false;
                if (token.IsName)
                {
                    names.Add(token.Text);
                }
            }
        }

        Pos = Math.Min(End, close + 1);
        return names;
    }

    /// <summary>A cursor over the tokens from <paramref name="start"/> to <paramref name="end"/> of the same token list.</summary>
    public SqlCursor Slice(int start, int end)
    {
        return new SqlCursor(_tokens, start, end);
    }

    /// <summary>Cuts the tokens from here to the end of the slice at the commas outside any parentheses.</summary>
    public List<SqlCursor> SplitItems()
    {
        var items = new List<SqlCursor>();
        int depth = 0;
        int itemStart = Pos;
        int end = End;
        for (int i = Pos; i < end; i++)
        {
            SqlToken token = _tokens[i];
            if (token.IsSymbol("("))
            {
                depth++;
            }
            else if (token.IsSymbol(")") && depth > 0)
            {
                depth--;
            }
            else if (token.IsSymbol(",") && depth == 0)
            {
                items.Add(new SqlCursor(_tokens, itemStart, i));
                itemStart = i + 1;
            }
        }

        if (itemStart < end)
        {
            items.Add(new SqlCursor(_tokens, itemStart, end));
        }

        return items;
    }

    /// <summary>The statement's first few words, for a warning that names it.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        for (int i = Start; i < End && i < Start + 8 && !_tokens[i].IsSymbol("(") && !_tokens[i].IsSymbol("="); i++)
        {
            // A qualified name reads as dbo.Users, not "dbo . Users".
            if (text.Length > 0 && !_tokens[i].IsSymbol(".") && !_tokens[i - 1].IsSymbol("."))
            {
                text.Append(' ');
            }

            text.Append(_tokens[i].Text);
        }

        return text.Length > 70 ? text.ToString(0, 70) + "…" : text.ToString();
    }
}
