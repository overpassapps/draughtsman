namespace Draughtsman.Core.Tests.SchemaCorpus;

/// <summary>
/// The few things the replacement validator deliberately says differently from the library it replaced (docs/adr/0004-document-schema-validator.md),
/// as rules. Each is a place the library <b>threw</b> or <b>overflowed</b>, never a verdict that moved from invalid to valid for a document the library
/// could judge. A difference is accepted only when a rule below names it AND it is listed, with the exact new outcome, in
/// <c>intentional-differences.jsonl</c>, so re-recording cannot hide a regression that is not one of these.
/// </summary>
internal static class IntentionalDifferences
{
    public const string DecimalRange = "decimal-range";
    public const string LoneSurrogate = "lone-surrogate";
    public const string LoneSurrogateInMemberName = "lone-surrogate-in-member-name";
    public const string ExponentOverflow = "exponent-overflow";

    /// <summary>The reason this change from <paramref name="recorded"/> to <paramref name="now"/> is intended, or null when it is not.</summary>
    public static string? Classify(CorpusOutcome recorded, CorpusOutcome now)
    {
        // A number outside the range of decimal made the library throw FormatException when it compared it with `const` or `enum`: now it is a violation.
        if (recorded is { Kind: CorpusOutcome.Throws, Exception: nameof(FormatException) } && now.Kind == CorpusOutcome.Invalid)
        {
            return DecimalRange;
        }

        // A lone surrogate (legal JSON, not legal text) made the library throw InvalidOperationException wherever it read the string or a member name as text.
        // Now a value with a string keyword is a violation, and a member name of a map that accepts any member (props, theme) is not read at all.
        if (recorded is { Kind: CorpusOutcome.Throws, Exception: nameof(InvalidOperationException) })
        {
            return now.Kind switch
            {
                CorpusOutcome.Invalid when now.Violations.Any(v => v.EndsWith("Value is not valid Unicode text", StringComparison.Ordinal) || v.Contains("\\u", StringComparison.Ordinal)) => LoneSurrogate,
                CorpusOutcome.Valid => LoneSurrogateInMemberName,
                _ => null,
            };
        }

        // The library read an exponent of ten digits or more into an int and wrapped it, so 1e1000000000000 was "number" there; it is an integer here.
        if (recorded.Kind == CorpusOutcome.Invalid && now.Kind == CorpusOutcome.Invalid && recorded.Violations.Count == now.Violations.Count
            && recorded.Violations.Zip(now.Violations, (r, n) => r == n || r.Replace("\"number\"", "\"integer\"", StringComparison.Ordinal) == n).All(same => same))
        {
            return ExponentOverflow;
        }

        return null;
    }
}
