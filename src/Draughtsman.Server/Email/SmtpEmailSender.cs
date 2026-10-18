using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Draughtsman.Server.Email;

/// <summary>
/// Sends through an SMTP server with the framework's <see cref="SmtpClient"/> (no dependency). It speaks explicit TLS (STARTTLS, normally port 587) and, with <c>email.smtp.tls: starttls</c>, will not
/// send a password or a message on a connection the server will not upgrade. It does not do implicit TLS on 465. Every
/// failure becomes an <see cref="EmailSendException"/> that says what failed (could not connect, certificate refused, password
/// rejected, recipient refused, timed out), so the admin's test button can be precise; the SMTP password is read at the
/// moment of sending, never cached, and is stripped from any text that leaves here.
/// </summary>
public sealed class SmtpEmailSender(EmailSettings settings, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!settings.Configured || settings.From is null || settings.Host is null)
        {
            throw new EmailSendException("Outgoing email is not configured: " + string.Join(' ', settings.Problems));
        }

        string? password = settings.ReadPassword();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            using MailMessage mail = Build(settings.From, message, settings.ReplyTo);
            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.StartTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Timeout = (int)settings.Timeout.TotalMilliseconds,
            };
            if (settings.User is not null)
            {
                client.Credentials = new NetworkCredential(settings.User, password);
            }

            await client.SendMailAsync(mail, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmailSendException(TimedOut($"{settings.Host}:{settings.Port.ToString(CultureInfo.InvariantCulture)}", settings));
        }
        catch (Exception error) when (error is SmtpException or FormatException or InvalidOperationException or IOException or AuthenticationException or SocketException)
        {
            string described = Describe(error, settings, password);
            // The reason is ours, not the server's raw reply: it names the failure and carries no credential or message text.
            logger.LogWarning("Sending email through {Host}:{Port} failed: {Reason}", settings.Host, settings.Port, described);
            throw new EmailSendException(described, error);
        }
    }

    public static MailMessage Build(MailAddress from, EmailMessage message, MailAddress? replyTo = null)
    {
        if (!EmailAddresses.IsPlainAddress(message.To))
        {
            throw new EmailSendException("The recipient is not a plain email address (name@host), so nothing was sent.");
        }

        var mail = new MailMessage
        {
            From = from,
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = false,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
            // Quoted-printable rather than the framework's base64 for UTF-8: mostly-ASCII text stays readable in a raw message, and plain-text mail that is
            // wholly base64 is a pattern some spam filters mark down. A long line (the link) is soft-wrapped and decoded by every mail client.
            BodyTransferEncoding = System.Net.Mime.TransferEncoding.QuotedPrintable,
        };
        mail.To.Add(new MailAddress(message.To));
        if (replyTo is not null)
        {
            mail.ReplyToList.Add(replyTo);
        }

        return mail;
    }

    /// <summary>
    /// What went wrong, in a sentence an administrator can act on. Looks at the whole exception chain (the framework wraps a
    /// refused connection or certificate in a generic "Failure sending mail"), and removes <paramref name="password"/> from
    /// anything it quotes, so even a server that echoed it back could not put it on the page.
    /// </summary>
    internal static string Describe(Exception error, EmailSettings settings, string? password)
    {
        string where = $"{settings.Host}:{settings.Port.ToString(CultureInfo.InvariantCulture)}";
        string text = Classify(error, where, settings);
        if (!string.IsNullOrEmpty(password))
        {
            text = text.Replace(password, "[redacted]", StringComparison.Ordinal);
        }

        return Clean(text);
    }

    private static string Classify(Exception error, string where, EmailSettings settings)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case TimeoutException:
                    return TimedOut(where, settings);
                case SmtpFailedRecipientException recipient:
                    return $"The mail server at {where} refused the recipient address ({(int)recipient.StatusCode} {recipient.StatusCode}).";
                case AuthenticationException tls:
                    return $"The secure connection (STARTTLS) to {where} failed: {TlsReason(tls)}";
                case SocketException socket:
                    return $"Could not connect to {where}: {socket.SocketErrorCode}. Check the server name and port, and that this machine may reach it.";
                case FormatException:
                    return "A mail address is not valid, so nothing was sent. Check the From and Reply-to addresses.";
            }
        }

        if (error is SmtpException smtp)
        {
            string detail = Innermost(smtp);
            if (detail.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return TimedOut(where, settings);
            }

            return smtp.StatusCode switch
            {
                SmtpStatusCode.ClientNotPermitted => $"The mail server at {where} rejected the user name or password ({(int)smtp.StatusCode}).",
                // 530 is both "authentication required" and "STARTTLS first"; the server's own wording says which. The framework carries on unauthenticated after
                // a refused sign-in, so a wrong password shows up here, at the first command that needs one.
                SmtpStatusCode.MustIssueStartTlsFirst when detail.Contains("auth", StringComparison.OrdinalIgnoreCase) =>
                    $"The mail server at {where} did not accept the user name or password: it asks for authentication and the sign-in was refused ({(int)smtp.StatusCode}).",
                SmtpStatusCode.MustIssueStartTlsFirst when !settings.StartTls => $"The mail server at {where} requires STARTTLS first, and email.smtp.tls is not \"starttls\".",
                SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.MailboxBusy => $"The mail server at {where} could not deliver to that address ({(int)smtp.StatusCode} {smtp.StatusCode}). If the From address is not one the server allows, use a verified sender address.",
                SmtpStatusCode.GeneralFailure when detail.Contains("secure connection", StringComparison.OrdinalIgnoreCase) || detail.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase) =>
                    $"The mail server at {where} did not offer or accept STARTTLS. Use a port that offers it (usually 587), or turn encryption off only for a server on this machine.",
                // The server's own reply text is deliberately not repeated: it is text an arbitrary host chose, and an administrator can point this at any host.
                _ => $"The mail server at {where} refused the message ({(int)smtp.StatusCode} {smtp.StatusCode}).",
            };
        }

        if (error is IOException)
        {
            return $"The connection to {where} was closed while sending. The server may not speak SMTP on that port, or may require a different encryption setting.";
        }

        return $"Sending failed ({error.GetType().Name}); see the server log.";
    }

    private static string TimedOut(string where, EmailSettings settings)
    {
        return $"The mail server at {where} did not answer within {settings.Timeout.TotalSeconds:0} second{(settings.Timeout.TotalSeconds == 1 ? string.Empty : "s")}.";
    }

    /// <summary>Why the TLS handshake failed, as one of a few fixed sentences. The framework's own text can quote what the server presented, so it is not repeated.</summary>
    private static string TlsReason(Exception error)
    {
        string text = Innermost(error);
        if (text.Contains("RemoteCertificateNameMismatch", StringComparison.OrdinalIgnoreCase) || text.Contains("name mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return "the server's certificate is for a different name than the one entered.";
        }

        if (text.Contains("UntrustedRoot", StringComparison.OrdinalIgnoreCase) || text.Contains("PartialChain", StringComparison.OrdinalIgnoreCase) || text.Contains("not trusted", StringComparison.OrdinalIgnoreCase))
        {
            return "the server's certificate is not trusted by this machine.";
        }

        if (text.Contains("expired", StringComparison.OrdinalIgnoreCase) || text.Contains("NotTimeValid", StringComparison.OrdinalIgnoreCase))
        {
            return "the server's certificate has expired.";
        }

        return "the certificate or the encryption settings were refused.";
    }

    private static string Innermost(Exception error)
    {
        Exception current = error;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }

    /// <summary>One line, control characters out, capped: it ends up in a response and the log.</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, 400));
        foreach (char c in text)
        {
            if (builder.Length >= 400)
            {
                break;
            }

            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString().Trim();
    }
}
