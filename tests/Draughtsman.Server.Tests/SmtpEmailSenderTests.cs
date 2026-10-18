using System.Net.Security;
using System.Net.Sockets;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Email;
using Draughtsman.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The real <c>SmtpClient</c> against an in-process server: the path with no TLS, the STARTTLS path (against a self-signed
/// certificate the test alone trusts), and each way a mail server fails. What is pinned: the password never crosses the wire in the
/// clear, a server that will not do STARTTLS is refused before anything is sent, and every failure comes back as a sentence that
/// says what happened and never contains the password.
/// </summary>
[Collection(SmtpCollection.Name)]
public class SmtpEmailSenderTests
{
    private const string Password = "s3cret-smtp-password";

    // A generous default: the sender gives up after this long, and a machine under load must not turn a send to an in-process server into a timeout.
    private static EmailSettings Settings(int port, string tls, string? user = null, string? password = null, int timeoutSeconds = 60, string host = "127.0.0.1")
    {
        var config = new EmailConfig
        {
            Provider = EmailConfig.ProviderSmtp,
            From = "Draughtsman <noreply@draughtsman.test>",
            Smtp = new SmtpConfig { Host = host, Port = port, Tls = tls, User = user ?? string.Empty, TimeoutSeconds = timeoutSeconds },
        };
        return new EmailSettings(config, new ServerConfig(), Path.GetTempPath(), name => name == config.Smtp.PasswordEnvironmentVariable ? password : null);
    }

    private static SmtpEmailSender Sender(EmailSettings settings)
    {
        return new SmtpEmailSender(settings, NullLogger<SmtpEmailSender>.Instance);
    }

    private static readonly EmailMessage s_message = new("person@example.com", "Reset your Draughtsman password", "Open https://draughtsman.test/reset-password#token=dpr_abc\n\nBye.\n");

    [Fact]
    public async Task Without_TLS_a_message_reaches_a_relay_that_needs_no_sign_in()
    {
        await using var server = new TestSmtpServer();
        server.Start();

        await Sender(Settings(server.Port, SmtpConfig.TlsNone)).SendAsync(s_message, CancellationToken.None);

        ReceivedMail mail = Assert.Single(server.Mail);
        Assert.Equal("noreply@draughtsman.test", mail.From);
        Assert.Equal(["person@example.com"], mail.To);
        Assert.False(mail.OverTls);
        Assert.Contains("Subject: Reset your Draughtsman password", mail.Data, StringComparison.Ordinal);
        Assert.Contains("From: \"Draughtsman\" <noreply@draughtsman.test>", mail.Data, StringComparison.Ordinal);
        Assert.Contains("Open https://draughtsman.test/reset-password#token=dpr_abc", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Log, line => line.Contains("STARTTLS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_STARTTLS_the_connection_is_upgraded_before_the_password_is_sent_and_the_message_travels_encrypted()
    {
        await using var server = new TestSmtpServer(offerStartTls: true, password: Password);
        server.Start();
        string expected = server.Certificate!.GetCertHashString();

        using (TrustOnly(expected))
        {
            await Sender(Settings(server.Port, SmtpConfig.TlsStartTls, "mailer", Password)).SendAsync(s_message, CancellationToken.None);
        }

        ReceivedMail mail = Assert.Single(server.Mail);
        Assert.True(mail.OverTls);
        Assert.True(server.SawAuthOverTls);
        Assert.False(server.SawAuthInTheClear);
        Assert.Contains(server.Log, line => line == "clear:STARTTLS");
        // EHLO again over TLS, then auth, then the envelope: nothing sensitive before the upgrade.
        int starttls = server.Log.ToList().IndexOf("clear:STARTTLS");
        Assert.DoesNotContain(server.Log.Take(starttls), line => line.Contains("AUTH", StringComparison.OrdinalIgnoreCase) || line.Contains("MAIL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_certificate_the_system_does_not_trust_stops_the_send_before_the_password_leaves()
    {
        await using var server = new TestSmtpServer(offerStartTls: true, password: Password);
        server.Start();

        EmailSendException error = await Assert.ThrowsAsync<EmailSendException>(() => Sender(Settings(server.Port, SmtpConfig.TlsStartTls, "mailer", Password)).SendAsync(s_message, CancellationToken.None));

        Assert.Contains("secure connection (STARTTLS)", error.Message, StringComparison.Ordinal);
        Assert.Contains($"127.0.0.1:{server.Port}", error.Message, StringComparison.Ordinal);
        Assert.False(server.SawAuthOverTls);
        Assert.False(server.SawAuthInTheClear);
        Assert.Empty(server.Mail);
        Assert.DoesNotContain(Password, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_will_not_do_STARTTLS_is_refused_when_STARTTLS_is_required_and_nothing_is_sent()
    {
        await using var server = new TestSmtpServer(offerStartTls: false, password: Password);
        server.Start();

        EmailSendException error = await Assert.ThrowsAsync<EmailSendException>(() => Sender(Settings(server.Port, SmtpConfig.TlsStartTls, "mailer", Password)).SendAsync(s_message, CancellationToken.None));

        Assert.Contains("STARTTLS", error.Message, StringComparison.Ordinal);
        Assert.False(server.SawAuthInTheClear);
        Assert.False(server.SawAuthOverTls);
        Assert.Empty(server.Mail);
        Assert.DoesNotContain(Password, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_password_is_reported_as_rejected_credentials_and_a_server_that_echoes_it_cannot_put_it_on_the_page()
    {
        await using var server = new TestSmtpServer(offerStartTls: true, password: "the-real-password") { EchoPasswordInRejection = true };
        server.Start();

        using (TrustOnly(server.Certificate!.GetCertHashString()))
        {
            EmailSendException error = await Assert.ThrowsAsync<EmailSendException>(() => Sender(Settings(server.Port, SmtpConfig.TlsStartTls, "mailer", Password)).SendAsync(s_message, CancellationToken.None));

            Assert.Contains("did not accept the user name or password", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("the-real-password", error.Message, StringComparison.Ordinal);
            Assert.Empty(server.Mail);
        }
    }

    [Fact]
    public async Task A_refused_recipient_is_named_as_such()
    {
        await using var server = new TestSmtpServer { RecipientReply = "550 5.1.1 The email account that you tried to reach does not exist" };
        server.Start();

        EmailSendException error = await Assert.ThrowsAsync<EmailSendException>(() => Sender(Settings(server.Port, SmtpConfig.TlsNone)).SendAsync(s_message, CancellationToken.None));

        Assert.Contains("refused the recipient address", error.Message, StringComparison.Ordinal);
        Assert.Empty(server.Mail);
    }

    [Fact]
    public async Task Nothing_listening_is_reported_as_could_not_connect_with_the_address_tried()
    {
        int port;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }

        EmailSendException error = await Assert.ThrowsAsync<EmailSendException>(() => Sender(Settings(port, SmtpConfig.TlsNone)).SendAsync(s_message, CancellationToken.None));

        Assert.Contains($"Could not connect to 127.0.0.1:{port}", error.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionRefused", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_never_answers_is_given_up_on_and_the_wait_is_named()
    {
        await using var server = new TestSmtpServer(silent: true);
        server.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        EmailSendException error = await Assert.ThrowsAsync<EmailSendException>(() => Sender(Settings(server.Port, SmtpConfig.TlsNone, timeoutSeconds: 1)).SendAsync(s_message, CancellationToken.None));

        Assert.True(watch.Elapsed < LoadScale.Scaled(TimeSpan.FromSeconds(10)), $"took {watch.Elapsed}");
        Assert.Contains("did not answer within 1 second", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_who_cancels_gets_a_cancellation_not_a_timeout_message()
    {
        await using var server = new TestSmtpServer(silent: true);
        server.Start();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sender(Settings(server.Port, SmtpConfig.TlsNone, timeoutSeconds: 30)).SendAsync(s_message, cancel.Token));
    }

    [Fact]
    public async Task A_header_injection_attempt_in_the_address_cannot_add_a_recipient_or_a_header()
    {
        await using var server = new TestSmtpServer();
        server.Start();
        var hostile = new EmailMessage("victim@example.com\r\nBcc: attacker@example.com", "Hello", "Body\n");

        EmailSendException error = await Assert.ThrowsAsync<EmailSendException>(() => Sender(Settings(server.Port, SmtpConfig.TlsNone)).SendAsync(hostile, CancellationToken.None));

        Assert.Contains("not a plain email address", error.Message, StringComparison.Ordinal);
        Assert.Empty(server.Mail);
        Assert.Empty(server.Log);
    }

    [Theory]
    [InlineData("person@example.com", true)]
    [InlineData("first.last+tag@sub.example.co.uk", true)]
    [InlineData("victim@example.com\r\nBcc: attacker@example.com", false)]
    [InlineData("victim@example.com\nBcc: attacker@example.com", false)]
    [InlineData("Name <person@example.com>", false)]
    [InlineData("person@example.com, other@example.com", false)]
    [InlineData("person@example.com;other@example.com", false)]
    [InlineData("\"quoted\"@example.com", false)]
    [InlineData(" person@example.com", false)]
    [InlineData("person", false)]
    [InlineData("", false)]
    public void Only_a_bare_address_is_a_plain_address(string address, bool expected)
    {
        Assert.Equal(expected, EmailAddresses.IsPlainAddress(address));
    }

    [Fact]
    public void A_failure_never_repeats_what_the_server_said_so_a_quoted_password_cannot_reach_the_page()
    {
        EmailSettings settings = Settings(587, SmtpConfig.TlsStartTls, "mailer", Password);
        var echoing = new System.Net.Mail.SmtpException(System.Net.Mail.SmtpStatusCode.GeneralFailure, $"The server said: bad credentials for password {Password}, goodbye");

        string described = SmtpEmailSender.Describe(echoing, settings, Password);

        Assert.DoesNotContain(Password, described, StringComparison.Ordinal);
        Assert.DoesNotContain("bad credentials", described, StringComparison.Ordinal);
        Assert.DoesNotContain("goodbye", described, StringComparison.Ordinal);
        Assert.Contains("refused the message", described, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexpected_exception_is_named_by_type_only_and_the_password_is_still_redacted()
    {
        EmailSettings settings = Settings(587, SmtpConfig.TlsStartTls, "mailer", Password);

        string described = SmtpEmailSender.Describe(new ArgumentOutOfRangeException(null, $"odd value {Password}"), settings, Password);

        Assert.DoesNotContain(Password, described, StringComparison.Ordinal);
        Assert.DoesNotContain("odd value", described, StringComparison.Ordinal);
        Assert.Contains("ArgumentOutOfRangeException", described, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configuration_with_problems_sends_nothing_and_says_why()
    {
        EmailSettings broken = Settings(587, "bogus");

        EmailSendException error = await Assert.ThrowsAsync<EmailSendException>(() => Sender(broken).SendAsync(s_message, CancellationToken.None));

        Assert.Contains("email.smtp.tls must be", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Trusts exactly one certificate (by hash) for the duration of a test; SmtpClient has no per-client callback, so the framework's global one is used.</summary>
    private static IDisposable TrustOnly(string certificateHash)
    {
#pragma warning disable SYSLIB0014 // SmtpClient still consults it; there is no per-client alternative
        RemoteCertificateValidationCallback? previous = System.Net.ServicePointManager.ServerCertificateValidationCallback;
        System.Net.ServicePointManager.ServerCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && string.Equals(certificate.GetCertHashString(), certificateHash, StringComparison.OrdinalIgnoreCase);
        return new Restore(previous);
#pragma warning restore SYSLIB0014
    }

    private sealed class Restore(RemoteCertificateValidationCallback? previous) : IDisposable
    {
        public void Dispose()
        {
#pragma warning disable SYSLIB0014
            System.Net.ServicePointManager.ServerCertificateValidationCallback = previous;
#pragma warning restore SYSLIB0014
        }
    }
}

/// <summary>Serialises the SMTP tests: they share the framework's process-wide certificate callback.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SmtpCollection
{
    public const string Name = "smtp";
}
