using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Draughtsman.Server.Email;

/// <summary>
/// The development sender: writes each message to its own file under <c>email.file.folder</c> and sends nothing. What a local
/// run, a demo and the tests use, so no real email leaves by accident. A file holds the message exactly as it would have been
/// sent, link included, so the folder is as sensitive as a mailbox; the log names the file and never its contents.
/// </summary>
public sealed class FileEmailSender(EmailSettings settings, TimeProvider clock, ILogger<FileEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (settings.OutboxFolder is null || settings.From is null)
        {
            throw new EmailSendException("Outgoing email is not configured: " + string.Join(' ', settings.Problems));
        }

        if (!EmailAddresses.IsPlainAddress(message.To))
        {
            throw new EmailSendException("The recipient is not a plain email address (name@host), so nothing was written.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        string name = $"{now:yyyyMMddTHHmmssfff}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}.txt";
        string path = Path.Combine(settings.OutboxFolder, name);
        try
        {
            Directory.CreateDirectory(settings.OutboxFolder);
            var text = new StringBuilder();
            text.Append("From: ").Append(settings.From.ToString()).Append('\n');
            if (settings.ReplyTo is not null)
            {
                text.Append("Reply-To: ").Append(settings.ReplyTo.ToString()).Append('\n');
            }

            text.Append("To: ").Append(message.To).Append('\n');
            text.Append("Subject: ").Append(message.Subject).Append('\n');
            text.Append("Date: ").Append(now.ToString("R", CultureInfo.InvariantCulture)).Append("\n\n");
            text.Append(message.Body);
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new EmailSendException($"The outbox folder {settings.OutboxFolder} could not be written ({error.GetType().Name}).", error);
        }

        logger.LogInformation("Wrote an email to {File} (file provider: nothing was sent)", path);
        return Task.CompletedTask;
    }
}

/// <summary>Registered when email is not configured, so a feature that sends fails with a reason instead of a null reference.</summary>
public sealed class UnconfiguredEmailSender(EmailSettings settings) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        string why = settings.Problems.Count > 0 ? string.Join(' ', settings.Problems) : "Outgoing email has not been set up (Admin, Email, or email.provider in draughtsman.yaml).";
        throw new EmailSendException("Outgoing email is not configured: " + why);
    }
}
