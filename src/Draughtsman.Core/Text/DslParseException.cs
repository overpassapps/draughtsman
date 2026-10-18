namespace Draughtsman.Core.Text;

/// <summary>A syntax error in DSL text. Line and column are 1-based and point at the offending token.</summary>
public sealed class DslParseException : FormatException
{
    public DslParseException(int line, int column, string message)
        : base($"line {line}, column {column}: {message}")
    {
        Line = line;
        Column = column;
        Detail = message;
    }

    public int Line { get; }

    public int Column { get; }

    /// <summary>The message without the position prefix.</summary>
    public string Detail { get; }
}
