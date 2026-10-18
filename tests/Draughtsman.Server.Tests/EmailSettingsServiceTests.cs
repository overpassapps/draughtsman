using Draughtsman.Server.Configuration;
using Draughtsman.Server.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The rules behind Admin, Email, each in both directions: draughtsman.yaml wins and locks a field while a key left at its default does not;
/// what an administrator types is refused when it could forge a header, name a URL where a host belongs or leave email half set up; and the
/// password is kept, replaced or cleared only by saying so.
/// </summary>
public sealed class EmailSettingsServiceTests : IDisposable
{
    private const string Password = "p4ssw0rd-never-in-a-message";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-email-service-tests", Guid.NewGuid().ToString("N"));

    public EmailSettingsServiceTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private EmailSettingsStore Store()
    {
        return new EmailSettingsStore(new DataFolder(_folder), DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_folder, "ring"))), NullLogger<EmailSettingsStore>.Instance);
    }

    private EmailSettingsService Service(Action<DraughtsmanConfig>? yaml = null, Func<string, string?>? environment = null)
    {
        var config = new DraughtsmanConfig();
        yaml?.Invoke(config);
        return new EmailSettingsService(config, _folder, Store(), NullLogger.Instance, environment ?? (_ => null));
    }

    private static EmailSettingsRequest Good(string? password = null)
    {
        return new EmailSettingsRequest(
            From: "Draughtsman <noreply@draughtsman.test>",
            Host: "smtp.draughtsman.test",
            Port: 587,
            Tls: "starttls",
            User: password is null ? null : "mailer",
            PublicUrl: "https://draughtsman.test",
            PasswordAction: password is null ? "keep" : "replace",
            Password: password);
    }

    private static string Errors(EmailPlan plan)
    {
        return string.Join(" | ", plan.Errors.Select(e => $"{e.Field}: {e.Message}"));
    }

    // ---- precedence: the file wins and locks, a default locks nothing -----------------------------------------------

    [Fact]
    public void Saved_values_apply_when_the_file_sets_nothing_and_nothing_is_locked()
    {
        EmailSettingsService service = Service();

        service.Apply(Good(Password), out IReadOnlyList<EmailFieldError> errors);

        Assert.Empty(errors);
        EmailSettings now = service.Current;
        Assert.True(now.Configured);
        Assert.Equal("smtp.draughtsman.test", now.Host);
        Assert.Equal("https://draughtsman.test", now.PublicUrl);
        Assert.Equal(Password, now.ReadPassword());
        Assert.Empty(now.Locks);
    }

    [Fact]
    public void A_value_the_file_sets_wins_locks_its_field_and_cannot_be_changed_from_the_page()
    {
        EmailSettingsService service = Service(config =>
        {
            config.Email.Provider = "smtp";
            config.Email.From = "files@draughtsman.test";
            config.Email.Smtp.Host = "from-the-file.example.com";
        });

        Assert.Equal(EmailSettings.ReasonInFile, service.Current.Locks[EmailSettings.FieldHost]);
        EmailPlan refused = service.Plan(Good() with { Host = "from-the-page.example.com" });
        Assert.False(refused.Valid);
        Assert.Contains("The mail server cannot be changed here", Errors(refused), StringComparison.Ordinal);
        Assert.Contains("draughtsman.yaml", Errors(refused), StringComparison.Ordinal);

        service.Apply(Good() with { Host = "from-the-page.example.com" }, out _);
        Assert.Equal("from-the-file.example.com", service.Current.Host);

        // The fields the file did not set are still the administrator's.
        Assert.DoesNotContain(EmailSettings.FieldUser, service.Current.Locks.Keys);
        Assert.DoesNotContain(EmailSettings.FieldReplyTo, service.Current.Locks.Keys);
        Assert.DoesNotContain(EmailSettings.FieldPublicUrl, service.Current.Locks.Keys);
        EmailSettingsService other = Service(config =>
        {
            config.Email.Provider = "smtp";
            config.Email.From = "files@draughtsman.test";
            config.Email.Smtp.Host = "from-the-file.example.com";
        });
        other.Apply(new EmailSettingsRequest(Host: "from-the-file.example.com", PublicUrl: "https://page-set.example.com"), out IReadOnlyList<EmailFieldError> errors);
        Assert.Empty(errors);
        Assert.Equal("https://page-set.example.com", other.Current.PublicUrl);
    }

    [Fact]
    public void Sending_a_locked_value_back_as_shown_is_accepted_and_not_stored()
    {
        EmailSettingsService service = Service(config =>
        {
            config.Email.Provider = "smtp";
            config.Email.Smtp.Host = "from-the-file.example.com";
        });

        EmailPlan plan = service.Plan(Good() with { Host = "from-the-file.example.com" });

        Assert.True(plan.Valid, Errors(plan));
        Assert.Null(plan.Overrides.Host);
    }

    [Fact]
    public void A_file_key_left_at_its_default_is_not_set_and_locks_nothing()
    {
        EmailSettingsService service = Service(config =>
        {
            config.Email.Smtp.Port = SmtpConfig.DefaultPort;
            config.Email.Smtp.Tls = "STARTTLS";
            config.Email.Smtp.Host = "   ";
        });

        Assert.Empty(service.Current.Locks);

        service.Apply(Good() with { Port = 2525 }, out IReadOnlyList<EmailFieldError> errors);
        Assert.Empty(errors);
        Assert.Equal(2525, service.Current.Port);
    }

    [Fact]
    public void A_file_value_that_is_not_the_default_locks_the_port_and_the_encryption()
    {
        EmailSettingsService service = Service(config =>
        {
            config.Email.Smtp.Port = 25;
            config.Email.Smtp.Tls = "none";
        });

        Assert.Contains(EmailSettings.FieldPort, service.Current.Locks.Keys);
        Assert.Contains(EmailSettings.FieldTls, service.Current.Locks.Keys);
        Assert.False(service.Plan(Good() with { Port = 587, Tls = "starttls" }).Valid);
    }

    [Fact]
    public void A_saved_value_the_file_has_taken_over_stays_underneath_and_returns_when_the_file_lets_go()
    {
        EmailSettingsService first = Service();
        first.Apply(Good(), out _);

        EmailSettingsService withFile = Service(config => config.Email.Smtp.Host = "file-host.example.com");
        Assert.Equal("file-host.example.com", withFile.Current.Form.Host);

        EmailSettingsService withoutFile = Service();
        Assert.Equal("smtp.draughtsman.test", withoutFile.Current.Form.Host);
    }

    [Fact]
    public void A_file_that_writes_files_locks_the_mail_server_fields_and_leaves_the_public_address_editable()
    {
        EmailSettingsService service = Service(config =>
        {
            config.Email.Provider = "file";
            config.Email.From = "files@draughtsman.test";
        });

        foreach (string field in new[] { EmailSettings.FieldHost, EmailSettings.FieldPort, EmailSettings.FieldTls, EmailSettings.FieldUser, EmailSettings.FieldPassword })
        {
            Assert.Contains("write files", service.Current.Locks[field], StringComparison.Ordinal);
        }

        Assert.DoesNotContain(EmailSettings.FieldPublicUrl, service.Current.Locks.Keys);
        service.Apply(new EmailSettingsRequest(PublicUrl: "https://page-set.example.com"), out IReadOnlyList<EmailFieldError> errors);
        Assert.Empty(errors);
        Assert.Equal("https://page-set.example.com", service.Current.PublicUrl);
        Assert.Equal("file", service.Current.Provider);
    }

    // ---- the password: environment/file wins, keep / replace / clear -------------------------------------------------

    [Fact]
    public void A_password_from_the_environment_wins_over_a_saved_one_and_the_page_cannot_replace_or_clear_it()
    {
        EmailSettingsService service = Service(environment: name => name == "DRAUGHTSMAN_SMTP_PASSWORD" ? "from-the-environment" : null);

        Assert.Contains("DRAUGHTSMAN_SMTP_PASSWORD", service.Current.Locks[EmailSettings.FieldPassword], StringComparison.Ordinal);
        Assert.Equal("environment", service.Current.PasswordKind);
        Assert.Contains("password cannot be changed here", Errors(service.Plan(Good(Password))), StringComparison.Ordinal);
        Assert.Contains("password cannot be changed here", Errors(service.Plan(Good() with { PasswordAction = "clear" })), StringComparison.Ordinal);
        Assert.Equal("from-the-environment", service.Current.ReadPassword());
    }

    [Fact]
    public void A_password_file_named_in_the_yaml_locks_the_password_and_wins()
    {
        File.WriteAllText(Path.Combine(_folder, "smtp-password"), "from-the-file\n");
        EmailSettingsService service = Service(config => config.Email.Smtp.PasswordFile = "smtp-password");

        Assert.Equal("file", service.Current.PasswordKind);
        Assert.Equal("from-the-file", service.Current.ReadPassword());
        Assert.True(service.Current.PasswordLocked);
    }

    [Fact]
    public void An_environment_variable_with_no_value_does_not_lock_the_password()
    {
        EmailSettingsService service = Service(environment: _ => null);

        Assert.DoesNotContain(EmailSettings.FieldPassword, service.Current.Locks.Keys);
        Assert.False(service.Current.PasswordLocked);
    }

    [Fact]
    public void The_password_is_kept_replaced_or_cleared_only_when_the_request_says_so()
    {
        EmailSettingsService service = Service();
        service.Apply(Good(Password), out _);
        Assert.Equal(Password, service.Current.ReadPassword());

        // Keep (and the default when the field is absent): an edit elsewhere must not lose the password.
        service.Apply(Good() with { User = "mailer", Port = 2525, PasswordAction = "keep" }, out IReadOnlyList<EmailFieldError> keepErrors);
        Assert.Empty(keepErrors);
        Assert.Equal(Password, service.Current.ReadPassword());
        service.Apply(Good() with { User = "mailer", Port = 2526, PasswordAction = null }, out _);
        Assert.Equal(Password, service.Current.ReadPassword());

        // Replace.
        IReadOnlyList<string> replaced = service.Apply(Good("a-new-password") with { Port = 2526 }, out _);
        Assert.Equal("a-new-password", service.Current.ReadPassword());
        Assert.Contains("password replaced", replaced);

        // Clear: with a user name still set that leaves email unable to sign in, so it is refused whole and nothing changes...
        EmailPlan stillSignedIn = service.Plan(Good() with { User = "mailer", Port = 2526, PasswordAction = "clear" });
        Assert.False(stillSignedIn.Valid);
        Assert.Contains("no password was found", Errors(stillSignedIn), StringComparison.Ordinal);
        Assert.Equal("a-new-password", service.Current.ReadPassword());

        // ...and with the user name taken away too it goes.
        IReadOnlyList<string> cleared = service.Apply(Good() with { User = "", Port = 2526, PasswordAction = "clear" }, out IReadOnlyList<EmailFieldError> clearErrors);
        Assert.Empty(clearErrors);
        Assert.Null(service.Current.ReadPassword());
        Assert.Contains("password cleared", cleared);
    }

    [Fact]
    public void A_password_with_no_user_name_to_go_with_it_is_refused_and_a_stored_one_is_dropped_with_the_user()
    {
        EmailSettingsService service = Service();
        EmailPlan noUser = service.Plan(Good() with { PasswordAction = "replace", Password = Password });
        Assert.False(noUser.Valid);
        Assert.Contains("needs a user name", Errors(noUser), StringComparison.Ordinal);

        service.Apply(Good(Password), out _);
        service.Apply(Good() with { User = "", PasswordAction = "keep" }, out _);
        Assert.Null(service.Current.Stored.Password);
        Assert.Null(service.Current.User);
    }

    [Fact]
    public void An_unknown_password_action_is_refused()
    {
        Assert.False(Service().Plan(Good() with { PasswordAction = "reveal" }).Valid);
    }

    // ---- what an administrator types is refused when it is not what it claims to be ----------------------------------

    [Theory]
    [InlineData("https://smtp.example.com")]
    [InlineData("smtp.example.com/relay")]
    [InlineData("user@smtp.example.com")]
    [InlineData("smtp.example.com:587")]
    [InlineData("smtp .example.com")]
    [InlineData("smtp.example.com\r\nBcc: attacker@example.com")]
    [InlineData("smtp.example.com?x=1")]
    [InlineData("-bad-.example.com")]
    [InlineData("[::1]")]
    [InlineData("smtp.exämple.com")]
    public void A_mail_server_that_is_not_a_plain_host_name_or_address_is_refused_by_name_and_never_echoed(string host)
    {
        EmailSettingsService service = Service();

        EmailPlan plan = service.Plan(Good() with { Host = host });

        Assert.False(plan.Valid);
        Assert.Contains(plan.Errors, e => e.Field == EmailSettings.FieldHost);
        Assert.DoesNotContain("attacker", Errors(plan), StringComparison.Ordinal);
        service.Apply(Good() with { Host = host }, out _);
        Assert.False(service.Current.Configured);
    }

    [Theory]
    [InlineData("smtp.office365.com")]
    [InlineData("email-smtp.eu-west-1.amazonaws.com")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    [InlineData("mail_relay.corp.example")]
    public void Plain_hosts_and_addresses_are_accepted(string host)
    {
        EmailPlan plan = Service().Plan(Good() with { Host = host });

        Assert.True(plan.Valid, Errors(plan));
    }

    [Theory]
    [InlineData("from", "noreply@draughtsman.test\r\nBcc: attacker@example.com")]
    [InlineData("from", "noreply@draughtsman.test\nBcc: attacker@example.com")]
    [InlineData("from", "Draughtsman\r <noreply@draughtsman.test>")]
    [InlineData("from", "not an address")]
    [InlineData("from", "a@b.test, c@d.test")]
    [InlineData("replyTo", "help@draughtsman.test\r\nBcc: attacker@example.com")]
    [InlineData("replyTo", "help@draughtsman.test\u0000")]
    [InlineData("replyTo", "not an address")]
    public void An_address_that_could_forge_a_header_or_is_not_one_address_is_refused(string field, string value)
    {
        EmailSettingsRequest request = field == "from" ? Good() with { From = value } : Good() with { ReplyTo = value };
        EmailSettingsService service = Service();

        EmailPlan plan = service.Plan(request);

        Assert.False(plan.Valid);
        Assert.Contains(plan.Errors, e => e.Field == (field == "from" ? EmailSettings.FieldFrom : EmailSettings.FieldReplyTo));
        Assert.DoesNotContain("attacker", Errors(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void A_user_name_a_password_and_a_public_address_cannot_carry_a_line_break()
    {
        EmailSettingsService service = Service();

        Assert.Contains(EmailSettings.FieldUser, service.Plan(Good(Password) with { User = "mailer\r\nRCPT TO:<x@y.test>" }).Errors.Select(e => e.Field));
        Assert.Contains(EmailSettings.FieldPassword, service.Plan(Good("pass\nword")).Errors.Select(e => e.Field));
        Assert.Contains(EmailSettings.FieldPublicUrl, service.Plan(Good() with { PublicUrl = "https://draughtsman.test\r\nX: y" }).Errors.Select(e => e.Field));
        Assert.Contains(EmailSettings.FieldPublicUrl, service.Plan(Good() with { PublicUrl = "ftp://draughtsman.test" }).Errors.Select(e => e.Field));
        Assert.Contains(EmailSettings.FieldPublicUrl, service.Plan(Good() with { PublicUrl = "https://user:pw@draughtsman.test" }).Errors.Select(e => e.Field));
        Assert.Contains(EmailSettings.FieldPublicUrl, service.Plan(Good() with { PublicUrl = "https://draughtsman.test/?a=b" }).Errors.Select(e => e.Field));
    }

    [Fact]
    public void Field_lengths_are_capped()
    {
        EmailSettingsService service = Service();

        Assert.False(service.Plan(Good() with { Host = new string('a', 254) }).Valid);
        Assert.False(service.Plan(Good() with { From = new string('a', 330) + "@x.test" }).Valid);
        Assert.False(service.Plan(Good(new string('p', EmailFieldRules.MaxPassword + 1))).Valid);
        Assert.False(service.Plan(Good(Password) with { User = new string('u', EmailFieldRules.MaxUser + 1) }).Valid);
        Assert.False(service.Plan(Good() with { PublicUrl = "https://x.test/" + new string('p', EmailFieldRules.MaxPublicUrl) }).Valid);
        Assert.True(service.Plan(Good(new string('p', EmailFieldRules.MaxPassword))).Valid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(-1)]
    public void A_port_outside_1_to_65535_is_refused(int port)
    {
        Assert.Contains(EmailSettings.FieldPort, Service().Plan(Good() with { Port = port }).Errors.Select(e => e.Field));
    }

    [Theory]
    [InlineData("ssl")]
    [InlineData("implicit")]
    [InlineData("tls")]
    public void Encryption_is_starttls_or_none_and_implicit_tls_is_explained(string tls)
    {
        EmailPlan plan = Service().Plan(Good() with { Tls = tls });

        Assert.False(plan.Valid);
        Assert.Contains("Implicit TLS on port 465 is not supported", Errors(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void No_encryption_is_refused_with_a_user_name_for_a_server_on_another_machine_and_allowed_on_this_one()
    {
        EmailPlan remote = Service().Plan(Good(Password) with { Tls = "none" });
        Assert.False(remote.Valid);
        Assert.Contains("will not send a password to another machine unencrypted", Errors(remote), StringComparison.Ordinal);

        Assert.True(Service().Plan(Good(Password) with { Tls = "none", Host = "127.0.0.1" }).Valid);
        Assert.True(Service().Plan(Good() with { Tls = "none", User = "" }).Valid);
    }

    // ---- email is saved whole or not at all ------------------------------------------------------------------------

    [Fact]
    public void A_form_that_would_leave_email_half_set_up_is_refused_and_nothing_is_stored()
    {
        EmailSettingsService service = Service();

        service.Apply(new EmailSettingsRequest(Host: "smtp.draughtsman.test"), out IReadOnlyList<EmailFieldError> noFrom);
        Assert.Contains("email.from is not set", string.Join(' ', noFrom.Select(e => e.Message)), StringComparison.Ordinal);

        service.Apply(Good() with { User = "mailer" }, out IReadOnlyList<EmailFieldError> noPassword);
        Assert.Contains("no password was found", string.Join(' ', noPassword.Select(e => e.Message)), StringComparison.Ordinal);

        Assert.False(service.Current.Configured);
        Assert.False(service.HasSaved);
        Assert.False(File.Exists(Path.Combine(_folder, "email", "settings.json")));
    }

    [Fact]
    public void Only_the_public_address_can_be_saved_while_the_mail_server_is_still_blank()
    {
        EmailSettingsService service = Service();

        service.Apply(new EmailSettingsRequest(PublicUrl: "https://draughtsman.test"), out IReadOnlyList<EmailFieldError> errors);

        Assert.Empty(errors);
        Assert.False(service.Current.Configured);
        Assert.Equal("https://draughtsman.test", service.Current.PublicUrl);
        Assert.Null(service.Current.Stored.Overrides.Provider);
    }

    [Fact]
    public void Changed_names_what_changed_and_never_a_value()
    {
        EmailSettingsService service = Service();

        IReadOnlyList<string> first = service.Apply(Good(Password), out _);
        IReadOnlyList<string> nothing = service.Apply(Good() with { User = "mailer" }, out _);
        IReadOnlyList<string> port = service.Apply(Good() with { User = "mailer", Port = 2525 }, out _);

        Assert.Contains("email turned on", first);
        Assert.Contains("mail server", first);
        Assert.Contains("password replaced", first);
        Assert.Empty(nothing);
        Assert.Equal(["port"], port);
        string everything = string.Join(' ', first.Concat(nothing).Concat(port));
        Assert.DoesNotContain(Password, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp.draughtsman.test", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_forgets_every_saved_field_and_the_password_and_applies_at_once()
    {
        EmailSettingsService service = Service();
        service.Apply(Good(Password), out _);
        Assert.True(service.HasSaved);

        service.Reset();

        Assert.False(service.HasSaved);
        Assert.False(service.Current.Configured);
        Assert.Null(service.Current.ReadPassword());
        Assert.False(File.Exists(Path.Combine(_folder, "email", "settings.json")));
        Assert.False(Service().Current.Configured);
    }

    [Fact]
    public void A_save_survives_a_restart()
    {
        Service().Apply(Good(Password), out _);

        EmailSettings after = Service().Current;

        Assert.True(after.Configured);
        Assert.Equal(Password, after.ReadPassword());
    }
}
