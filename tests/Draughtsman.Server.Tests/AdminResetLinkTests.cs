using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The administrator-issued reset link: no email involved, the administrator never sees or chooses the person's new password.
/// Every security rule has a test in each direction (the thing it must allow, and the thing it must refuse).
/// </summary>
public class AdminResetLinkTests
{
    private static readonly DateTimeOffset s_start = DateTimeOffset.Parse("2030-01-01T09:00:00Z");

    [Fact]
    public async Task An_administrator_creates_a_link_and_the_person_chooses_their_own_password_with_it()
    {
        using var factory = new ResetServerFactory(ResetServerFactory.EmailYaml);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("forgetful@example.com");
        HttpClient signedInElsewhere = await factory.SignIn("forgetful@example.com");

        (HttpResponseMessage created, string? secret) = await admin.CreateLink(id);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonNode body = await created.Json();
        Assert.StartsWith("dpr_", secret, StringComparison.Ordinal);
        Assert.Equal($"{ResetServerFactory.PublicUrl}/reset-password#token={secret}", body["link"]!.GetValue<string>());
        Assert.Equal(24, body["expiresInHours"]!.GetValue<int>());
        Assert.Equal("forgetful@example.com", body["userEmail"]!.GetValue<string>());
        // The person, not the administrator, picks the password: nothing in the response is one, and the old one still works until the link is used.
        Assert.True(await factory.CanSignIn("forgetful@example.com", AuthBarHelpers.UserPassword));

        HttpResponseMessage done = await factory.CreateClient().Complete(secret);

        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        Assert.True(await factory.CanSignIn("forgetful@example.com", PasswordResetHelpers.NewPassword));
        Assert.False(await factory.CanSignIn("forgetful@example.com", AuthBarHelpers.UserPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, (await signedInElsewhere.GetAsync("/api/documents")).StatusCode);
    }

    [Fact]
    public async Task Without_a_public_address_configured_the_response_carries_the_secret_and_no_link_for_the_page_to_build_from_its_own_origin()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("no-public-url@example.com");

        (HttpResponseMessage created, string? secret) = await admin.CreateLink(id);

        JsonNode body = await created.Json();
        Assert.Null(body["link"]);
        Assert.Equal(secret, body["token"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_link_works_exactly_once()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("once@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        HttpClient browser = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(secret, "the-first-password-chosen")).StatusCode);
        HttpResponseMessage reused = await browser.Complete(secret, "the-second-password-chosen");

        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.True(await factory.CanSignIn("once@example.com", "the-first-password-chosen"));
        Assert.False(await factory.CanSignIn("once@example.com", "the-second-password-chosen"));
    }

    [Fact]
    public async Task A_link_expires_after_twenty_four_hours_and_not_a_minute_before()
    {
        var clock = new TestClock(s_start);
        using var factory = new ResetServerFactory(clock: clock);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("slow@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        (_, string? neverUsed) = await admin.CreateLink(await admin.CreateUser("slow-too@example.com"));
        HttpClient browser = factory.CreateClient();

        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(secret)).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(2));
        HttpResponseMessage late = await browser.Complete(neverUsed);

        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.False(await factory.CanSignIn("slow-too@example.com", PasswordResetHelpers.NewPassword));
    }

    [Fact]
    public async Task The_lifetime_is_configurable_and_clamped_so_a_typo_cannot_make_a_dead_or_immortal_link()
    {
        var clock = new TestClock(s_start);
        using var factory = new ResetServerFactory("auth:\n  passwordReset:\n    adminLinkHours: 2\n", clock);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        (_, string? twoHours) = await admin.CreateLink(await admin.CreateUser("two-hours@example.com"));
        clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().Complete(twoHours)).StatusCode);

        using var zero = new ResetServerFactory("auth:\n  passwordReset:\n    adminLinkHours: 0\n");
        HttpClient zeroAdmin = await zero.CreateAuthenticatedClient();
        (HttpResponseMessage clampedUp, string? secret) = await zeroAdmin.CreateLink(await zeroAdmin.CreateUser("clamped@example.com"));
        Assert.Equal(1, (await clampedUp.Json())["expiresInHours"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NoContent, (await zero.CreateClient().Complete(secret)).StatusCode);

        using var huge = new ResetServerFactory("auth:\n  passwordReset:\n    adminLinkHours: 100000\n");
        HttpClient hugeAdmin = await huge.CreateAuthenticatedClient();
        (HttpResponseMessage clampedDown, _) = await hugeAdmin.CreateLink(await hugeAdmin.CreateUser("clamped-down@example.com"));
        Assert.Equal(168, (await clampedDown.Json())["expiresInHours"]!.GetValue<int>());
    }

    [Fact]
    public async Task Creating_a_new_link_kills_the_earlier_one()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("replaced@example.com");
        (_, string? first) = await admin.CreateLink(id);
        (_, string? second) = await admin.CreateLink(id);
        HttpClient browser = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete(first)).StatusCode);
        Assert.False(await factory.CanSignIn("replaced@example.com", PasswordResetHelpers.NewPassword));
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(second)).StatusCode);

        JsonNode issued = (await admin.SecurityEvents($"?type=password_reset.link_issued&user={id}"))[0]!;
        Assert.Contains("0 earlier", (await admin.SecurityEvents($"?type=password_reset.link_issued&user={id}"))[1]!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("1 earlier", issued["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_link_for_one_person_does_not_touch_another_persons_link()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        (_, string? alice) = await admin.CreateLink(await admin.CreateUser("alice-links@example.com"));
        await admin.CreateLink(await admin.CreateUser("bob-links@example.com"));

        Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().Complete(alice)).StatusCode);
    }

    [Fact]
    public async Task Using_a_link_signs_the_person_out_everywhere_but_leaves_their_api_tokens_working()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("sessions@example.com");
        HttpClient first = await factory.SignIn("sessions@example.com");
        HttpClient second = await factory.SignIn("sessions@example.com");
        (_, string tokenSecret) = await first.CreateToken("an agent");
        Assert.Equal(HttpStatusCode.OK, (await factory.Bearer(tokenSecret).GetAsync("/api/documents")).StatusCode);
        (_, string? secret) = await admin.CreateLink(id);

        Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().Complete(secret)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.Bearer(tokenSecret).GetAsync("/api/documents")).StatusCode);
        string completed = (await admin.SecurityEvents($"?type=password_reset.completed&user={id}"))[0]!["detail"]!.GetValue<string>();
        Assert.Contains("2 session(s) signed out", completed, StringComparison.Ordinal);
        Assert.Contains("API tokens left as they were", completed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_person_signed_in_in_the_same_browser_can_use_their_own_link_with_the_csrf_header_and_is_signed_out_by_it()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("same-browser@example.com");
        HttpClient browser = await factory.SignIn("same-browser@example.com");
        (_, string? secret) = await admin.CreateLink(id);

        // The session cookie rides along, so CSRF applies like any other cookie request: without the header it is refused and the link is not spent.
        browser.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);
        HttpResponseMessage refused = await browser.PostAsJsonAsync("/api/auth/password-reset/complete", new { token = secret, newPassword = PasswordResetHelpers.NewPassword });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(await factory.CanSignIn("same-browser@example.com", PasswordResetHelpers.NewPassword));

        HttpClient again = await factory.SignIn("same-browser@example.com");
        HttpResponseMessage done = await again.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/password-reset/complete", new { token = secret, newPassword = PasswordResetHelpers.NewPassword }, again));

        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.GetAsync("/api/documents")).StatusCode);
        Assert.True(await factory.CanSignIn("same-browser@example.com", PasswordResetHelpers.NewPassword));
    }

    [Fact]
    public async Task Using_a_link_does_not_sign_out_anyone_else()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("target@example.com");
        await admin.CreateUser("bystander@example.com");
        HttpClient bystander = await factory.SignIn("bystander@example.com");
        (_, string? secret) = await admin.CreateLink(id);

        await factory.CreateClient().Complete(secret);

        Assert.Equal(HttpStatusCode.OK, (await bystander.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/documents")).StatusCode);
    }

    [Fact]
    public async Task Every_way_a_link_can_be_dead_gets_the_same_answer()
    {
        var clock = new TestClock(s_start);
        using var factory = new ResetServerFactory(clock: clock);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpClient browser = factory.CreateClient();

        string usedId = await admin.CreateUser("used-link@example.com");
        (_, string? used) = await admin.CreateLink(usedId);
        await browser.Complete(used);
        string expiredId = await admin.CreateUser("expired-link@example.com");
        (_, string? expired) = await admin.CreateLink(expiredId);
        string tamperedId = await admin.CreateUser("tampered-link@example.com");
        (_, string? realOne) = await admin.CreateLink(tamperedId);
        string replacedId = await admin.CreateUser("replaced-link@example.com");
        (_, string? replaced) = await admin.CreateLink(replacedId);
        await admin.CreateLink(replacedId);
        clock.Advance(TimeSpan.FromHours(25));
        string flipped = realOne![..^1] + (realOne[^1] == 'A' ? 'B' : 'A');
        string survivorId = await admin.CreateUser("survivor@example.com");
        (_, string? survivor) = await admin.CreateLink(survivorId);

        string[] answers =
        [
            await (await browser.Complete(used)).Problem(),
            await (await browser.Complete(expired)).Problem(),
            await (await browser.Complete(flipped)).Problem(),
            await (await browser.Complete(realOne[..^1])).Problem(),
            await (await browser.Complete(realOne + "x")).Problem(),
            await (await browser.Complete(realOne.ToUpperInvariant())).Problem(),
            await (await browser.Complete(replaced)).Problem(),
            await (await browser.Complete("dpr_" + new string('A', 43))).Problem(),
            await (await browser.Complete("not even close")).Problem(),
            await (await browser.Complete(new string('x', 5000))).Problem(),
            await (await browser.Complete(string.Empty)).Problem(),
            await (await browser.Complete(null)).Problem(),
        ];

        Assert.Single(answers.Distinct());
        Assert.StartsWith("400|Link not valid|This reset link is not valid.", answers[0], StringComparison.Ordinal);
        Assert.False(await factory.CanSignIn("tampered-link@example.com", PasswordResetHelpers.NewPassword));
        // A neighbour's refused attempts spent nothing of a link that is still good.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(survivor)).StatusCode);
    }

    [Fact]
    public async Task A_credential_meant_for_something_else_is_not_a_reset_link_and_a_reset_link_is_not_a_credential()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("wrong-purpose@example.com");
        HttpClient person = await factory.SignIn("wrong-purpose@example.com");
        (_, string apiTokenSecret) = await person.CreateToken("agent");
        string sessionSecret = SessionSecretOf(factory, "wrong-purpose@example.com");
        (_, string? resetSecret) = await admin.CreateLink(id);
        HttpClient browser = factory.CreateClient();

        // Other credentials presented as a reset link.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete(apiTokenSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete(sessionSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete(AuthBarHelpers.UserPassword)).StatusCode);

        // The reset link presented as a bearer token or a session cookie.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Bearer(resetSecret!).GetAsync("/api/documents")).StatusCode);
        var asCookie = new HttpRequestMessage(HttpMethod.Get, "/api/documents");
        asCookie.Headers.Add("Cookie", $"{SessionCookie.Name}={resetSecret}; {SessionCookie.HostName}={resetSecret}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().SendAsync(asCookie)).StatusCode);

        // The link itself is untouched by all of that.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(resetSecret)).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_account_cannot_get_a_link_and_a_link_it_had_dies_with_the_deactivation_even_if_it_is_reactivated()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("deactivated@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{id}", new { active = false }, admin))).StatusCode);

        (HttpResponseMessage refused, _) = await admin.CreateLink(id);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().Complete(secret)).StatusCode);

        await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{id}", new { active = true }, admin));
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().Complete(secret)).StatusCode);
        Assert.False(await factory.CanSignIn("deactivated@example.com", PasswordResetHelpers.NewPassword));
        // Reactivated, a fresh link works.
        (_, string? fresh) = await admin.CreateLink(id);
        Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().Complete(fresh)).StatusCode);
    }

    [Fact]
    public async Task A_link_is_refused_when_its_account_is_deactivated_behind_its_back()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("behind-back@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            // Straight to the store, so the endpoint's own invalidation is out of the picture: the use-time check is what is under test.
            await scope.ServiceProvider.GetRequiredService<IUserStore>().SetUserActive(id, false);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().Complete(secret)).StatusCode);
        JsonNode refused = (await admin.SecurityEvents("?type=password_reset.link_refused"))[0]!;
        Assert.Equal("account deactivated", refused["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Only_a_signed_in_administrator_on_a_session_can_create_a_link()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string targetId = await admin.CreateUser("target-of-others@example.com");
        await admin.CreateUser("just-an-editor@example.com");
        await admin.CreateUser("other-admin@example.com", "Admin");
        HttpClient editor = await factory.SignIn("just-an-editor@example.com");
        HttpClient otherAdmin = await factory.SignIn("other-admin@example.com");
        (_, string adminToken) = await admin.CreateToken("an admin's token");
        (_, string otherAdminToken) = await otherAdmin.CreateToken("another admin's token");

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.CreateLink(targetId)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync($"/api/users/{targetId}/password-reset-link", null)).StatusCode);
        foreach (string secret in new[] { adminToken, otherAdminToken })
        {
            HttpResponseMessage viaToken = await factory.Bearer(secret).PostAsync($"/api/users/{targetId}/password-reset-link", null);
            Assert.True(viaToken.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"a token created a link ({viaToken.StatusCode})");
        }

        Assert.Equal(HttpStatusCode.Created, (await otherAdmin.CreateLink(targetId)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.CreateLink(targetId)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.CreateLink("usr_doesnotexist")).Response.StatusCode);
        // None of the refused attempts left a link behind or a row in the log.
        Assert.Equal(2, (await admin.SecurityEvents("?type=password_reset.link_issued")).Count);
    }

    [Fact]
    public async Task A_cookie_session_without_the_csrf_header_cannot_create_a_link()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("csrf@example.com");
        admin.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);

        HttpResponseMessage response = await admin.PostAsync($"/api/users/{id}/password-reset-link", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await admin.SecurityEvents("?type=password_reset.link_issued"));
    }

    [Fact]
    public async Task The_actor_header_decides_nothing_the_event_names_the_signed_in_administrator()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("header@example.com");

        (HttpResponseMessage created, string? secret) = await admin.CreateLink(id, new Dictionary<string, string> { ["X-Draughtsman-Actor"] = "agent:somebody-else/some-model" });
        HttpResponseMessage completed = await factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/auth/password-reset/complete")
        {
            Content = JsonContent.Create(new { token = secret, newPassword = PasswordResetHelpers.NewPassword }),
            Headers = { { "X-Draughtsman-Actor", "user:" + AuthTestHelpers.DefaultEmail } },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        JsonNode issued = (await admin.SecurityEvents("?type=password_reset.link_issued"))[0]!;
        Assert.Equal(AuthTestHelpers.DefaultEmail, issued["actorLabel"]!.GetValue<string>());
        Assert.DoesNotContain("somebody-else", await admin.WholeSecurityLog(), StringComparison.Ordinal);
        JsonNode done = (await admin.SecurityEvents($"?type=password_reset.completed&user={id}"))[0]!;
        Assert.Equal("header@example.com", done["actorLabel"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_short_password_is_refused_without_spending_the_link_or_saying_whether_the_link_is_good()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("typo@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        HttpClient browser = factory.CreateClient();

        HttpResponseMessage shortOnLiveLink = await browser.Complete(secret, "too short");
        HttpResponseMessage shortOnNothing = await browser.Complete("dpr_nothing", "too short");

        Assert.Equal(HttpStatusCode.BadRequest, shortOnLiveLink.StatusCode);
        Assert.Equal(await shortOnLiveLink.Problem(), await shortOnNothing.Problem());
        Assert.Contains("at least 12 characters", await shortOnLiveLink.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(secret)).StatusCode);
    }

    [Fact]
    public async Task A_unicode_password_and_a_very_long_one_are_stored_exactly_and_sign_in_with_them_works()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        const string Unicode = "pässwörd-日本語-🔐🔐-ñandú";
        // 6,000 characters: as large as the 16 KiB auth body limit allows once the JSON has escaped the accents.
        string veryLong = string.Concat(Enumerable.Repeat("pw-é", 1500));
        string firstId = await admin.CreateUser("unicode@example.com");
        string secondId = await admin.CreateUser("very-long@example.com");
        (_, string? first) = await admin.CreateLink(firstId);
        (_, string? second) = await admin.CreateLink(secondId);

        Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().Complete(first, Unicode)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().Complete(second, veryLong)).StatusCode);

        Assert.True(await factory.CanSignIn("unicode@example.com", Unicode));
        Assert.False(await factory.CanSignIn("unicode@example.com", "passwörd-日本語-🔐🔐-ñandú"));
        Assert.True(await factory.CanSignIn("very-long@example.com", veryLong));
        Assert.False(await factory.CanSignIn("very-long@example.com", veryLong[..^1]));
    }

    [Fact]
    public async Task A_body_over_the_auth_limit_is_refused_before_it_is_read()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient browser = factory.CreateClient();

        HttpResponseMessage response = await browser.Complete("dpr_x", new string('p', 200 * 1024));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task The_secret_is_read_from_the_body_only_never_from_the_address()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("querystring@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        HttpClient browser = factory.CreateClient();

        HttpResponseMessage viaQuery = await browser.PostAsJsonAsync($"/api/auth/password-reset/complete?token={secret}&t={secret}", new { newPassword = PasswordResetHelpers.NewPassword });
        HttpResponseMessage viaHeader = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/auth/password-reset/complete")
        {
            Content = JsonContent.Create(new { newPassword = PasswordResetHelpers.NewPassword }),
            Headers = { { "X-Reset-Token", secret! } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, viaQuery.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, viaHeader.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(secret)).StatusCode);
    }

    [Fact]
    public async Task The_event_log_names_who_for_whom_and_the_expiry_and_never_the_link_or_its_hash()
    {
        var clock = new TestClock(s_start);
        using var factory = new ResetServerFactory(clock: clock);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("logged@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        await factory.CreateClient().Complete(secret);
        await factory.CreateClient().Complete(secret);

        string log = await admin.WholeSecurityLog();
        JsonNode issued = (await admin.SecurityEvents($"?type=password_reset.link_issued&user={id}")).Single()!;

        Assert.Equal(AuthTestHelpers.DefaultEmail, issued["actorLabel"]!.GetValue<string>());
        Assert.Equal("logged@example.com", issued["targetLabel"]!.GetValue<string>());
        Assert.Contains("expires 2030-01-02 09:00:00Z", issued["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret!, log, StringComparison.Ordinal);
        Assert.DoesNotContain(secret![4..], log, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordResetSecretsHash(secret), log, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordResetHelpers.NewPassword, log, StringComparison.Ordinal);
        Assert.Single(await admin.SecurityEvents($"?type=password_reset.link_used&user={id}"));
        Assert.Single(await admin.SecurityEvents($"?type=password_reset.completed&user={id}"));
        JsonNode refused = Assert.Single(await admin.SecurityEvents($"?type=password_reset.link_refused&user={id}"))!;
        Assert.Equal("link already used", refused["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Refusals_record_a_reason_class_that_tells_an_administrator_what_happened()
    {
        var clock = new TestClock(s_start);
        using var factory = new ResetServerFactory(clock: clock);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("reasons@example.com");
        (_, string? expired) = await admin.CreateLink(id);
        clock.Advance(TimeSpan.FromHours(30));
        (_, string? replaced) = await admin.CreateLink(id);
        (_, string? current) = await admin.CreateLink(id);
        HttpClient browser = factory.CreateClient();

        await browser.Complete(expired);
        await browser.Complete(replaced);
        await browser.Complete("dpr_unknown");

        string[] details = (await admin.SecurityEvents("?type=password_reset.link_refused")).Select(e => e!["detail"]!.GetValue<string>()).ToArray();
        Assert.Contains("link not recognised", details);
        Assert.Contains("link ended by a newer link, a password change or a deactivation", details);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(current)).StatusCode);
    }

    [Fact]
    public async Task A_password_change_by_an_administrator_by_the_person_or_from_the_host_shell_kills_an_outstanding_link()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string byAdminId = await admin.CreateUser("set-by-admin@example.com");
        string byPersonId = await admin.CreateUser("set-by-person@example.com");
        (_, string? beforeAdminSet) = await admin.CreateLink(byAdminId);
        (_, string? beforeOwnChange) = await admin.CreateLink(byPersonId);

        await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{byAdminId}", new { password = "set-by-an-administrator" }, admin));
        HttpClient person = await factory.SignIn("set-by-person@example.com");
        await person.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/password", new { currentPassword = AuthBarHelpers.UserPassword, newPassword = "changed-by-the-person" }, person));

        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().Complete(beforeAdminSet)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().Complete(beforeOwnChange)).StatusCode);
        Assert.True(await factory.CanSignIn("set-by-admin@example.com", "set-by-an-administrator"));
        Assert.True(await factory.CanSignIn("set-by-person@example.com", "changed-by-the-person"));
    }

    private static string PasswordResetSecretsHash(string secret)
    {
        return Draughtsman.Server.Auth.PasswordReset.PasswordResetSecrets.Hash(secret);
    }

    /// <summary>The raw session cookie value for <paramref name="email"/>, by signing in and reading Set-Cookie.</summary>
    private static string SessionSecretOf(DraughtsmanServerFactory factory, string email)
    {
        HttpClient client = factory.CreateClient();
        HttpResponseMessage login = client.PostAsJsonAsync("/api/auth/login", new { email, password = AuthBarHelpers.UserPassword }).GetAwaiter().GetResult();
        return login.Cookie(SessionCookie.Name) ?? login.Cookie(SessionCookie.HostName) ?? throw new InvalidOperationException("No session cookie was set.");
    }
}
