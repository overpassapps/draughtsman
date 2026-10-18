using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Auth.PasswordReset;
using Draughtsman.Server.Email;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Self-service "Forgot password?" by email, run against the file provider (a message is a file in the outbox, so a test reads the
/// email back). Every rule that keeps it from being used to find out who has an account, to lock anyone out, or to be told
/// apart by timing has a test here in both directions.
/// </summary>
public class PasswordResetEmailTests
{
    private static readonly DateTimeOffset s_start = DateTimeOffset.Parse("2030-01-01T09:00:00Z");

    private static async Task<(ResetServerFactory Factory, HttpClient Admin, string Id)> Instance(string email = "forgot@example.com", string? yaml = null, TimeProvider? clock = null, IEmailSender? sender = null)
    {
        var factory = new ResetServerFactory(yaml ?? ResetServerFactory.EmailYaml, clock, sender);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser(email);
        return (factory, admin, id);
    }

    [Fact]
    public async Task Asking_for_a_link_emails_the_person_a_working_single_use_link_and_the_email_says_when_it_stops()
    {
        (ResetServerFactory factory, HttpClient admin, string id) = await Instance();
        using (factory)
        {
            HttpClient signedIn = await factory.SignIn("forgot@example.com");

            HttpResponseMessage asked = await factory.CreateClient().RequestReset("forgot@example.com");
            await factory.WaitForMail();

            Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
            OutboxMessage mail = Assert.Single(factory.ReadOutbox());
            Assert.Equal("forgot@example.com", mail.To);
            Assert.Equal("Reset your Draughtsman password", mail.Subject);
            Assert.StartsWith(ResetServerFactory.PublicUrl + "/reset-password#token=dpr_", mail.Link, StringComparison.Ordinal);
            Assert.Contains("expires in 1 hour", mail.Body, StringComparison.Ordinal);
            Assert.Contains("If you did not ask for this, ignore this email", mail.Body, StringComparison.Ordinal);
            Assert.Contains("signs you out everywhere else", mail.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthBarHelpers.UserPassword, mail.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("dst_", mail.Body, StringComparison.Ordinal);
            // Nothing happened to the account just by asking.
            Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/documents")).StatusCode);
            Assert.True(await factory.CanSignIn("forgot@example.com", AuthBarHelpers.UserPassword));

            Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().Complete(mail.Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().Complete(mail.Secret)).StatusCode);
            Assert.True(await factory.CanSignIn("forgot@example.com", PasswordResetHelpers.NewPassword));
            Assert.Equal(HttpStatusCode.Unauthorized, (await signedIn.GetAsync("/api/documents")).StatusCode);
            _ = (admin, id);
        }
    }

    [Fact]
    public async Task The_answer_is_the_same_for_an_address_with_an_account_one_without_a_deactivated_one_and_an_oddly_typed_one()
    {
        (ResetServerFactory factory, HttpClient admin, string id) = await Instance();
        using (factory)
        {
            string inactiveId = await admin.CreateUser("inactive@example.com");
            await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{inactiveId}", new { active = false }, admin));
            HttpClient browser = factory.CreateClient();
            var bodies = new List<string>();
            string[] addresses = ["forgot@example.com", "nobody-at-all@example.com", "inactive@example.com", "  FORGOT@Example.COM  ", "x@y"];
            for (int i = 0; i < addresses.Length; i++)
            {
                HttpResponseMessage response = await browser.RequestReset(addresses[i], ip: "10.1.0." + i);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
                Assert.False(response.Headers.Contains("Set-Cookie"));
                bodies.Add(await response.Content.ReadAsStringAsync());
            }

            await factory.WaitForMail();

            Assert.Single(bodies.Distinct());
            Assert.Contains("If an account with that address can be reset", bodies[0], StringComparison.Ordinal);
            // Mail went to the one active account, twice (the two spellings of its address), and nowhere else.
            Assert.Equal(2, factory.ReadOutbox().Count);
            Assert.All(factory.ReadOutbox(), m => Assert.Equal("forgot@example.com", m.To));
            // The log records a request for every address alike, and none of those rows says whether an account exists.
            JsonArray requested = await admin.SecurityEvents("?type=password_reset.requested&limit=100");
            Assert.Equal(addresses.Length, requested.Count);
            Assert.All(requested, e => Assert.Null(e!["targetUserId"]));
            _ = id;
        }
    }

    [Fact]
    public async Task How_long_the_answer_takes_does_not_depend_on_whether_the_address_has_an_account()
    {
        // The mail server is made slow enough that sending inside the request could not hide in noise, and the comparison is against a baseline taken
        // in the same process a moment apart (the answers for addresses with no account, which never involve mail), never against a number of
        // milliseconds: a machine under load slows both sides alike, and only a request that waited for the mail server is half this far behind.
        TimeSpan mailDelay = TimeSpan.FromMilliseconds(1500);
        var slow = new RecordingSender(mailDelay);
        (ResetServerFactory factory, _, _) = await Instance(sender: slow);
        using (factory)
        {
            HttpClient browser = factory.CreateClient();
            await browser.RequestReset("warm-up@example.com", ip: "10.2.0.1");
            await factory.WaitForMail();
            var known = new List<double>();
            var unknown = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                known.Add(await Timed(() => browser.RequestReset("forgot@example.com", ip: "10.2.1." + i)));
                unknown.Add(await Timed(() => browser.RequestReset($"stranger{i}@example.com", ip: "10.2.2." + i)));
            }

            double knownMedian = known.Order().ElementAt(2);
            double unknownMedian = unknown.Order().ElementAt(2);
            await factory.WaitForMail();

            Assert.True(knownMedian - unknownMedian < mailDelay.TotalMilliseconds / 2, $"known {knownMedian:0} ms vs unknown {unknownMedian:0} ms: the answer waited for the mail server ({mailDelay.TotalMilliseconds:0} ms)");
            // And the slow mail really was sent (three for the one account: the per-address cap), so the comparison above compared something.
            Assert.Equal(3, slow.Sent.Count);
        }
    }

    [Fact]
    public async Task Strangers_asking_for_someones_address_cannot_replace_the_link_in_their_inbox_nor_lock_them_out()
    {
        (ResetServerFactory factory, HttpClient admin, string id) = await Instance();
        using (factory)
        {
            HttpClient signedIn = await factory.SignIn("forgot@example.com");
            HttpClient browser = factory.CreateClient();
            await browser.RequestReset("forgot@example.com", ip: Owner);
            await factory.WaitForMail();
            string genuine = Assert.Single(factory.ReadOutbox()).Secret!;

            // An attacker hammers the address from their own: they are throttled after three, and the account is untouched throughout.
            var statuses = new List<HttpStatusCode>();
            for (int i = 0; i < 6; i++)
            {
                statuses.Add((await browser.RequestReset("forgot@example.com", ip: Attacker)).StatusCode);
            }

            await factory.WaitForMail();
            Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
            Assert.True(await factory.CanSignIn("forgot@example.com", AuthBarHelpers.UserPassword));
            Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync("/api/documents")).StatusCode);

            // The link the owner already holds still works, whatever the attacker caused to be sent, and so does any of the others.
            Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(genuine)).StatusCode);
            Assert.True(await factory.CanSignIn("forgot@example.com", PasswordResetHelpers.NewPassword));
            _ = (admin, id);
        }
    }

    [Fact]
    public async Task A_second_request_does_not_end_the_first_link_but_the_inbox_never_gets_more_than_three_in_a_quarter_of_an_hour()
    {
        (ResetServerFactory factory, HttpClient admin, _) = await Instance();
        using (factory)
        {
            HttpClient browser = factory.CreateClient();
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(HttpStatusCode.Accepted, (await browser.RequestReset("forgot@example.com", ip: "10.3.0." + i)).StatusCode);
            }

            await factory.WaitForMail();

            IReadOnlyList<OutboxMessage> mails = factory.ReadOutbox();
            Assert.Equal(PasswordResetPolicy.MaxEmailsPerWindow, mails.Count);
            Assert.Equal(3, mails.Select(m => m.Secret).Distinct().Count());
            // All three are live at once (a request never ends an earlier one: the oldest still works), and spending one ends the others.
            Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(mails[0].Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete(mails[1].Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete(mails[2].Secret)).StatusCode);
            Assert.Equal(3, (await admin.SecurityEvents("?type=password_reset.email_sent")).Count);
        }
    }

    [Fact]
    public async Task The_cap_on_emails_lifts_when_the_window_has_passed()
    {
        var clock = new TestClock(s_start);
        (ResetServerFactory factory, _, _) = await Instance(clock: clock);
        using (factory)
        {
            HttpClient browser = factory.CreateClient();
            for (int i = 0; i < 4; i++)
            {
                await browser.RequestReset("forgot@example.com", ip: "10.4.0." + i);
            }

            await factory.WaitForMail();
            Assert.Equal(3, factory.ReadOutbox().Count);

            clock.Advance(PasswordResetPolicy.EmailWindow + TimeSpan.FromMinutes(1));
            await browser.RequestReset("forgot@example.com", ip: "10.4.1.1");
            await factory.WaitForMail();

            Assert.Equal(4, factory.ReadOutbox().Count);
        }
    }

    [Fact]
    public async Task One_address_is_stopped_after_ten_requests_and_after_three_for_the_same_email()
    {
        (ResetServerFactory factory, HttpClient admin, _) = await Instance();
        using (factory)
        {
            HttpClient browser = factory.CreateClient();
            for (int i = 0; i < PasswordResetPolicy.RequestsPerAddress; i++)
            {
                Assert.Equal(HttpStatusCode.Accepted, (await browser.RequestReset($"stranger{i}@example.com", ip: Attacker)).StatusCode);
            }

            // Eleventh from the same address: refused, for a known address and an unknown one alike.
            HttpResponseMessage knownBlocked = await browser.RequestReset("forgot@example.com", ip: Attacker);
            HttpResponseMessage unknownBlocked = await browser.RequestReset("one-more-stranger@example.com", ip: Attacker);
            Assert.Equal(HttpStatusCode.TooManyRequests, knownBlocked.StatusCode);
            Assert.Equal(await knownBlocked.Problem(), await unknownBlocked.Problem());
            // A different address is not affected.
            Assert.Equal(HttpStatusCode.Accepted, (await browser.RequestReset("forgot@example.com", ip: Owner)).StatusCode);
            Assert.Single(await admin.SecurityEvents("?type=sign_in.throttled"));
            await factory.WaitForMail();
        }
    }

    [Fact]
    public async Task An_emailed_link_lasts_an_hour_by_default_and_the_configured_time_otherwise()
    {
        var clock = new TestClock(s_start);
        (ResetServerFactory factory, _, _) = await Instance(clock: clock);
        using (factory)
        {
            HttpClient browser = factory.CreateClient();
            await browser.RequestReset("forgot@example.com", ip: "10.5.0.1");
            await factory.WaitForMail();
            string secret = Assert.Single(factory.ReadOutbox()).Secret!;

            clock.Advance(TimeSpan.FromMinutes(61));
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete(secret)).StatusCode);
            Assert.False(await factory.CanSignIn("forgot@example.com", PasswordResetHelpers.NewPassword));
        }

        var early = new TestClock(s_start);
        (ResetServerFactory thirty, _, _) = await Instance(yaml: ResetServerFactory.EmailYaml + "\nauth:\n  passwordReset:\n    emailedLinkMinutes: 30\n", clock: early);
        using (thirty)
        {
            HttpClient browser = thirty.CreateClient();
            await browser.RequestReset("forgot@example.com", ip: "10.5.1.1");
            await thirty.WaitForMail();
            OutboxMessage mail = Assert.Single(thirty.ReadOutbox());
            Assert.Contains("expires in 30 minutes, at 09:30 UTC on 1 January 2030", mail.Body, StringComparison.Ordinal);

            early.Advance(TimeSpan.FromMinutes(29));
            Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(mail.Secret)).StatusCode);
        }
    }

    [Fact]
    public async Task An_administrator_link_ends_the_emailed_ones_and_using_one_ends_the_rest()
    {
        (ResetServerFactory factory, HttpClient admin, string id) = await Instance();
        using (factory)
        {
            HttpClient browser = factory.CreateClient();
            await browser.RequestReset("forgot@example.com", ip: "10.6.0.1");
            await browser.RequestReset("forgot@example.com", ip: "10.6.0.2");
            await factory.WaitForMail();
            IReadOnlyList<OutboxMessage> mails = factory.ReadOutbox();

            (_, string? adminLink) = await admin.CreateLink(id);
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete(mails[0].Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete(mails[1].Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(adminLink)).StatusCode);
        }
    }

    [Fact]
    public async Task A_deactivated_account_gets_no_email_and_no_link_exists_to_use()
    {
        (ResetServerFactory factory, HttpClient admin, string id) = await Instance();
        using (factory)
        {
            await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{id}", new { active = false }, admin));

            Assert.Equal(HttpStatusCode.Accepted, (await factory.CreateClient().RequestReset("forgot@example.com")).StatusCode);
            await factory.WaitForMail();

            Assert.Empty(factory.ReadOutbox());
            Assert.Empty(await admin.SecurityEvents("?type=password_reset.email_sent"));
        }
    }

    [Fact]
    public async Task When_the_mail_cannot_be_sent_the_link_made_for_it_is_ended_the_failure_is_logged_and_the_answer_is_unchanged()
    {
        var failing = new RecordingSender(TimeSpan.Zero) { Failure = new EmailSendException("The mail server at mail.example.com:587 did not answer within 20 seconds.") };
        (ResetServerFactory factory, HttpClient admin, _) = await Instance(sender: failing);
        using (factory)
        {
            HttpResponseMessage asked = await factory.CreateClient().RequestReset("forgot@example.com");
            await factory.WaitForMail();

            Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
            EmailMessage attempted = Assert.Single(failing.Attempted);
            string secret = System.Text.RegularExpressions.Regex.Match(attempted.Body, @"token=(dpr_[A-Za-z0-9_-]+)").Groups[1].Value;
            // The link never reached anyone, so it was ended rather than left live for an hour.
            Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().Complete(secret)).StatusCode);
            JsonNode failed = Assert.Single(await admin.SecurityEvents("?type=password_reset.email_failed"))!;
            Assert.Contains("did not answer within 20 seconds", failed["detail"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, await admin.WholeSecurityLog(), StringComparison.Ordinal);
            Assert.Empty(await admin.SecurityEvents("?type=password_reset.email_sent"));
        }
    }

    [Fact]
    public async Task The_events_for_the_email_path_are_recorded_without_the_link()
    {
        (ResetServerFactory factory, HttpClient admin, string id) = await Instance();
        using (factory)
        {
            HttpClient browser = factory.CreateClient();
            await browser.RequestReset("forgot@example.com", ip: "10.7.0.1");
            await factory.WaitForMail();
            string secret = Assert.Single(factory.ReadOutbox()).Secret!;
            await browser.Complete(secret);

            string log = await admin.WholeSecurityLog();

            Assert.Single(await admin.SecurityEvents("?type=password_reset.requested"));
            JsonNode sent = Assert.Single(await admin.SecurityEvents($"?type=password_reset.email_sent&user={id}"))!;
            Assert.Contains("reset link emailed (file); expires", sent["detail"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Single(await admin.SecurityEvents($"?type=password_reset.link_used&user={id}"));
            Assert.Contains("emailed link used", log, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
            Assert.DoesNotContain(PasswordResetSecrets.Hash(secret), log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_account_whose_address_would_mail_somebody_else_is_never_mailed()
    {
        (ResetServerFactory factory, HttpClient admin, _) = await Instance();
        using (factory)
        {
            // The framework's parser reads this as a display name followed by attacker@example.com. The mail layer must not.
            const string Hostile = "victim@example.com\r\nBcc: attacker@example.com";
            await admin.CreateUser(Hostile);

            Assert.Equal(HttpStatusCode.Accepted, (await factory.CreateClient().RequestReset(Hostile)).StatusCode);
            await factory.WaitForMail();

            Assert.Empty(factory.ReadOutbox());
            Assert.Empty(await admin.SecurityEvents("?type=password_reset.email_sent"));
        }
    }

    [Fact]
    public async Task Without_email_set_up_the_route_is_not_there_and_the_sign_in_page_is_told_so()
    {
        using var plain = new DraughtsmanServerFactory();
        HttpClient admin = await plain.CreateAuthenticatedClient();
        await admin.CreateUser("no-email@example.com");

        Assert.Equal(HttpStatusCode.NotFound, (await plain.CreateClient().RequestReset("no-email@example.com")).StatusCode);
        Assert.False((await SelfServiceFlag(plain)));

        // Email alone is not enough: the link needs the instance's own address, which is never taken from the request.
        using var noAddress = new ResetServerFactory("email:\n  provider: file\n  from: a@b.example\n");
        Assert.Equal(HttpStatusCode.NotFound, (await noAddress.CreateClient().RequestReset("x@example.com")).StatusCode);
        Assert.False(await SelfServiceFlag(noAddress));

        // Turned off on purpose.
        using var off = new ResetServerFactory(ResetServerFactory.EmailYaml + "\nauth:\n  passwordReset:\n    selfService: false\n");
        Assert.Equal(HttpStatusCode.NotFound, (await off.CreateClient().RequestReset("x@example.com")).StatusCode);
        Assert.False(await SelfServiceFlag(off));

        // A half-configured mail server (no host) is no mail server.
        using var halfSmtp = new ResetServerFactory("server:\n  publicUrl: https://draughtsman.test\nemail:\n  provider: smtp\n  from: a@b.example\n");
        Assert.False(await SelfServiceFlag(halfSmtp));

        using var on = new ResetServerFactory(ResetServerFactory.EmailYaml);
        Assert.True(await SelfServiceFlag(on));
    }

    [Fact]
    public async Task A_hosts_header_chosen_by_the_requester_never_ends_up_in_the_link()
    {
        // Host filtering is opened up so the request reaches the endpoint with the hostile header: the link must not depend on it either way.
        (ResetServerFactory factory, _, _) = await Instance(yaml: "server:\n  publicUrl: https://draughtsman.test\n  allowedHosts: ['*']\nemail:\n  provider: file\n  from: noreply@draughtsman.test\n");
        using (factory)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password-reset/request") { Content = JsonContent.Create(new { email = "forgot@example.com" }) };
            request.Headers.Host = "evil.example.net";
            request.Headers.Add("X-Forwarded-Host", "evil.example.net");
            request.Headers.Add("Origin", "https://evil.example.net");

            await factory.CreateClient().SendAsync(request);
            await factory.WaitForMail();

            OutboxMessage mail = Assert.Single(factory.ReadOutbox());
            Assert.DoesNotContain("evil.example.net", mail.Body, StringComparison.Ordinal);
            Assert.StartsWith(ResetServerFactory.PublicUrl + "/reset-password#", mail.Link, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_bad_request_is_a_400_for_everyone_and_an_api_token_can_neither_ask_nor_spend()
    {
        (ResetServerFactory factory, HttpClient admin, string id) = await Instance();
        using (factory)
        {
            HttpClient browser = factory.CreateClient();
            foreach (string bad in new[] { string.Empty, "   ", "no-at-sign", new string('a', 300) + "@example.com" })
            {
                Assert.Equal(HttpStatusCode.BadRequest, (await browser.RequestReset(bad)).StatusCode);
            }

            Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/api/auth/password-reset/request", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);

            (_, string tokenSecret) = await admin.CreateToken("agent");
            (_, string? link) = await admin.CreateLink(id);
            HttpClient viaToken = factory.Bearer(tokenSecret);
            Assert.Equal(HttpStatusCode.Forbidden, (await viaToken.RequestReset("forgot@example.com")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viaToken.Complete(link)).StatusCode);
            // The refused attempt spent nothing.
            Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(link)).StatusCode);
            Assert.Empty(factory.ReadOutbox());
        }
    }

    private const string Owner = "198.51.100.20";
    private const string Attacker = "203.0.113.7";

    private static async Task<bool> SelfServiceFlag(DraughtsmanServerFactory factory)
    {
        JsonNode status = await (await factory.CreateClient().GetAsync("/api/auth/status")).Json();
        return status["passwordReset"]!["selfService"]!.GetValue<bool>();
    }

    private static async Task<double> Timed(Func<Task> action)
    {
        var watch = Stopwatch.StartNew();
        await action();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static async Task<double> Timed(Func<Task<HttpResponseMessage>> action)
    {
        var watch = Stopwatch.StartNew();
        await action();
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Records every message handed to it, optionally slowly or failing, in place of the file provider.</summary>
    private sealed class RecordingSender(TimeSpan delay) : IEmailSender
    {
        private readonly List<EmailMessage> _sent = [];
        private readonly List<EmailMessage> _attempted = [];

        public EmailSendException? Failure { get; init; }

        public IReadOnlyList<EmailMessage> Sent
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent];
                }
            }
        }

        public IReadOnlyList<EmailMessage> Attempted
        {
            get
            {
                lock (_attempted)
                {
                    return [.. _attempted];
                }
            }
        }

        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            lock (_attempted)
            {
                _attempted.Add(message);
            }

            await Task.Delay(delay, cancellationToken);
            if (Failure is not null)
            {
                throw Failure;
            }

            lock (_sent)
            {
                _sent.Add(message);
            }
        }
    }
}
