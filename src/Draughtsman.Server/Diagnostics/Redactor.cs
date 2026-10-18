using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Draughtsman.Core;

namespace Draughtsman.Server.Diagnostics;

/// <summary>
/// Takes secrets and personal data out of text that is going to leave the machine inside a support bundle. It is deliberately a
/// sledgehammer: it would rather remove a harmless value than leave a secret, because the person who sends the bundle reads it first
/// and the cost of a missing word is a question, while the cost of a leaked password is not. Applied to a line at a time, in this order:
/// <list type="number">
/// <item>whole-line rules: an <c>Authorization</c>, <c>Cookie</c> or <c>Set-Cookie</c> header, anything after "setup code", a private-key block header;</item>
/// <item>a value after a secret-looking name (<c>password</c>, <c>token</c>, <c>secret</c>, <c>apiKey</c>, <c>session</c>, <c>xsrf</c>, <c>connectionString</c>...) and a
/// <c>:</c>, <c>=</c>, "is" or "was": the rest of the line goes, because a password can hold spaces and a JSON value can hold commas;</item>
/// <item><c>Bearer</c> and <c>Basic</c> credentials, and the user and password inside a URL;</item>
/// <item>email addresses, kept as the first character and the domain (<c>a***@example.com</c>);</item>
/// <item>the shapes secrets have: Draughtsman's own API-token and reset-link prefixes, other vendors' key prefixes, JWTs, any long run that looks
/// random (many changes between upper case, lower case and digits), a long hex string, and a ten-character code of the shape the first-run setup code has;</item>
/// <item>network addresses, cut to their /24 (IPv4) or /64 (IPv6); loopback and the unspecified address are left alone.</item>
/// </list>
/// Every expression runs in time linear in the line (the non-backtracking engine), and if one still times out the line is withheld rather than kept. The
/// result is idempotent: redacting redacted text changes nothing, which the bundle relies on to check its own work. Free text can always hold a secret
/// no rule can see (a password in a sentence); the bundle's README says so and tells the sender to read the files before sending.
/// </summary>
public static class Redactor
{
    public const string Redacted = "[redacted]";

    public const string Masked = "[masked]";

    public const string Withheld = "[line withheld: redaction timed out]";

    /// <summary>Names that mean a value follows that must not leave the machine. Matched as part of a longer name too (<c>DRAUGHTSMAN_SMTP_PASSWORD</c>, <c>smtp.password</c>, <c>X-Api-Key</c>).</summary>
    private const string SecretNames =
        "password|passwd|pwd|passphrase|secret|token|apikey|api[_-]?key|private[_-]?key|access[_-]?key|credential|connection[_-]?string|"
        + "authorization|session|xsrf|csrf|antiforgery|cookie|setup[_-]?code|bearer|signature|salt|hash|"
        + "(?:ai|api|auth|license|licence|encryption|signing|master|secret|access|shared)[ _-]?key";

    // -- The file logger's line: time, level, category, then the message. The category is a type name from this code (it holds words like Cookie and Session), not text a caller sent.
    private static readonly Regex s_logPrefix = RegexLimits.Linear(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z [A-Z]{3} [A-Za-z0-9_.]+: )(.*)$");

    // -- Content a person typed that the product itself writes into a log line: a document's title in "Security event document.deleted", an API token's name in `token tok_x "CI deploy"`.
    private static readonly Regex s_documentEvent = RegexLimits.Linear(@"\bSecurity event (document\.[a-z_]+):.*$");
    private static readonly Regex s_tokenName = RegexLimits.Linear(@"\b(token [A-Za-z0-9_\-]+ )""[^""]*""");

    // -- Whole-line rules.
    private static readonly Regex s_headerLine = RegexLimits.Linear(@"\b(authorization|proxy-authorization|cookie|set-cookie|x-api-key|x-xsrf-token|x-csrf-token)\b[ \t""']*[:=].*$", RegexOptions.IgnoreCase);
    private static readonly Regex s_setupCode = RegexLimits.Linear(@"\bsetup[ _-]?code\b.*$", RegexOptions.IgnoreCase);
    private static readonly Regex s_privateKey = RegexLimits.Linear(@"-----BEGIN [A-Z ]*(?:PRIVATE KEY|CERTIFICATE|PGP|ENCRYPTED)[A-Z ]*-----.*$", RegexOptions.IgnoreCase);

    // -- A secret-looking name, an optional closing quote, a separator, then the rest of the line.
    private static readonly Regex s_nameValue = RegexLimits.Linear(@"([A-Za-z0-9_.\-]*(?:" + SecretNames + @")[A-Za-z0-9_.\-]*)([""']?[ \t]*[:=][ \t]*)(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex s_nameIs = RegexLimits.Linear(@"\b((?:" + SecretNames + @")s?)([ \t]+(?:is|was|are|were)[ \t]+)(.*)$", RegexOptions.IgnoreCase);

    // -- Credentials by scheme and inside a URL.
    private static readonly Regex s_bearer = RegexLimits.Linear(@"\b(bearer|basic|digest|negotiate)([ \t]+)\S+", RegexOptions.IgnoreCase);
    private static readonly Regex s_urlUserInfo = RegexLimits.Linear(@"://[^/\s@?#]+@");

    // -- Personal data.
    private static readonly Regex s_email = RegexLimits.Linear(@"([A-Za-z0-9._%+\-]+)@([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,})");

    // -- The shapes secrets have.
    private static readonly Regex s_prefixed = RegexLimits.Linear(@"\b(?:(?:dst|dpr|sk|pk|rk|ghp|gho|ghu|ghs|ghr)[_\-]|xox[abprs]-)[A-Za-z0-9_\-]{12,}|\b(?:AKIA|ASIA)[0-9A-Z]{12,}|\bAIza[A-Za-z0-9_\-]{20,}");
    private static readonly Regex s_jwt = RegexLimits.Linear(@"\beyJ[A-Za-z0-9_\-]{3,}\.[A-Za-z0-9_\-]{3,}(?:\.[A-Za-z0-9_\-]*)?");
    private static readonly Regex s_dotted = RegexLimits.Linear(@"[A-Za-z0-9_\-]{6,}(?:\.[A-Za-z0-9_\-]{6,}){2,}");
    private static readonly Regex s_longRun = RegexLimits.Linear(@"[A-Za-z0-9+/_=\-]{20,}");
    private static readonly Regex s_hex = RegexLimits.Linear(@"\b[0-9A-Fa-f]{32,}\b");
    // The compact timestamps Draughtsman puts in the names of backups and pre-migration copies (20261006T120000Z, 20261006-120000): not part of what makes a name look random.
    private static readonly Regex s_timestamp = RegexLimits.Linear(@"\d{8}T\d{6}Z|\d{8}-\d{6}");
    private static readonly Regex s_hexShort = RegexLimits.Linear(@"\b[0-9A-Fa-f]{20,31}\b");
    private static readonly Regex s_setupShaped = RegexLimits.Linear(@"\b[A-HJKMNP-Z2-9]{10}\b");

    // -- Addresses.
    private static readonly Regex s_ipv4 = RegexLimits.Linear(@"\b(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?\b");
    private static readonly Regex s_ipv6 = RegexLimits.Linear(@"(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:/\d{1,3})?");

    /// <summary>
    /// Redacts a text of any size, a line at a time. <paramref name="truncateAddresses"/> is on for everything that may hold the address of a
    /// person and off for a settings listing, where an address is an operator's own (a trusted proxy) and must stay exact to be of any use.
    /// </summary>
    public static string Redact(string? text, bool truncateAddresses = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = new StringBuilder(text.Length);
        int start = 0;
        while (start <= text.Length)
        {
            int newline = text.IndexOf('\n', start);
            string line = newline < 0 ? text[start..] : text[start..newline];
            bool carriage = line.EndsWith('\r');
            result.Append(RedactLine(carriage ? line[..^1] : line, truncateAddresses));
            if (carriage)
            {
                result.Append('\r');
            }

            if (newline < 0)
            {
                break;
            }

            result.Append('\n');
            start = newline + 1;
        }

        return result.ToString();
    }

    /// <summary>Redacts one line (no line break in it).</summary>
    public static string RedactLine(string line, bool truncateAddresses = true)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length == 0)
        {
            return line;
        }

        try
        {
            Match prefix = s_logPrefix.Match(line);
            return prefix.Success ? prefix.Groups[1].Value + RedactMessage(prefix.Groups[2].Value, truncateAddresses) : RedactMessage(line, truncateAddresses);
        }
        catch (RegexMatchTimeoutException)
        {
            return Withheld;
        }
    }

    private static string RedactMessage(string line, bool truncateAddresses)
    {
        try
        {
            string text = line;
            text = s_documentEvent.Replace(text, "Security event $1: [document title and details withheld]");
            text = s_tokenName.Replace(text, "$1\"[name withheld]\"");
            text = s_headerLine.Replace(text, m => m.Groups[1].Value + ": " + Redacted);
            text = s_setupCode.Replace(text, "setup code " + Redacted);
            text = s_privateKey.Replace(text, "-----BEGIN KEY BLOCK----- " + Redacted);
            text = ReplaceNameValues(text);
            text = s_nameIs.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
            text = s_bearer.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
            text = s_urlUserInfo.Replace(text, "://" + Redacted + "@");
            text = s_email.Replace(text, MaskEmail);
            text = s_prefixed.Replace(text, Masked);
            text = s_jwt.Replace(text, Masked);
            // Three or more long dotted pieces with a digit in them: a JWT or a handwritten one. (A type name without a digit is not touched.)
            text = s_dotted.Replace(text, m => HasDigit(m.Value) ? Masked : m.Value);
            text = s_longRun.Replace(text, MaskRandomRun);
            text = s_hex.Replace(text, Masked);
            text = s_hexShort.Replace(text, m => IsShortHexToken(m.Value) ? Masked : m.Value);
            text = s_setupShaped.Replace(text, m => HasDigit(m.Value) && HasLetter(m.Value) ? Masked : m.Value);
            if (truncateAddresses)
            {
                text = s_ipv4.Replace(text, TruncateIPv4);
                string before = text;
                text = s_ipv6.Replace(before, m => TruncateIPv6(m, before));
            }

            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            return Withheld;
        }
    }

    /// <summary>
    /// A secret-looking name and a separator redact the rest of the line, except where the name is one of Draughtsman's own event names. That match
    /// also swallowed the rest of the line, so the rest is searched again: <c>Security event token.created: token=abc</c> keeps the event and loses the token.
    /// </summary>
    private static string ReplaceNameValues(string text)
    {
        return s_nameValue.Replace(text, m => IsEventName(m.Groups[1].Value)
            ? m.Groups[1].Value + m.Groups[2].Value + ReplaceNameValues(m.Groups[3].Value)
            : IsPlaceholder(m.Groups[3].Value) ? m.Value : m.Groups[1].Value + m.Groups[2].Value + Redacted);
    }

    /// <summary>An email address as its first character and its domain: <c>eric@example.com</c> becomes <c>e***@example.com</c>.</summary>
    public static string MaskEmail(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        int at = address.LastIndexOf('@');
        return at <= 0 ? address : address[0] + "***" + address[at..];
    }

    /// <summary>A network address cut to its prefix: IPv4 to its /24 (<c>203.0.113.0/24</c>), IPv6 to its /64. Loopback, the unspecified address and anything that is not an address come back unchanged.</summary>
    public static string TruncateAddress(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        string text = address.Trim();
        if (text.Contains('/', StringComparison.Ordinal) || !IPAddress.TryParse(text, out IPAddress? parsed))
        {
            return address;
        }

        return Truncate(parsed) ?? address;
    }

    /// <summary>The words the settings listing writes in place of a hidden value: <c>(set)</c>, <c>(not set)</c>, <c>(set, 2 entries)</c>, <c>(set; host example.com)</c>. They carry nothing, and removing them would hide the one fact the listing exists to give.</summary>
    private static bool IsPlaceholder(string value)
    {
        string text = value.Trim();
        return text == "(not set)" || (text.StartsWith("(set", StringComparison.Ordinal) && text.EndsWith(')') && text.Length <= 48 && !text.AsSpan(1, text.Length - 2).ContainsAny('(', ')', '\n'));
    }

    /// <summary>The names of Draughtsman's own security events (<c>token.created</c>, <c>password.changed</c>) contain a secret-looking word and are followed by a colon in the log; they are fixed words from a list, never a value, and a log with them blanked says nothing.</summary>
    private static bool IsEventName(string name)
    {
        return s_eventNames.Contains(name);
    }

    private static readonly HashSet<string> s_eventNames = new(
        typeof(Draughtsman.Core.Auth.SecurityEventTypes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!),
        StringComparer.Ordinal);

    private static string MaskEmail(Match match)
    {
        return MaskEmail(match.Value);
    }

    private static string TruncateIPv4(Match match)
    {
        // Already a prefix (a CIDR range, from an earlier pass or a setting): nothing to cut.
        if (match.Value.Contains('/', StringComparison.Ordinal))
        {
            return match.Value;
        }

        string[] octets = match.Value.Split('.');
        foreach (string octet in octets)
        {
            if (!int.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value > 255 || (octet.Length > 1 && octet[0] == '0'))
            {
                return match.Value; // a version number, not an address
            }
        }

        return IPAddress.TryParse(match.Value, out IPAddress? parsed) ? Truncate(parsed) ?? match.Value : match.Value;
    }

    private static string TruncateIPv6(Match match, string text)
    {
        if (match.Value.Contains('/', StringComparison.Ordinal))
        {
            return match.Value;
        }

        // Text like std::string or a label such as "Retry::now" is not an address: a real one is not stuck to a letter or digit on either side.
        if ((match.Index > 0 && char.IsAsciiLetterOrDigit(text[match.Index - 1])) || (match.Index + match.Length < text.Length && char.IsAsciiLetterOrDigit(text[match.Index + match.Length])))
        {
            return match.Value;
        }

        // A candidate is only an address if it parses as one: a time (09:00:00), a MAC address or a label like "Message:" never does.
        string value = match.Value;
        string trailing = string.Empty;
        // A trailing colon is punctuation after the address ("from ::1: refused"), not part of it, unless the address ends with "::".
        if (value.EndsWith(':') && !value.EndsWith("::", StringComparison.Ordinal))
        {
            trailing = ":";
            value = value[..^1];
        }

        return IPAddress.TryParse(value, out IPAddress? parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6 ? (Truncate(parsed) ?? value) + trailing : match.Value;
    }

    private static string? Truncate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return $"{bytes[0].ToString(CultureInfo.InvariantCulture)}.{bytes[1].ToString(CultureInfo.InvariantCulture)}.{bytes[2].ToString(CultureInfo.InvariantCulture)}.0/24";
        }

        var groups = new List<string>();
        for (int i = 0; i < 8; i += 2)
        {
            groups.Add(((bytes[i] << 8) | bytes[i + 1]).ToString("x", CultureInfo.InvariantCulture));
        }

        return string.Join(':', groups) + "::/64";
    }

    /// <summary>
    /// True for a run that looks random rather than written: at least 16 letters and digits, a digit and a letter among them, and (counting only the letters and
    /// digits, so a path's slashes and a file name's hyphens do not count) a change between upper case, lower case and digit in 18 positions in a hundred or more.
    /// A random token changes class about three positions in five (hex about one in two), a path with a digit in it one in ten, a timestamp one in seven. Measured
    /// on random strings, essentially no random token of 22 or more characters that holds a digit is missed; some long camel-case names that hold a digit are caught too. A run
    /// of letters with no digit is never called random: the odds a real 43-character token holds no digit are about one in eight thousand, while a long identifier
    /// with none is common. A migration id (<c>20260930101500_AddPasswordResetTokens</c>) is one of the lines the bundle exists to show, so it is left alone.
    /// </summary>
    internal static bool LooksRandom(string run)
    {
        if (IsMigrationId(run))
        {
            return false;
        }

        run = s_timestamp.Replace(run, string.Empty);
        int count = 0;
        int changes = 0;
        int previous = -1;
        bool digit = false;
        bool letter = false;
        foreach (char c in run)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                continue;
            }

            int kind = char.IsAsciiLetterUpper(c) ? 0 : char.IsAsciiLetterLower(c) ? 1 : 2;
            digit |= kind == 2;
            letter |= kind != 2;
            if (previous >= 0 && kind != previous)
            {
                changes++;
            }

            previous = kind;
            count++;
        }

        return count >= 16 && digit && letter && changes * 100 >= (count - 1) * 18;
    }

    /// <summary>
    /// A run that looks random is masked whole. One that does not as a whole may still hold a random token between slashes (<c>/api/reset/Zk3...Q/confirm</c>:
    /// the plain words around it dilute the change count), so each slash-separated piece is judged on its own and only a piece that looks random is masked.
    /// </summary>
    private static string MaskRandomRun(Match match)
    {
        string run = match.Value;
        if (LooksRandom(run))
        {
            return Masked;
        }

        if (!run.Contains('/', StringComparison.Ordinal))
        {
            return run;
        }

        return string.Join('/', run.Split('/').Select(piece => LooksRandom(piece) ? Masked : piece));
    }

    /// <summary>A hex run of 20 to 31 characters (a 96-bit or 120-bit value) with both a digit and a letter in it; 32 or more is caught whole by the hex rule.</summary>
    private static bool IsShortHexToken(string value)
    {
        return HasDigit(value) && value.Any(char.IsAsciiLetter);
    }

    private static bool IsMigrationId(string run)
    {
        return run.Length > 16 && run[..14].All(char.IsAsciiDigit) && run[14] == '_' && run[15..].All(char.IsAsciiLetterOrDigit);
    }

    private static bool HasDigit(string value)
    {
        return value.Any(char.IsAsciiDigit);
    }

    private static bool HasLetter(string value)
    {
        return value.Any(char.IsAsciiLetter);
    }
}
