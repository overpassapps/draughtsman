using System.Globalization;

namespace Draughtsman.Core.Import.Drawio;

/// <summary>
/// A draw.io style: one string of semicolon-separated parts, each a bare name (<c>ellipse</c>, <c>swimlane</c>) or a
/// <c>key=value</c> pair. A later pair for the same key replaces an earlier one, as draw.io does. Read in one pass with no regular expression.
/// </summary>
internal sealed class DrawioStyle
{
    private static readonly DrawioStyle s_empty = new(null, [], []);

    private readonly Dictionary<string, string> _pairs;
    private readonly HashSet<string> _tokens;

    private DrawioStyle(string? name, Dictionary<string, string> pairs, HashSet<string> tokens)
    {
        Name = name;
        _pairs = pairs;
        _tokens = tokens;
    }

    /// <summary>The first bare word: the base style draw.io applies (<c>ellipse</c>, <c>rhombus</c>, <c>swimlane</c>, <c>text</c>), or null when there is none.</summary>
    public string? Name { get; }

    public static DrawioStyle Parse(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return s_empty;
        }

        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? name = null;
        int start = 0;
        while (start <= style.Length)
        {
            int end = style.IndexOf(';', start);
            if (end < 0)
            {
                end = style.Length;
            }

            ReadOnlySpan<char> part = style.AsSpan(start, end - start).Trim();
            int equals = part.IndexOf('=');
            if (equals > 0)
            {
                pairs[part[..equals].Trim().ToString()] = part[(equals + 1)..].Trim().ToString();
            }
            else if (equals < 0 && !part.IsEmpty)
            {
                string token = part.ToString();
                tokens.Add(token);
                name ??= token;
            }

            start = end + 1;
        }

        return new DrawioStyle(name, pairs, tokens);
    }

    /// <summary>The value of <paramref name="key"/>, or null when the style does not set it.</summary>
    public string? Get(string key)
    {
        return _pairs.GetValueOrDefault(key);
    }

    public bool Has(string key)
    {
        return _pairs.ContainsKey(key);
    }

    /// <summary>True when the style has the bare word <paramref name="token"/> (not a key of that name).</summary>
    public bool HasToken(string token)
    {
        return _tokens.Contains(token);
    }

    /// <summary>On for <c>1</c> or <c>true</c>; off for anything else, including a key that is not there.</summary>
    public bool Flag(string key)
    {
        string? value = Get(key);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The value as a finite number (invariant culture), or null when it is missing, not a number, or infinite.</summary>
    public double? Number(string key)
    {
        return double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
    }
}
