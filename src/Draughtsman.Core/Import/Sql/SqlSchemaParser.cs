using System.Text;

namespace Draughtsman.Core.Import.Sql;

/// <summary>
/// A tolerant reader for the DDL that describes a schema: <c>CREATE TABLE</c> and the <c>ALTER TABLE ... ADD</c> forms
/// that add a key. It understands enough of T-SQL, PostgreSQL, MySQL and SQLite to find each table's columns, primary
/// key, unique constraints and foreign keys, and skips everything else safely: column modifiers it has no use for
/// (defaults, collations, identity, checks, storage clauses, ...) are stepped over with their parentheses, strings and
/// <c>::</c> casts intact; any statement it does not read becomes a warning that names it and its line. It never throws.
/// </summary>
internal static class SqlSchemaParser
{
    public static SqlSchema Parse(string source, SqlDialect hint)
    {
        SqlDialect dialect = hint == SqlDialect.Auto ? SqlDialects.Detect(source) : hint;
        var schema = new SqlSchema();
        List<SqlToken> tokens = SqlTokenizer.Tokenize(source, dialect, schema.Comments);
        foreach ((int start, int end) in Split(tokens, dialect))
        {
            Interpret(schema, new SqlCursor(tokens, start, end));
        }

        return schema;
    }

    #region Statements

    /// <summary>
    /// Cuts the token stream into statements at <c>;</c>, at a <c>GO</c> line, and where a line starts with a word that
    /// can only begin a new statement (<c>CREATE</c>, <c>ALTER</c>, <c>DROP</c>, and a few more when nothing is open),
    /// because SSMS output and hand-written scripts often have no terminator at all. A T-SQL procedure, function or
    /// trigger runs to the next <c>GO</c>, since its body holds semicolons of its own.
    /// </summary>
    private static List<(int Start, int End)> Split(List<SqlToken> tokens, SqlDialect dialect)
    {
        var statements = new List<(int, int)>();
        int start = 0;
        int depth = 0;
        bool procedural = false;
        bool alterTable = false;

        for (int i = 0; i < tokens.Count; i++)
        {
            SqlToken token = tokens[i];
            if (IsBatchSeparator(tokens, i))
            {
                Close(statements, start, i);
                if (i + 1 < tokens.Count && tokens[i + 1].Kind == SqlTokenKind.Number && tokens[i + 1].Line == token.Line)
                {
                    i++;
                }

                start = i + 1;
                depth = 0;
                procedural = false;
                alterTable = false;
                continue;
            }

            if (i == start)
            {
                procedural = dialect == SqlDialect.TSql && StartsProcedure(tokens, i);
                alterTable = StartsAlterTable(tokens, i);
            }
            else if (!procedural && token.LineStart && StartsStatement(tokens, i, depth, alterTable))
            {
                Close(statements, start, i);
                start = i;
                depth = 0;
                procedural = dialect == SqlDialect.TSql && StartsProcedure(tokens, i);
                alterTable = StartsAlterTable(tokens, i);
            }

            if (token.IsSymbol("("))
            {
                depth++;
            }
            else if (token.IsSymbol(")") && depth > 0)
            {
                depth--;
            }
            else if (token.IsSymbol(";") && !procedural)
            {
                Close(statements, start, i);
                start = i + 1;
                depth = 0;
                alterTable = false;
            }
        }

        Close(statements, start, tokens.Count);
        return statements;
    }

    private static void Close(List<(int, int)> statements, int start, int end)
    {
        if (end > start)
        {
            statements.Add((start, end));
        }
    }

    /// <summary>A <c>GO</c> alone on its line (optionally with a repeat count) is a batch separator, not a statement.</summary>
    private static bool IsBatchSeparator(List<SqlToken> tokens, int i)
    {
        SqlToken token = tokens[i];
        if (!token.IsWord("go") || !token.LineStart)
        {
            return false;
        }

        int next = i + 1;
        if (next < tokens.Count && tokens[next].Kind == SqlTokenKind.Number && tokens[next].Line == token.Line)
        {
            next++;
        }

        return next >= tokens.Count || tokens[next].Line != token.Line;
    }

    private static bool StartsAlterTable(List<SqlToken> tokens, int i)
    {
        return tokens[i].IsWord("alter") && i + 1 < tokens.Count && tokens[i + 1].IsWord("table");
    }

    private static bool StartsProcedure(List<SqlToken> tokens, int i)
    {
        if (!tokens[i].IsWord("create") && !tokens[i].IsWord("alter"))
        {
            return false;
        }

        int j = i + 1;
        while (j < tokens.Count && (tokens[j].IsWord("or") || tokens[j].IsWord("alter") || tokens[j].IsWord("replace")))
        {
            j++;
        }

        return j < tokens.Count && (tokens[j].IsWord("proc") || tokens[j].IsWord("procedure") || tokens[j].IsWord("function") || tokens[j].IsWord("trigger"));
    }

    /// <summary>
    /// Whether the word that opens a line starts a new statement. <c>CREATE</c>, <c>ALTER</c> and <c>DROP</c> do even
    /// inside an unclosed parenthesis (so one broken statement cannot swallow the rest); the rest only when nothing is
    /// open, and never as the tail of <c>ON DELETE</c>, <c>ON UPDATE</c>, <c>ON DELETE SET NULL</c> or <c>CREATE OR ALTER</c>.
    /// </summary>
    private static bool StartsStatement(List<SqlToken> tokens, int i, int depth, bool inAlterTable)
    {
        SqlToken token = tokens[i];
        SqlToken previous = tokens[i - 1];
        if (token.IsWord("create"))
        {
            return true;
        }

        // ALTER TABLE t / DROP CONSTRAINT x / ALTER COLUMN y: the later lines continue the statement.
        if (inAlterTable)
        {
            return false;
        }

        if (token.IsWord("drop"))
        {
            return true;
        }

        if (token.IsWord("alter"))
        {
            return !previous.IsWord("or");
        }

        if (depth > 0)
        {
            return false;
        }

        if (token.IsWord("update") || token.IsWord("delete"))
        {
            return !previous.IsWord("on");
        }

        if (token.IsWord("set"))
        {
            return !previous.IsWord("delete") && !previous.IsWord("update");
        }

        if (token.IsWord("comment"))
        {
            return i + 1 < tokens.Count && tokens[i + 1].IsWord("on");
        }

        return token.IsWord("insert") || token.IsWord("use") || token.IsWord("grant") || token.IsWord("revoke") || token.IsWord("exec")
            || token.IsWord("execute") || token.IsWord("truncate") || token.IsWord("commit") || token.IsWord("rollback")
            || token.IsWord("pragma") || token.IsWord("delimiter") || token.IsWord("declare") || token.IsWord("print");
    }

    private static void Interpret(SqlSchema schema, SqlCursor c)
    {
        int line = c.Line;
        if (c.TryWord("create"))
        {
            bool temporary = false;
            bool unique = false;
            while (true)
            {
                if (c.TryWord("or"))
                {
                    if (!c.TryWord("replace"))
                    {
                        c.TryWord("alter");
                    }
                }
                else if (c.TryWord("temp") || c.TryWord("temporary"))
                {
                    temporary = true;
                }
                else if (c.TryWord("unique"))
                {
                    unique = true;
                }
                else if (!(c.TryWord("global") || c.TryWord("local") || c.TryWord("unlogged") || c.TryWord("external") || c.TryWord("virtual")
                    || c.TryWord("clustered") || c.TryWord("nonclustered")))
                {
                    break;
                }
            }

            if (!unique && c.TryWord("table"))
            {
                CreateTable(schema, c, line, temporary);
                return;
            }

            // Only a unique index says anything about the schema (it can make a foreign key one-to-one); the rest fall through to the skipped-statement warning.
            if (unique && c.TryWord("index"))
            {
                CreateUniqueIndex(schema, c, line);
                return;
            }
        }
        else if (c.IsWord("alter") && c.IsWord("table", 1))
        {
            c.Skip(2);
            AlterTable(schema, c, line);
            return;
        }

        Warn(schema, ImportWarningCodes.UnsupportedSyntax, $"Skipped \"{c.Describe()}\": only CREATE TABLE and ALTER TABLE ... ADD carry schema.", line);
    }

    private static void Warn(SqlSchema schema, string code, string message, int line)
    {
        schema.Warnings.Add(new ImportWarning(code, message, line));
    }

    #endregion

    #region CREATE TABLE and ALTER TABLE

    private static void CreateTable(SqlSchema schema, SqlCursor c, int line, bool temporary)
    {
        c.TryWords("if", "not", "exists");
        SqlName? name = c.ReadName();
        if (name is null)
        {
            Warn(schema, ImportWarningCodes.ParseError, "Skipped a CREATE TABLE statement that has no table name.", line);
            return;
        }

        if (temporary || name.Value.Name.StartsWith('#'))
        {
            Warn(schema, ImportWarningCodes.UnsupportedSyntax, $"Skipped temporary table {name.Value.Display}: it is not part of the schema.", line);
            return;
        }

        if (!c.Peek().IsSymbol("("))
        {
            Warn(schema, ImportWarningCodes.UnsupportedSyntax, $"Skipped \"CREATE TABLE {name.Value.Display}\": it has no column list (AS SELECT, LIKE and PARTITION OF are not read).", line);
            return;
        }

        if (schema.Find(name.Value) is not null)
        {
            Warn(schema, ImportWarningCodes.DuplicateTable, $"Table {name.Value.Display} is defined more than once; the first definition was kept.", line);
            return;
        }

        int close = c.MatchingClose(c.Pos);
        c.Skip();
        var table = new SqlTable(name.Value, line) { EndLine = c.LineAt(Math.Min(close, c.End - 1)) };
        foreach (SqlCursor item in c.Slice(c.Pos, close).SplitItems())
        {
            ParseTableItem(schema, table, item);
        }

        schema.TryAdd(table);
    }

    /// <summary>
    /// <c>CREATE UNIQUE [CLUSTERED | NONCLUSTERED] INDEX [IF NOT EXISTS] [CONCURRENTLY] name ON [ONLY] table [USING m] (cols)
    /// [INCLUDE (...)] [WHERE ...]</c>: its columns become one of the table's unique keys. Only a plain column list counts
    /// (an expression such as <c>lower(email)</c> is not a column), and a filtered index counts only when its filter is
    /// <c>col IS NOT NULL</c>, the usual way to let a nullable column repeat NULL; any other filter leaves rows outside
    /// the index, so it proves nothing about the relationship. Each of those cases is a warning, never an error.
    /// </summary>
    private static void CreateUniqueIndex(SqlSchema schema, SqlCursor c, int line)
    {
        c.TryWords("if", "not", "exists");
        c.TryWord("concurrently");
        if (!c.IsWord("on"))
        {
            c.ReadName();
        }

        if (!c.TryWord("on"))
        {
            Warn(schema, ImportWarningCodes.ParseError, $"Could not read \"{c.Describe()}\": no ON clause, so the unique index was skipped.", line);
            return;
        }

        c.TryWord("only");
        SqlName? name = c.ReadName();
        SqlTable? table = name is null ? null : schema.Find(name.Value);
        if (table is null)
        {
            Warn(schema, ImportWarningCodes.MissingReference, $"A unique index names {name?.Display ?? "a table"}, which is not defined in the source; it was not read.", line);
            return;
        }

        if (c.TryWord("using"))
        {
            c.Skip();
        }

        if (!c.Peek().IsSymbol("(") || !IsPlainKeyList(c))
        {
            Warn(schema, ImportWarningCodes.UnsupportedSyntax, $"Skipped a unique index on {table.Name.Display}: its key is not a plain list of columns.", line);
            return;
        }

        List<string> columns = c.ReadNameList();
        if (c.TryWord("include"))
        {
            c.SkipGroup();
        }

        if (c.TryWord("where") && !IsNotNullFilter(c))
        {
            Warn(schema, ImportWarningCodes.UnsupportedSyntax, $"Skipped a filtered unique index on {table.Name.Display} ({string.Join(", ", columns)}): it covers only some rows, so it does not make a relationship one-to-one.", line);
            return;
        }

        if (columns.Count > 0)
        {
            table.UniqueKeys.Add(columns);
        }
    }

    /// <summary>Whether the parenthesised list at the cursor holds only columns: each item a name, optionally with a sort order, a prefix length or an operator class.</summary>
    private static bool IsPlainKeyList(SqlCursor c)
    {
        SqlCursor inner = c.Slice(c.Pos + 1, c.MatchingClose(c.Pos));
        List<SqlCursor> items = inner.SplitItems();
        foreach (SqlCursor item in items)
        {
            if (!item.Peek().IsName)
            {
                return false;
            }

            item.Skip();
            if (item.Peek().IsSymbol("(") && item.Peek(1).Kind == SqlTokenKind.Number && item.Peek(2).IsSymbol(")"))
            {
                item.Skip(3);
            }

            while (!item.Done)
            {
                if (item.Next().Kind == SqlTokenKind.Symbol)
                {
                    return false;
                }
            }
        }

        return items.Count > 0;
    }

    /// <summary>True when everything up to WITH / ON / the end is <c>col IS NOT NULL</c> clauses joined by AND (parentheses allowed).</summary>
    private static bool IsNotNullFilter(SqlCursor c)
    {
        bool sawClause = false;
        while (!c.Done && !c.IsWord("with") && !c.IsWord("on"))
        {
            SqlToken token = c.Peek();
            if (token.IsSymbol("(") || token.IsSymbol(")") || token.IsWord("and"))
            {
                c.Skip();
            }
            else if (token.IsName && !token.IsWord("is") && !token.IsWord("not") && !token.IsWord("null") && c.IsWord("is", 1) && c.IsWord("not", 2) && c.IsWord("null", 3))
            {
                c.Skip(4);
                sawClause = true;
            }
            else
            {
                return false;
            }
        }

        return sawClause;
    }

    private static void AlterTable(SqlSchema schema, SqlCursor c, int line)
    {
        c.TryWords("if", "exists");
        c.TryWord("only");
        SqlName? name = c.ReadName();
        if (name is null)
        {
            Warn(schema, ImportWarningCodes.ParseError, "Skipped an ALTER TABLE statement that has no table name.", line);
            return;
        }

        if (c.TryWord("with"))
        {
            if (!c.TryWord("check"))
            {
                c.TryWord("nocheck");
            }
        }

        // SSMS follows each ADD FOREIGN KEY with "ALTER TABLE t CHECK CONSTRAINT fk": it changes no shape, so it is not worth a warning.
        if ((c.IsWord("check") || c.IsWord("nocheck")) && c.IsWord("constraint", 1))
        {
            return;
        }

        if (!c.TryWord("add"))
        {
            Warn(schema, ImportWarningCodes.UnsupportedSyntax, $"Skipped \"{c.Describe()}\": only ALTER TABLE ... ADD is read.", line);
            return;
        }

        SqlTable? table = schema.Find(name.Value);
        if (table is null)
        {
            Warn(schema, ImportWarningCodes.MissingReference, $"ALTER TABLE names {name.Value.Display}, which is not defined in the source; its changes were not read.", line);
            return;
        }

        foreach (SqlCursor item in c.SplitItems())
        {
            item.TryWord("add");
            if (item.TryWord("column"))
            {
                item.TryWords("if", "not", "exists");
            }

            ParseTableItem(schema, table, item);
        }
    }

    /// <summary>One entry of a table body: a constraint, an index the schema does not need, or a column.</summary>
    private static void ParseTableItem(SqlSchema schema, SqlTable table, SqlCursor c)
    {
        if (c.Done)
        {
            return;
        }

        if (c.TryWord("constraint"))
        {
            if (c.Peek().IsName)
            {
                c.Skip();
            }

            // CONSTRAINT DF_x DEFAULT (0) FOR col, and any other constraint kind the schema does not carry.
            if (!(c.IsWord("primary") || c.IsWord("unique") || c.IsWord("foreign") || c.IsWord("check") || c.IsWord("exclude")))
            {
                return;
            }
        }

        if (c.IsWord("primary") && c.IsWord("key", 1))
        {
            c.Skip(2);
            SkipNames(c);
            List<string> columns = c.ReadNameList();
            if (columns.Count > 0)
            {
                table.PrimaryKey.Clear();
                table.PrimaryKey.AddRange(columns);
            }

            return;
        }

        if (c.IsWord("unique"))
        {
            c.Skip();
            if (!c.TryWord("key"))
            {
                c.TryWord("index");
            }

            SkipNames(c);
            List<string> columns = c.ReadNameList();
            if (columns.Count > 0)
            {
                table.UniqueKeys.Add(columns);
            }

            return;
        }

        if (c.IsWord("foreign") && c.IsWord("key", 1))
        {
            c.Skip(2);
            SkipNames(c);
            List<string> columns = c.ReadNameList();
            if (columns.Count > 0 && c.TryWord("references") && ReadReference(c) is { } reference)
            {
                table.ForeignKeys.Add(new SqlForeignKey(columns, reference.Table, reference.Columns, c.Line));
            }
            else
            {
                Warn(schema, ImportWarningCodes.ParseError, $"Could not read a FOREIGN KEY on {table.Name.Display}; it was skipped.", c.Line);
            }

            return;
        }

        if (c.IsWord("check") || c.IsWord("exclude") || c.IsWord("period") || c.IsWord("like") || IsIndexItem(c))
        {
            return;
        }

        ParseColumn(schema, table, c);
    }

    /// <summary>MySQL's <c>KEY name (cols)</c>, <c>INDEX (cols)</c> and <c>FULLTEXT KEY ...</c>: indexes, not columns.</summary>
    private static bool IsIndexItem(SqlCursor c)
    {
        if ((c.IsWord("fulltext") || c.IsWord("spatial")) && (c.IsWord("key", 1) || c.IsWord("index", 1)))
        {
            return true;
        }

        if (!c.IsWord("key") && !c.IsWord("index"))
        {
            return false;
        }

        return c.Peek(1).IsSymbol("(") || (c.Peek(1).IsName && c.Peek(2).IsSymbol("("));
    }

    /// <summary>Steps over the names, and index-type words such as CLUSTERED, that sit between a constraint keyword and its column list.</summary>
    private static void SkipNames(SqlCursor c)
    {
        while (c.Peek().IsName)
        {
            c.Skip();
        }
    }

    private sealed record Reference(SqlName Table, List<string> Columns);

    /// <summary>After REFERENCES: the target table and, when written, its columns; no column list means the target's primary key.</summary>
    private static Reference? ReadReference(SqlCursor c)
    {
        SqlName? table = c.ReadName();
        if (table is null)
        {
            return null;
        }

        return new Reference(table.Value, c.ReadNameList());
    }

    #endregion

    #region Columns

    private static void ParseColumn(SqlSchema schema, SqlTable table, SqlCursor c)
    {
        int line = c.Line;
        if (!c.Peek().IsName)
        {
            Warn(schema, ImportWarningCodes.ParseError, $"Could not read a column of {table.Name.Display} (\"{c.Describe()}\"); it was skipped.", line);
            return;
        }

        string name = c.Next().Text;

        // T-SQL computed column: "Total AS (Price * Qty) PERSISTED", which has no type of its own.
        string type = c.IsWord("as") ? string.Empty : ReadType(c);
        var column = new SqlColumn(name, type) { Line = line, EndLine = c.LastLine };
        if (type.StartsWith("serial", StringComparison.OrdinalIgnoreCase) || type.StartsWith("bigserial", StringComparison.OrdinalIgnoreCase) || type.StartsWith("smallserial", StringComparison.OrdinalIgnoreCase))
        {
            column.Generated = true;
        }

        while (!c.Done)
        {
            SqlToken token = c.Peek();
            if (token.Kind != SqlTokenKind.Word)
            {
                if (token.IsSymbol("("))
                {
                    c.SkipGroup();
                }
                else
                {
                    c.Skip();
                }

                continue;
            }

            if (token.IsWord("not") && c.IsWord("null", 1))
            {
                column.NotNull = true;
                c.Skip(2);
            }
            else if (token.IsWord("null"))
            {
                column.NotNull ??= false;
                c.Skip();
            }
            else if (token.IsWord("primary") && c.IsWord("key", 1))
            {
                c.Skip(2);
                table.PrimaryKey.Add(name);
            }
            else if (token.IsWord("unique"))
            {
                c.Skip();
                c.TryWord("key");
                table.UniqueKeys.Add([name]);
            }
            else if (token.IsWord("references"))
            {
                c.Skip();
                if (ReadReference(c) is { } reference)
                {
                    table.ForeignKeys.Add(new SqlForeignKey([name], reference.Table, reference.Columns, line));
                }
            }
            else if (token.IsWord("constraint"))
            {
                c.Skip();
                if (c.Peek().IsName && !IsColumnClause(c.Peek()))
                {
                    c.Skip();
                }
            }
            else if (token.IsWord("default"))
            {
                c.Skip();
                SkipDefault(c);
            }
            else if (token.IsWord("collate"))
            {
                c.Skip();
                c.ReadName();
            }
            else if (token.IsWord("identity") || token.IsWord("auto_increment") || token.IsWord("autoincrement"))
            {
                column.Generated = true;
                c.Skip();
                c.SkipGroup();
            }
            else if (token.IsWord("generated"))
            {
                c.Skip();
                while (!c.Done && !c.IsWord("as"))
                {
                    c.Skip();
                }

                c.TryWord("as");
                if (c.TryWord("identity"))
                {
                    column.Generated = true;
                }

                c.SkipGroup();
            }
            else if (token.IsWord("on"))
            {
                SkipOn(c);
            }
            else if (token.IsWord("comment"))
            {
                c.Skip(2);
            }
            else if (token.IsWord("character") && c.IsWord("set", 1))
            {
                c.Skip(3);
            }
            else if (token.IsWord("charset"))
            {
                c.Skip(2);
            }
            else
            {
                c.Skip();
                c.SkipGroup();
            }
        }

        table.Columns.Add(column);
    }

    /// <summary>The words that open a column clause, so a column with no type (SQLite allows that) is not misread as having one.</summary>
    private static bool IsColumnClause(SqlToken token)
    {
        if (token.Kind != SqlTokenKind.Word)
        {
            return false;
        }

        return token.Text.ToLowerInvariant() is "not" or "null" or "primary" or "unique" or "references" or "constraint" or "default"
            or "collate" or "check" or "identity" or "generated" or "auto_increment" or "autoincrement" or "comment" or "on" or "key"
            or "sparse" or "rowguidcol" or "filestream";
    }

    /// <summary>
    /// The data type as a display string: <c>nvarchar(50)</c>, <c>numeric(10, 2)</c>, <c>nvarchar(max)</c>, <c>double
    /// precision</c>, <c>timestamp(3) with time zone</c>, <c>integer[]</c>, <c>dbo.MyType</c>. Empty when the next token
    /// already starts a column clause.
    /// </summary>
    private static string ReadType(SqlCursor c)
    {
        SqlToken first = c.Peek();
        if (!first.IsName || IsColumnClause(first))
        {
            return string.Empty;
        }

        var text = new StringBuilder(c.Next().Text);
        while (c.Peek().IsSymbol(".") && c.Peek(1).IsName)
        {
            text.Append('.').Append(c.Peek(1).Text);
            c.Skip(2);
        }

        while (!c.Done)
        {
            SqlToken token = c.Peek();
            if (token.IsSymbol("("))
            {
                text.Append(ReadArguments(c));
            }
            else if (token.IsSymbol("["))
            {
                text.Append(ReadArrayBound(c));
            }
            else if (token.Kind == SqlTokenKind.Word && IsTypeContinuation(token.Text, text.ToString()))
            {
                text.Append(' ').Append(token.Text);
                c.Skip();
            }
            else if ((token.IsWord("with") || token.IsWord("without")) && c.IsWord("time", 1) && c.IsWord("zone", 2))
            {
                text.Append(' ').Append(token.Text).Append(" time zone");
                c.Skip(3);
            }
            else
            {
                break;
            }
        }

        return text.ToString();
    }

    private static bool IsTypeContinuation(string word, string typeSoFar)
    {
        string lower = word.ToLowerInvariant();
        if (lower is "precision" or "varying" or "unsigned" or "signed" or "zerofill")
        {
            return true;
        }

        return typeSoFar.Equals("national", StringComparison.OrdinalIgnoreCase) && lower is "character" or "char";
    }

    /// <summary>A type's argument list as <c>(10, 2)</c>, <c>(max)</c> or <c>('a', 'b')</c>, its own spacing normalised.</summary>
    private static string ReadArguments(SqlCursor c)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        SqlToken? previous = null;
        while (!c.Done)
        {
            SqlToken token = c.Next();
            if (token.IsSymbol("("))
            {
                depth++;
                if (depth == 1)
                {
                    continue;
                }
            }
            else if (token.IsSymbol(")"))
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
            else if (token.IsSymbol(",") && depth == 1)
            {
                items.Add(current.ToString());
                current.Clear();
                previous = null;
                continue;
            }

            string piece = token.Kind == SqlTokenKind.String ? $"'{token.Text.Replace("'", "''", StringComparison.Ordinal)}'" : token.Text;
            if (previous is { } prior && IsWordLike(prior) && IsWordLike(token))
            {
                current.Append(' ');
            }

            current.Append(piece);
            previous = token;
        }

        items.Add(current.ToString());
        return $"({string.Join(", ", items)})";
    }

    private static bool IsWordLike(SqlToken token)
    {
        return token.Kind is SqlTokenKind.Word or SqlTokenKind.Number or SqlTokenKind.Quoted;
    }

    /// <summary>An array type's bound: <c>[]</c> or <c>[3]</c>.</summary>
    private static string ReadArrayBound(SqlCursor c)
    {
        var text = new StringBuilder("[");
        c.Skip();
        while (!c.Done)
        {
            SqlToken token = c.Next();
            if (token.IsSymbol("]"))
            {
                break;
            }

            text.Append(token.Text);
        }

        return text.Append(']').ToString();
    }

    /// <summary>
    /// Steps over one DEFAULT expression: a literal, a name or a call (<c>getdate()</c>, <c>nextval('s')</c>), a
    /// parenthesised expression, with any <c>::type</c> casts and arithmetic after it. It stops at the next clause word
    /// so <c>DEFAULT 0 NOT NULL</c> still sees the NOT NULL, and <c>DEFAULT NULL</c> is not a nullability clause.
    /// </summary>
    private static void SkipDefault(SqlCursor c)
    {
        SkipPrimary(c);
        while (!c.Done)
        {
            SqlToken token = c.Peek();
            if (token.IsSymbol("::"))
            {
                c.Skip();
                ReadType(c);
            }
            else if (token.Kind == SqlTokenKind.Symbol && token.Text is "+" or "-" or "*" or "/" or "|" or "%" or "&" or "^")
            {
                c.Skip();
                SkipPrimary(c);
            }
            else
            {
                break;
            }
        }
    }

    private static void SkipPrimary(SqlCursor c)
    {
        while (c.Peek().IsSymbol("+") || c.Peek().IsSymbol("-"))
        {
            c.Skip();
        }

        SqlToken token = c.Peek();
        if (token.IsSymbol("("))
        {
            c.SkipGroup();
            return;
        }

        if (token.Kind == SqlTokenKind.Symbol)
        {
            return;
        }

        c.Skip();
        if (token.IsWord("next") && c.IsWord("value") && c.IsWord("for", 1))
        {
            c.Skip(2);
            c.ReadName();
        }

        while (c.Peek().IsSymbol(".") && c.Peek(1).IsName)
        {
            c.Skip(2);
        }

        c.SkipGroup();
        c.SkipBrackets();
    }

    /// <summary>ON DELETE / ON UPDATE actions, MySQL's ON UPDATE CURRENT_TIMESTAMP, SQLite's ON CONFLICT and T-SQL's ON [PRIMARY].</summary>
    private static void SkipOn(SqlCursor c)
    {
        c.Skip();
        if (c.TryWord("delete") || c.TryWord("update"))
        {
            if (c.TryWord("set"))
            {
                c.Skip();
                c.SkipGroup();
            }
            else if (c.TryWord("no"))
            {
                c.Skip();
            }
            else if (!c.Done)
            {
                c.Skip();
                c.SkipGroup();
            }

            return;
        }

        c.TryWord("conflict");
        if (!c.Done)
        {
            c.Skip();
        }
    }

    #endregion
}
