using System.Net;
using System.Net.Mail;

namespace Draughtsman.Server.Email;

/// <summary>
/// What each outgoing-email field may hold, in one place so draughtsman.yaml, the admin page and the test button all refuse the same
/// things. Every rule returns a sentence for an administrator (never the rejected value, which could be a secret or an injection
/// attempt) or null when the value is fine. The two jobs: nothing that reaches an SMTP command or a message header may carry a line
/// break (an address or user name with <c>\r\n</c> in it is how a header is forged), and a host is a host: no URL, no user info, no path.
/// </summary>
public static class EmailFieldRules
{
    public const int MaxHost = 253;

    /// <summary>A display name plus an address: the address alone is capped at 254, the name gets a little room.</summary>
    public const int MaxSender = 320;

    public const int MaxUser = 256;

    public const int MaxPassword = 512;

    public const int MaxPublicUrl = 2048;

    public static string? HostProblem(string host)
    {
        if (host.Length > MaxHost)
        {
            return $"The mail server name is too long (at most {MaxHost} characters).";
        }

        if (host.Any(char.IsControl) || host.Any(char.IsWhiteSpace))
        {
            return "The mail server must be a plain host name or address, with no spaces or line breaks.";
        }

        if (host.Contains("://", StringComparison.Ordinal) || host.Any(c => c is '/' or '\\' or '@' or '?' or '#' or '[' or ']' or '%' or '<' or '>' or '"' or '\'' or ','))
        {
            return "The mail server must be a plain host name such as smtp.example.com, not a web address. The port has its own box.";
        }

        if (host.Contains(':', StringComparison.Ordinal))
        {
            return IPAddress.TryParse(host, out IPAddress? v6) && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? null
                : "Enter only the host name or address; the port has its own box.";
        }

        if (!host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            return "The mail server may contain only letters, digits, dots and dashes (write an international name in its xn-- form).";
        }

        // A DNS name: labels of 1 to 63 characters that neither start nor end with a dash (one trailing dot is the root, and fine).
        string[] labels = host.TrimEnd('.').Split('.');
        if (labels.Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'))
        {
            return "That is not a valid host name or address.";
        }

        return Uri.CheckHostName(host) == UriHostNameType.Unknown ? "That is not a valid host name or address." : null;
    }

    public static string? PortProblem(int port)
    {
        return port is < 1 or > 65535 ? "The port must be a number from 1 to 65535." : null;
    }

    /// <summary>
    /// A From or Reply-To value: <c>name@host</c> or <c>Display Name &lt;name@host&gt;</c>, one address, no control characters anywhere.
    /// The framework's parser alone is too forgiving (it reads a second address after a line break as the real one).
    /// </summary>
    public static string? SenderProblem(string value, string label, out MailAddress? address)
    {
        address = null;
        if (value.Length > MaxSender)
        {
            return $"{label} is too long (at most {MaxSender} characters).";
        }

        if (value.Any(char.IsControl))
        {
            return $"{label} must be on one line: no line breaks or other control characters.";
        }

        // One address, and nothing after it: the parser reads "a@b.test, c@d.test" and "Name <a@b.test> junk" as the first address and drops the rest.
        if (value.Any(c => c is ',' or ';')
            || !MailAddress.TryCreate(value, out MailAddress? parsed)
            || !EmailAddresses.IsPlainAddress(parsed.Address)
            || !(value == parsed.Address || value.EndsWith("<" + parsed.Address + ">", StringComparison.Ordinal)))
        {
            return $"{label} is not an email address. Use name@example.com or Name <name@example.com>.";
        }

        address = parsed;
        return null;
    }

    public static string? UserProblem(string user)
    {
        if (user.Length > MaxUser)
        {
            return $"The user name is too long (at most {MaxUser} characters).";
        }

        return user.Any(char.IsControl) ? "The user name must be on one line: no line breaks or other control characters." : null;
    }

    public static string? PasswordProblem(string password)
    {
        if (password.Length == 0)
        {
            return "Enter the password.";
        }

        if (password.Length > MaxPassword)
        {
            return $"The password is too long (at most {MaxPassword} characters).";
        }

        return password.Any(char.IsControl) ? "The password must be on one line: no line breaks or other control characters." : null;
    }

    public static string? PublicUrlProblem(string url)
    {
        if (url.Length > MaxPublicUrl || url.Any(char.IsControl) || url.Any(char.IsWhiteSpace))
        {
            return "The public address must be a single web address with no spaces or line breaks.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return "The public address must be an http or https address with no user name, query or fragment, for example https://draughtsman.example.com.";
        }

        return null;
    }
}
