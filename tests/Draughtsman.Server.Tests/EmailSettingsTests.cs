using Draughtsman.Server.Configuration;
using Draughtsman.Server.Email;

namespace Draughtsman.Server.Tests;

/// <summary>What <c>email:</c> and <c>server.publicUrl</c> add up to, and the words it uses when they do not: each rule in both directions.</summary>
public class EmailSettingsTests
{
    private static EmailSettings Resolve(Action<EmailConfig>? email = null, Action<ServerConfig>? server = null, Func<string, string?>? environment = null, string? dataFolder = null)
    {
        var config = new EmailConfig
        {
            Provider = EmailConfig.ProviderSmtp,
            From = "Draughtsman <noreply@draughtsman.test>",
            Smtp = new SmtpConfig { Host = "smtp.draughtsman.test", Port = 587 },
        };
        email?.Invoke(config);
        var serverConfig = new ServerConfig { PublicUrl = "https://draughtsman.test" };
        server?.Invoke(serverConfig);
        return new EmailSettings(config, serverConfig, dataFolder ?? Path.GetTempPath(), environment ?? (_ => null));
    }

    [Fact]
    public void A_complete_smtp_configuration_can_send_and_can_email_links()
    {
        EmailSettings settings = Resolve();

        Assert.True(settings.Configured);
        Assert.True(settings.CanEmailLinks);
        Assert.Empty(settings.Problems);
        Assert.Equal("smtp.draughtsman.test", settings.Host);
        Assert.Equal(587, settings.Port);
        Assert.True(settings.StartTls);
        Assert.Equal("noreply@draughtsman.test", settings.From!.Address);
        Assert.Equal("https://draughtsman.test", settings.PublicUrl);
    }

    [Fact]
    public void No_provider_means_no_email_and_no_complaint_about_it()
    {
        EmailSettings settings = Resolve(email => email.Provider = string.Empty);

        Assert.False(settings.Configured);
        Assert.False(settings.CanEmailLinks);
        Assert.Empty(settings.Problems);
    }

    [Theory]
    [InlineData("host", "", "email.smtp.host is not set")]
    [InlineData("host", "bad host", "is not a host name")]
    [InlineData("host", "smtp.example.com/path", "is not a host name")]
    [InlineData("port", "0", "is not a port number")]
    [InlineData("port", "70000", "is not a port number")]
    [InlineData("tls", "ssl", "email.smtp.tls must be")]
    [InlineData("tls", "implicit", "Implicit TLS on port 465 is not supported")]
    [InlineData("from", "", "email.from is not set")]
    [InlineData("from", "not an address", "is not an email address")]
    [InlineData("provider", "sendgrid", "email.provider must be")]
    public void Each_way_the_configuration_can_be_wrong_is_named_and_stops_sending(string field, string value, string expectedProblem)
    {
        EmailSettings settings = Resolve(email =>
        {
            switch (field)
            {
                case "host": email.Smtp.Host = value; break;
                case "port": email.Smtp.Port = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "tls": email.Smtp.Tls = value; break;
                case "from": email.From = value; break;
                case "provider": email.Provider = value; break;
            }
        });

        Assert.False(settings.Configured);
        Assert.False(settings.CanEmailLinks);
        Assert.Contains(settings.Problems, p => p.Contains(expectedProblem, StringComparison.Ordinal));
    }

    [Fact]
    public void A_user_name_needs_a_password_from_the_environment_or_a_file_and_the_file_wins()
    {
        Assert.Contains(Resolve(email => email.Smtp.User = "mailer").Problems, p => p.Contains("no password was found", StringComparison.Ordinal));

        EmailSettings fromEnvironment = Resolve(email => email.Smtp.User = "mailer", environment: name => name == "DRAUGHTSMAN_SMTP_PASSWORD" ? "from-env" : null);
        Assert.True(fromEnvironment.Configured);
        Assert.Equal("from-env", fromEnvironment.ReadPassword());
        Assert.Equal("environment variable DRAUGHTSMAN_SMTP_PASSWORD", fromEnvironment.PasswordSource());

        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "smtp-password"), "from-file\n");
            EmailSettings fromFile = Resolve(email => { email.Smtp.User = "mailer"; email.Smtp.PasswordFile = "smtp-password"; }, environment: _ => "from-env", dataFolder: folder);
            Assert.True(fromFile.Configured);
            Assert.Equal("from-file", fromFile.ReadPassword());
            Assert.Equal("file smtp-password", fromFile.PasswordSource());

            EmailSettings missingFile = Resolve(email => { email.Smtp.User = "mailer"; email.Smtp.PasswordFile = "nowhere"; }, environment: _ => "from-env", dataFolder: folder);
            Assert.False(missingFile.Configured);
            Assert.Contains("not readable or empty", missingFile.PasswordSource(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_password_is_never_sent_unencrypted_to_another_machine()
    {
        EmailSettings remote = Resolve(email => { email.Smtp.Tls = SmtpConfig.TlsNone; email.Smtp.User = "mailer"; }, environment: _ => "pw");
        EmailSettings loopback = Resolve(email => { email.Smtp.Tls = SmtpConfig.TlsNone; email.Smtp.User = "mailer"; email.Smtp.Host = "127.0.0.1"; }, environment: _ => "pw");
        EmailSettings openRelay = Resolve(email => email.Smtp.Tls = SmtpConfig.TlsNone);

        Assert.False(remote.Configured);
        Assert.Contains(remote.Problems, p => p.Contains("will not send a password to another machine unencrypted", StringComparison.Ordinal));
        Assert.True(loopback.Configured);
        Assert.False(loopback.StartTls);
        Assert.True(openRelay.Configured);
    }

    [Fact]
    public void The_file_provider_needs_only_a_from_address_and_resolves_its_folder_against_the_data_folder()
    {
        EmailSettings settings = Resolve(email => { email.Provider = EmailConfig.ProviderFile; email.Smtp = new SmtpConfig(); email.File.Folder = "mail-out"; }, dataFolder: "/srv/data");

        Assert.True(settings.Configured);
        Assert.Equal(Path.GetFullPath("/srv/data/mail-out"), settings.OutboxFolder);
        Assert.False(Resolve(email => { email.Provider = EmailConfig.ProviderFile; email.From = string.Empty; }).Configured);
    }

    [Theory]
    [InlineData("https://draughtsman.example.com", "https://draughtsman.example.com")]
    [InlineData("https://draughtsman.example.com/", "https://draughtsman.example.com")]
    [InlineData("  http://localhost:5180  ", "http://localhost:5180")]
    [InlineData("https://example.com/draughtsman/", "https://example.com/draughtsman")]
    public void A_public_address_is_kept_without_its_trailing_slash(string configured, string expected)
    {
        EmailSettings settings = Resolve(server: server => server.PublicUrl = configured);

        Assert.Equal(expected, settings.PublicUrl);
        Assert.Empty(settings.LinkProblems);
    }

    [Theory]
    [InlineData("draughtsman.example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("https://example.com/?next=x")]
    [InlineData("https://example.com/#frag")]
    [InlineData("javascript:alert(1)")]
    public void A_public_address_that_is_not_a_plain_http_address_is_refused_and_links_are_off(string configured)
    {
        EmailSettings settings = Resolve(server: server => server.PublicUrl = configured);

        Assert.Null(settings.PublicUrl);
        Assert.False(settings.CanEmailLinks);
        Assert.True(settings.Configured);
        Assert.Contains(settings.LinkProblems, p => p.Contains("server.publicUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_public_address_mail_can_be_sent_but_links_cannot_be_emailed()
    {
        EmailSettings settings = Resolve(server: server => server.PublicUrl = string.Empty);

        Assert.True(settings.Configured);
        Assert.False(settings.CanEmailLinks);
        Assert.Empty(settings.LinkProblems);
    }
}
