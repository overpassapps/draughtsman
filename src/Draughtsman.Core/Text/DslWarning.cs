namespace Draughtsman.Core.Text;

/// <summary>A non-fatal problem found while parsing: an unknown kind, a reference to an undeclared node or port.</summary>
public sealed record DslWarning(int Line, int Column, string Message)
{
    public override string ToString()
    {
        return $"line {Line}, column {Column}: {Message}";
    }
}
