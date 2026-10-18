using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Email;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Admin, Email as an editor (docs/api.md "## Email"), against a real in-process SMTP server: a save applies at once with no restart (a
/// "Forgot password?" request after it is delivered to the sink), the saved settings and the form as typed can each be tested, a field
/// draughtsman.yaml sets is locked, the password is write-only and kept, replaced or cleared only by saying so, and nothing an administrator
/// types can forge a header or name a URL where a host belongs. Only an administrator on a browser session may do any of it.
/// </summary>
[Collection(SmtpCollection.Name)]
public class EmailSettingsEndpointTests
{
    private const string Password = "s3cret-smtp-password-must-never-appear";
    private const string PublicUrl = "https://draughtsman.test";

    private static object Form(int port, string? user = null, string? passwordAction = null, string? password = null, string? from = "Draughtsman <noreply@draughtsman.test>", string host = "127.0.0.1", string tls = "none")
    {
        return new
        {
            from,
            host,
            port,
            tls,
            user,
            publicUrl = PublicUrl,
            passwordAction,
            password,
        };
    }

    private static Task<HttpResponseMessage> Put(HttpClient admin, object body)
    {
        return admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Put, "/api/admin/email", body, admin));
    }

    private static Task<HttpResponseMessage> Test(HttpClient admin, object? body = null)
    {
        return admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/email/test", body, admin));
    }

    private static async Task<JsonNode> Get(HttpClient admin)
    {
        return await (await admin.GetAsync("/api/admin/email")).Json();
    }

    // ---- the headline: a save applies with no restart ----------------------------------------------------------------

    [Fact]
    public async Task A_save_applies_at_once_with_no_restart_and_turns_forgot_password_on_and_mail_flows_to_the_server()
    {
        await using var sink = new TestSmtpServer();
        sink.Start();
        using var factory = new ResetServerFactory(spa: true);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpClient anonymous = factory.CreateClient();

        Assert.False((await Get(admin))["configured"]!.GetValue<bool>());
        Assert.False((await anonymous.GetFromJsonAsync<JsonNode>("/api/auth/status"))!["passwordReset"]!["selfService"]!.GetValue<bool>());

        HttpResponseMessage saved = await Put(admin, Form(sink.Port));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonNode body = await saved.Json();
        Assert.True(body["configured"]!.GetValue<bool>());
        Assert.True(body["selfServiceAvailable"]!.GetValue<bool>());
        Assert.True(body["hasSaved"]!.GetValue<bool>());
        Assert.True((await anonymous.GetFromJsonAsync<JsonNode>("/api/auth/status"))!["passwordReset"]!["selfService"]!.GetValue<bool>());

        // The real path: a "Forgot password?" request after the save goes to the sink, through the settings just saved.
        await anonymous.RequestReset(AuthTestHelpers.DefaultEmail);
        await factory.WaitForMail();
        ReceivedMail reset = Assert.Single(sink.Mail);
        Assert.Equal([AuthTestHelpers.DefaultEmail], reset.To);
        Assert.Contains(PublicUrl + "/reset-password#token=dpr_", reset.Body, StringComparison.Ordinal);

        // And a second save is used by the very next send: change the From address, send again.
        await Put(admin, Form(sink.Port, from: "Second <second@draughtsman.test>"));
        JsonNode test = await (await Test(admin)).Json();
        Assert.True(test["ok"]!.GetValue<bool>());
        Assert.Equal("second@draughtsman.test", sink.Mail[^1].From);
    }

    [Fact]
    public async Task Removing_the_saved_settings_applies_at_once_and_switches_forgot_password_off_again()
    {
        await using var sink = new TestSmtpServer();
        sink.Start();
        using var factory = new ResetServerFactory(spa: true);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await Put(admin, Form(sink.Port));
        Assert.True((await factory.CreateClient().GetFromJsonAsync<JsonNode>("/api/auth/status"))!["passwordReset"]!["selfService"]!.GetValue<bool>());

        HttpResponseMessage reset = await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Delete, "/api/admin/email", null, admin));

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        JsonNode body = await reset.Json();
        Assert.False(body["configured"]!.GetValue<bool>());
        Assert.False(body["hasSaved"]!.GetValue<bool>());
        Assert.False((await factory.CreateClient().GetFromJsonAsync<JsonNode>("/api/auth/status"))!["passwordReset"]!["selfService"]!.GetValue<bool>());
        Assert.False(File.Exists(Path.Combine(factory.DataFolder, "email", "settings.json")));
        Assert.Single(await admin.SecurityEvents("?type=email.settings_reset"));
    }

    // ---- testing: the saved settings, and the form as typed without saving ------------------------------------------

    [Fact]
    public async Task Test_saved_settings_sends_one_message_to_the_signed_in_administrator_only()
    {
        await using var sink = new TestSmtpServer();
        sink.Start();
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await Put(admin, Form(sink.Port));

        // A "to" in the body is not read: the test cannot be used to send mail to anyone.
        HttpResponseMessage response = await Test(admin, new { to = "victim@example.com" });

        JsonNode body = await response.Json();
        Assert.True(body["ok"]!.GetValue<bool>());
        ReceivedMail mail = Assert.Single(sink.Mail);
        Assert.Equal([AuthTestHelpers.DefaultEmail], mail.To);
        Assert.DoesNotContain("victim@example.com", mail.Data, StringComparison.Ordinal);
        Assert.Single(await admin.SecurityEvents("?type=email.test_sent"));
    }

    [Fact]
    public async Task Testing_the_form_as_typed_sends_through_those_values_and_stores_nothing()
    {
        await using var sink = new TestSmtpServer(password: Password);
        sink.Start();
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await Test(admin, new { to = "victim@example.com", values = Form(sink.Port, user: "mailer", passwordAction: "replace", password: Password) });

        JsonNode body = await response.Json();
        Assert.True(body["ok"]!.GetValue<bool>(), body.ToJsonString());
        Assert.Contains("Nothing was saved", body["message"]!.GetValue<string>(), StringComparison.Ordinal);
        ReceivedMail mail = Assert.Single(sink.Mail);
        Assert.Equal([AuthTestHelpers.DefaultEmail], mail.To);
        // Nothing stored, nothing changed: the instance still has no email and no file was written.
        JsonNode after = await Get(admin);
        Assert.False(after["configured"]!.GetValue<bool>());
        Assert.False(after["hasSaved"]!.GetValue<bool>());
        Assert.False(after["passwordSet"]!.GetValue<bool>());
        Assert.False(File.Exists(Path.Combine(factory.DataFolder, "email", "settings.json")));
        Assert.Contains("not saved", (await admin.SecurityEvents("?type=email.test_sent"))[0]!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_password_in_the_form_fails_the_test_without_being_saved_and_the_right_one_passes()
    {
        await using var sink = new TestSmtpServer(password: Password);
        sink.Start();
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode wrong = await (await Test(admin, new { values = Form(sink.Port, user: "mailer", passwordAction: "replace", password: "not-the-password") })).Json();
        JsonNode right = await (await Test(admin, new { values = Form(sink.Port, user: "mailer", passwordAction: "replace", password: Password) })).Json();

        Assert.False(wrong["ok"]!.GetValue<bool>());
        Assert.Contains("did not accept the user name or password", wrong["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-password", wrong["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(right["ok"]!.GetValue<bool>());
        Assert.False((await Get(admin))["hasSaved"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Testing_form_values_that_would_be_refused_on_save_is_refused_and_does_not_count_against_the_limit()
    {
        await using var sink = new TestSmtpServer();
        sink.Start();
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Test(admin, new { values = Form(sink.Port, host: "https://evil.example.com") })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await Test(admin, new { values = Form(sink.Port) })).StatusCode);
        Assert.Single(sink.Mail);
    }

    [Fact]
    public async Task Both_kinds_of_test_share_the_limit_so_the_buttons_cannot_hammer_a_mail_server()
    {
        await using var sink = new TestSmtpServer();
        sink.Start();
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await Put(admin, Form(sink.Port));

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Test(admin, i % 2 == 0 ? null : new { values = Form(sink.Port) })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Test(admin)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Test(admin, new { values = Form(sink.Port) })).StatusCode);
        Assert.Equal(5, sink.Mail.Count);
    }

    [Fact]
    public async Task A_server_reply_is_never_shown_back_only_a_short_class_of_failure()
    {
        await using var sink = new TestSmtpServer { RecipientReply = "550 5.1.1 LEAKY-SERVER-REPLY internal host mailhub-17.corp.example rejected you" };
        sink.Start();
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await Put(admin, Form(sink.Port));

        JsonNode body = await (await Test(admin)).Json();

        Assert.False(body["ok"]!.GetValue<bool>());
        string message = body["message"]!.GetValue<string>();
        Assert.Contains("refused the recipient address", message, StringComparison.Ordinal);
        Assert.DoesNotContain("LEAKY-SERVER-REPLY", message, StringComparison.Ordinal);
        Assert.DoesNotContain("mailhub-17", message, StringComparison.Ordinal);
        Assert.DoesNotContain("LEAKY-SERVER-REPLY", await admin.WholeSecurityLog(), StringComparison.Ordinal);
    }

    // ---- the password is write-only, and kept, replaced or cleared only by saying so ---------------------------------

    [Fact]
    public async Task The_password_is_never_returned_logged_recorded_or_written_in_plain_text()
    {
        await using var sink = new TestSmtpServer(password: Password);
        sink.Start();
        var logs = new CapturingLoggerProvider();
        using var factory = new ResetServerFactory(logs: logs);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage saved = await Put(admin, Form(sink.Port, user: "mailer", passwordAction: "replace", password: Password));
        string savedBody = await saved.Content.ReadAsStringAsync();
        string getBody = await (await admin.GetAsync("/api/admin/email")).Content.ReadAsStringAsync();
        await Test(admin);
        await Put(admin, Form(sink.Port, user: "mailer", passwordAction: "replace", password: "x\nbad")); // refused: its error must not echo it either
        HttpResponseMessage refused = await Put(admin, Form(sink.Port, user: "mailer", passwordAction: "replace", password: new string('p', 600)));

        JsonNode state = JsonNode.Parse(getBody)!;
        Assert.True(state["passwordSet"]!.GetValue<bool>());
        Assert.Equal("saved", state["passwordKind"]!.GetValue<string>());
        Assert.Equal("saved on this server (encrypted)", state["passwordSource"]!.GetValue<string>());
        Assert.DoesNotContain("\"password\":", getBody, StringComparison.OrdinalIgnoreCase);
        foreach (string text in new[] { savedBody, getBody, await refused.Content.ReadAsStringAsync(), await admin.WholeSecurityLog(), string.Join('\n', logs.Lines) })
        {
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        }

        string onDisk = File.ReadAllText(Path.Combine(factory.DataFolder, "email", "settings.json"));
        Assert.DoesNotContain(Password, onDisk, StringComparison.Ordinal);
        Assert.Contains("protectedPassword", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keep_replace_and_clear_each_do_exactly_what_they_say()
    {
        await using var sink = new TestSmtpServer(password: Password);
        sink.Start();
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        // Replace with the wrong one: saved, and the saved test fails.
        await Put(admin, Form(sink.Port, user: "mailer", passwordAction: "replace", password: "wrong-password"));
        Assert.False((await (await Test(admin)).Json())["ok"]!.GetValue<bool>());

        // Replace with the right one.
        await Put(admin, Form(sink.Port, user: "mailer", passwordAction: "replace", password: Password));
        Assert.True((await (await Test(admin)).Json())["ok"]!.GetValue<bool>());

        // Keep (explicit, and by leaving the action out) while editing something else: still signs in.
        await Put(admin, Form(sink.Port, user: "mailer", passwordAction: "keep", from: "Kept <kept@draughtsman.test>"));
        await Put(admin, Form(sink.Port, user: "mailer", from: "Kept Again <kept2@draughtsman.test>"));
        JsonNode kept = await (await Test(admin)).Json();
        Assert.True(kept["ok"]!.GetValue<bool>(), kept.ToJsonString());
        Assert.True((await Get(admin))["passwordSet"]!.GetValue<bool>());

        // Clear with the user name still set is refused whole: the old password stays and still works.
        HttpResponseMessage refused = await Put(admin, Form(sink.Port, user: "mailer", passwordAction: "clear"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True((await Get(admin))["passwordSet"]!.GetValue<bool>());
        Assert.True((await (await Test(admin)).Json())["ok"]!.GetValue<bool>());

        // Clear together with dropping the user name: gone, and the relay now sees an anonymous client.
        await Put(admin, Form(sink.Port, user: "", passwordAction: "clear"));
        JsonNode cleared = await Get(admin);
        Assert.False(cleared["passwordSet"]!.GetValue<bool>());
        Assert.Equal("none", cleared["passwordKind"]!.GetValue<string>());
        Assert.Null(cleared["user"]);

        string[] changes = (await admin.SecurityEvents("?type=email.settings_changed")).Select(e => e!["detail"]!.GetValue<string>()).ToArray();
        Assert.Contains(changes, c => c.Contains("password replaced", StringComparison.Ordinal));
        Assert.Contains(changes, c => c.Contains("password cleared", StringComparison.Ordinal));
        Assert.DoesNotContain(changes, c => c.Contains(Password, StringComparison.Ordinal) || c.Contains("127.0.0.1", StringComparison.Ordinal) || c.Contains("wrong-password", StringComparison.Ordinal));
    }

    // ---- what draughtsman.yaml sets is locked --------------------------------------------------------------------

    [Fact]
    public async Task A_field_the_yaml_sets_is_locked_with_its_reason_and_cannot_be_changed_but_the_rest_can()
    {
        await using var sink = new TestSmtpServer();
        sink.Start();
        string yaml = $"""
            email:
              provider: smtp
              from: Draughtsman <files@draughtsman.test>
              smtp:
                host: 127.0.0.1
                port: {sink.Port}
                tls: none
            """;
        using var factory = new ResetServerFactory(yaml);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode state = await Get(admin);
        JsonNode locked = state["locked"]!;
        Assert.Equal(EmailSettings.ReasonInFile, locked["host"]!.GetValue<string>());
        Assert.NotNull(locked["from"]);
        Assert.NotNull(locked["port"]);
        Assert.NotNull(locked["tls"]);
        Assert.NotNull(locked["provider"]);
        Assert.Null(locked["user"]);
        Assert.Null(locked["replyTo"]);
        Assert.Null(locked["publicUrl"]);
        Assert.Null(locked["password"]);

        // A different host is refused by name and nothing changes...
        HttpResponseMessage refused = await Put(admin, Form(sink.Port, host: "elsewhere.example.com", from: "Draughtsman <files@draughtsman.test>"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("The mail server cannot be changed here", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("127.0.0.1", (await Get(admin))["host"]!.GetValue<string>());
        Assert.False((await Get(admin))["hasSaved"]!.GetValue<bool>());

        // ...while the unlocked fields are the administrator's: the public address, and the reply-to.
        HttpResponseMessage ok = await Put(admin, new { host = "127.0.0.1", from = "Draughtsman <files@draughtsman.test>", port = sink.Port, tls = "none", publicUrl = PublicUrl, replyTo = "Help <help@draughtsman.test>" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        JsonNode after = await ok.Json();
        Assert.Equal(PublicUrl, after["publicUrl"]!.GetValue<string>());
        Assert.Equal("Help <help@draughtsman.test>", after["replyTo"]!.GetValue<string>());
        Assert.True(after["selfServiceAvailable"]!.GetValue<bool>());
        JsonNode test = await (await Test(admin)).Json();
        Assert.True(test["ok"]!.GetValue<bool>(), test.ToJsonString());
        Assert.Contains("Reply-To: Help <help@draughtsman.test>", sink.Mail[0].Data.Replace("\"Help\"", "Help", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_password_from_the_environment_or_a_file_is_locked_and_the_page_cannot_replace_or_clear_it()
    {
        string yaml = """
            email:
              provider: smtp
              from: noreply@draughtsman.test
              smtp:
                host: 127.0.0.1
                user: mailer
                tls: none
                passwordFile: smtp-password
            """;
        using var factory = new ResetServerFactory(yaml);
        Directory.CreateDirectory(factory.DataFolder);
        File.WriteAllText(Path.Combine(factory.DataFolder, "smtp-password"), Password + "\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode state = await Get(admin);
        Assert.NotNull(state["locked"]!["password"]);
        Assert.Equal("file", state["passwordKind"]!.GetValue<string>());
        Assert.True(state["passwordSet"]!.GetValue<bool>());
        Assert.DoesNotContain(Password, state.ToJsonString(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, new { passwordAction = "replace", password = "something-else", publicUrl = PublicUrl })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, new { passwordAction = "clear", publicUrl = PublicUrl })).StatusCode);
        Assert.Equal("file", (await Get(admin))["passwordKind"]!.GetValue<string>());
    }

    // ---- what an administrator types is refused ----------------------------------------------------------------------

    [Theory]
    [InlineData("https://smtp.example.com")]
    [InlineData("smtp.example.com/relay")]
    [InlineData("user@smtp.example.com")]
    [InlineData("smtp.example.com:25")]
    [InlineData("smtp.example.com\r\nBcc: attacker@example.com")]
    public async Task A_host_that_is_not_a_plain_host_is_refused_with_a_400_and_nothing_is_saved(string host)
    {
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await Put(admin, Form(25, host: host));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"host\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker", text, StringComparison.Ordinal);
        Assert.False((await Get(admin))["hasSaved"]!.GetValue<bool>());
        Assert.False(File.Exists(Path.Combine(factory.DataFolder, "email", "settings.json")));
    }

    [Theory]
    [InlineData("from", "noreply@draughtsman.test\r\nBcc: attacker@example.com")]
    [InlineData("from", "Name\nBcc: attacker@example.com <noreply@draughtsman.test>")]
    [InlineData("replyTo", "help@draughtsman.test\r\nBcc: attacker@example.com")]
    [InlineData("user", "mailer\r\nMAIL FROM:<attacker@example.com>")]
    public async Task A_line_break_in_a_from_reply_to_or_user_name_is_refused_and_never_reaches_the_wire(string field, string value)
    {
        await using var sink = new TestSmtpServer();
        sink.Start();
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        var body = new JsonObject
        {
            ["from"] = "noreply@draughtsman.test",
            ["host"] = "127.0.0.1",
            ["port"] = sink.Port,
            ["tls"] = "none",
            ["publicUrl"] = PublicUrl,
            [field] = value,
        };

        HttpResponseMessage saved = await Put(admin, body);
        HttpResponseMessage tested = await Test(admin, new { values = body });

        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tested.StatusCode);
        Assert.DoesNotContain("attacker", await saved.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(sink.Mail);
        Assert.Empty(sink.Log);
    }

    [Fact]
    public async Task Encryption_is_starttls_or_none_and_a_password_is_never_sent_unencrypted_to_another_machine()
    {
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage implicitTls = await Put(admin, Form(465, tls: "ssl", host: "smtp.example.com"));
        HttpResponseMessage remoteNone = await Put(admin, Form(25, user: "mailer", passwordAction: "replace", password: Password, host: "smtp.example.com", tls: "none"));

        Assert.Equal(HttpStatusCode.BadRequest, implicitTls.StatusCode);
        Assert.Contains("Implicit TLS on port 465 is not supported", await implicitTls.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, remoteNone.StatusCode);
        Assert.Contains("another machine unencrypted", await remoteNone.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False((await Get(admin))["hasSaved"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_malformed_body_is_a_400_not_a_crash()
    {
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpRequestMessage request = AuthBarHelpers.Request(HttpMethod.Put, "/api/admin/email", null, admin);
        request.Content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await admin.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType, response.StatusCode.ToString());
    }

    // ---- who may do any of it ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_a_cookie_administrator_may_read_change_remove_or_test_email_settings()
    {
        await using var sink = new TestSmtpServer();
        sink.Start();
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await Put(admin, Form(sink.Port));
        await admin.CreateUser("plain-editor@example.com");
        HttpClient editor = await factory.SignIn("plain-editor@example.com");
        (_, string adminToken) = await admin.CreateToken("admin token");
        HttpClient token = factory.Bearer(adminToken);
        HttpClient anonymous = factory.CreateClient();
        object attack = Form(sink.Port, from: "Attacker <attacker@example.com>", host: "attacker.example.com");

        // Editor (a signed-in browser session without the role): forbidden on every verb.
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/admin/email")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(editor, attack)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.SendAsync(AuthBarHelpers.Request(HttpMethod.Delete, "/api/admin/email", null, editor))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Test(editor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Test(editor, new { values = attack })).StatusCode);

        // An administrator's API token is not a browser session: forbidden too, so an agent cannot repoint the instance's mail.
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/admin/email")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PutAsJsonAsync("/api/admin/email", attack)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.DeleteAsync("/api/admin/email")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PostAsync("/api/admin/email/test", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/email")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/admin/email", attack)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/admin/email")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/admin/email/test", null)).StatusCode);

        // And none of it changed anything or sent anything.
        JsonNode after = await Get(admin);
        Assert.Equal("127.0.0.1", after["host"]!.GetValue<string>());
        Assert.True(after["hasSaved"]!.GetValue<bool>());
        Assert.Empty(sink.Mail);
    }

    [Fact]
    public async Task A_change_is_recorded_naming_what_changed_and_never_a_value()
    {
        await using var sink = new TestSmtpServer(password: Password);
        sink.Start();
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        await Put(admin, Form(sink.Port, user: "mailer", passwordAction: "replace", password: Password));
        await Put(admin, Form(sink.Port, user: "mailer")); // nothing changed: nothing recorded

        JsonNode recorded = Assert.Single(await admin.SecurityEvents("?type=email.settings_changed"))!;
        string detail = recorded["detail"]!.GetValue<string>();
        Assert.Contains("email turned on", detail, StringComparison.Ordinal);
        Assert.Contains("password replaced", detail, StringComparison.Ordinal);
        Assert.Equal(AuthTestHelpers.DefaultEmail, recorded["actorLabel"]!.GetValue<string>());
        string log = await admin.WholeSecurityLog();
        Assert.DoesNotContain(Password, log, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("noreply@draughtsman.test", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_saved_settings_folder_is_a_secret_one_that_a_backup_keeps_out_unless_asked()
    {
        Assert.Contains(EmailSettingsStore.DirectoryName, Draughtsman.Server.Backup.BackupLayout.SecretFolders);
    }
}
