namespace Draughtsman.Server.Email;

/// <summary>A plain-text message. Plain text only: nothing in it is ever interpreted as markup.</summary>
public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>
/// Which recipient addresses a message may be sent to. The framework's address parser is forgiving: it reads
/// <c>victim@example.com\r\nBcc: attacker@example.com</c> as a display name followed by the address after it, so the mail would go
/// to the attacker. An address here must be exactly a bare <c>name@host</c>: no display name, no whitespace or control character,
/// and the parser must hand back the text it was given.
/// </summary>
public static class EmailAddresses
{
    public static bool IsPlainAddress(string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > 254 || address.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or '"' or ',' or ';'))
        {
            return false;
        }

        return System.Net.Mail.MailAddress.TryCreate(address, out System.Net.Mail.MailAddress? parsed)
            && string.IsNullOrEmpty(parsed.DisplayName)
            && string.Equals(parsed.Address, address, StringComparison.Ordinal);
    }
}

public interface IEmailSender
{
    /// <summary>Hands <paramref name="message"/> on. Throws <see cref="EmailSendException"/> when it could not be, with text safe to show an administrator.</summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>The message could not be sent. The text says what failed and never carries a credential, so it is safe to store and show.</summary>
public sealed class EmailSendException(string message, Exception? inner = null) : Exception(message, inner);
