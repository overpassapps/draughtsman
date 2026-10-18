using System.Text.Json;

namespace Draughtsman.Core.Serialization;

/// <summary>
/// JSON numbers by their text. A document can spell a number with 400 digits or an exponent of 99999999999, and the schema's limits (<c>minimum 0</c>,
/// <c>maximum 600000</c>, <c>exclusiveMinimum 0</c>) have to hold for every spelling, so comparison is exact on the digits rather than through
/// <see cref="double"/> or <see cref="decimal"/>. The two judgements that are not simply "the mathematical answer" are kept because they are what
/// the schema library this replaced said (docs/adr/0004-document-schema-validator.md, pinned by the corpus): <see cref="IsInteger"/> and <see cref="SameValue"/>.
/// </summary>
internal static class JsonNumberText
{
    /// <summary>
    /// Whether a number is an integer: judged from its spelling, as the library did. <c>1</c>, <c>1.0</c>, <c>1e2</c>, <c>0.1e1</c> and <c>1.0e-0</c> are;
    /// <c>1.5</c> and <c>1.25e1</c> are not; and <c>10e-1</c> and <c>100e-2</c> are not either although they are 1, because a spelling with a negative
    /// exponent and no decimal point is never one. Every fraction digit of a spelling with a negative exponent has to be zero (<c>1.0e-5</c> counts).
    /// </summary>
    public static bool IsInteger(JsonElement number)
    {
        if (number.TryGetInt64(out _))
        {
            return true;
        }

        string text = number.GetRawText();
        int start = text[0] == '-' ? 1 : 0;
        int mantissaEnd = text.AsSpan().IndexOfAny('e', 'E');
        if (mantissaEnd < 0)
        {
            mantissaEnd = text.Length;
        }

        long exponent = mantissaEnd < text.Length ? ParseExponent(text.AsSpan(mantissaEnd + 1)) : 0;
        int point = text.IndexOf('.', start, mantissaEnd - start);
        if (point < 0)
        {
            return exponent >= 0;
        }

        int fractionDigits = mantissaEnd - point - 1;
        if (exponent >= fractionDigits)
        {
            return true;
        }

        for (int i = point + 1 + (int)Math.Max(exponent, 0); i < mantissaEnd; i++)
        {
            if (text[i] != '0')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether two numbers are the same value, as <c>const</c> and <c>enum</c> need. Both are read as <see cref="decimal"/> when they fit, as the library
    /// did, so a spelling with more than 28 significant digits equals the number it rounds to; a number outside the range of <see cref="decimal"/>
    /// (which made the library throw) is compared exactly.
    /// </summary>
    public static bool SameValue(JsonElement a, JsonElement b)
    {
        return a.TryGetDecimal(out decimal x) && b.TryGetDecimal(out decimal y) ? x == y : Compare(Parse(a), Parse(b)) == 0;
    }

    /// <summary>The exact comparison of a number with a limit: negative when it is below, zero when equal, positive when above.</summary>
    public static int Compare(JsonElement number, in Limit limit)
    {
        return limit.Integer is { } whole && number.TryGetInt64(out long value) ? value.CompareTo(whole) : Compare(Parse(number), limit.Parsed);
    }

    public static Limit ReadLimit(JsonElement limit)
    {
        return new Limit(limit.GetRawText(), limit.TryGetInt64(out long whole) ? whole : null, Parse(limit));
    }

    /// <summary>A number a schema keyword compares with, read once: its own text (for messages), its value when it is a plain 64-bit integer, and its digits.</summary>
    internal readonly record struct Limit(string Text, long? Integer, Parsed Parsed);

    /// <summary>A number as sign, significant digits (no leading or trailing zeros; empty for zero) and the decimal exponent of the first digit.</summary>
    internal readonly record struct Parsed(bool Negative, string Digits, long Exponent)
    {
        public bool IsZero => Digits.Length == 0;
    }

    private static Parsed Parse(JsonElement number)
    {
        string text = number.GetRawText();
        int start = text[0] == '-' ? 1 : 0;
        int mantissaEnd = text.AsSpan().IndexOfAny('e', 'E');
        if (mantissaEnd < 0)
        {
            mantissaEnd = text.Length;
        }

        long exponent = mantissaEnd < text.Length ? ParseExponent(text.AsSpan(mantissaEnd + 1)) : 0;
        int point = text.IndexOf('.', start, mantissaEnd - start);
        int integerDigits = (point < 0 ? mantissaEnd : point) - start;
        string digits = point < 0 ? text[start..mantissaEnd] : string.Concat(text.AsSpan(start, point - start), text.AsSpan(point + 1, mantissaEnd - point - 1));

        int first = 0;
        while (first < digits.Length && digits[first] == '0')
        {
            first++;
        }

        if (first == digits.Length)
        {
            return new Parsed(false, string.Empty, 0);
        }

        int last = digits.Length - 1;
        while (digits[last] == '0')
        {
            last--;
        }

        return new Parsed(start == 1, digits[first..(last + 1)], integerDigits - 1 - first + exponent);
    }

    /// <summary>The value of the digits after the <c>e</c>, saturating rather than overflowing for spellings no real number needs.</summary>
    private static long ParseExponent(ReadOnlySpan<char> text)
    {
        bool negative = text.Length > 0 && text[0] == '-';
        long value = 0;
        foreach (char c in text)
        {
            if (c is >= '0' and <= '9')
            {
                value = Math.Min((value * 10) + (c - '0'), 1_000_000_000_000_000L);
            }
        }

        return negative ? -value : value;
    }

    private static int Compare(Parsed a, Parsed b)
    {
        if (a.IsZero || b.IsZero)
        {
            return a.IsZero && b.IsZero ? 0 : a.IsZero ? (b.Negative ? 1 : -1) : (a.Negative ? -1 : 1);
        }

        if (a.Negative != b.Negative)
        {
            return a.Negative ? -1 : 1;
        }

        int magnitude = a.Exponent != b.Exponent ? a.Exponent.CompareTo(b.Exponent) : string.CompareOrdinal(a.Digits, b.Digits);
        return a.Negative ? -magnitude : magnitude;
    }
}
