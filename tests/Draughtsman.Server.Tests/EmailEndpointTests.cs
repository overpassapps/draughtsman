using System.Net;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

/// <summary>The admin Email page's two calls: what email is set to (never the password) and the test message that goes only to the signed-in administrator.</summary>
public class EmailEndpointTests
{
    private const string SmtpPassword = "the-smtp-password-must-never-appear";

    [Fact]
    public async Task The_page_describes_the_configuration_and_never_the_smtp_password()
    {
        using var factory = new ResetServerFactory(SmtpYaml(25999));
        Directory.CreateDirectory(factory.DataFolder);
        File.WriteAllText(Path.Combine(factory.DataFolder, "smtp-password"), SmtpPassword + "\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await admin.GetAsync("/api/admin/email");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SmtpPassword, raw, StringComparison.Ordinal);
        JsonNode body = JsonNode.Parse(raw)!;
        Assert.True(body["configured"]!.GetValue<bool>());
        Assert.Equal("smtp", body["provider"]!.GetValue<string>());
        Assert.Equal("127.0.0.1", body["host"]!.GetValue<string>());
        Assert.Equal(25999, body["port"]!.GetValue<int>());
        Assert.Equal("starttls", body["tls"]!.GetValue<string>());
        Assert.Equal("mailer", body["user"]!.GetValue<string>());
        Assert.Equal("file smtp-password", body["passwordSource"]!.GetValue<string>());
        Assert.Equal("https://draughtsman.test", body["publicUrl"]!.GetValue<string>());
        Assert.True(body["selfServiceAvailable"]!.GetValue<bool>());
        Assert.Equal(60, body["emailedLinkMinutes"]!.GetValue<int>());
        Assert.Equal(24, body["adminLinkHours"]!.GetValue<int>());
        Assert.Empty(body["problems"]!.AsArray());
    }

    [Fact]
    public async Task The_page_says_what_is_missing_when_email_is_half_set_up()
    {
        using var noAddress = new ResetServerFactory("email:\n  provider: file\n  from: noreply@draughtsman.test\n");
        HttpClient admin = await noAddress.CreateAuthenticatedClient();
        JsonNode body = await (await admin.GetAsync("/api/admin/email")).Json();
        Assert.True(body["configured"]!.GetValue<bool>());
        Assert.False(body["selfServiceAvailable"]!.GetValue<bool>());
        Assert.Contains("public address is not set", body["problems"]![0]!.GetValue<string>(), StringComparison.Ordinal);

        using var broken = new ResetServerFactory("email:\n  provider: smtp\n  from: nope\n");
        HttpClient brokenAdmin = await broken.CreateAuthenticatedClient();
        JsonNode brokenBody = await (await brokenAdmin.GetAsync("/api/admin/email")).Json();
        Assert.False(brokenBody["configured"]!.GetValue<bool>());
        string[] problems = brokenBody["problems"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray();
        Assert.Contains(problems, p => p.Contains("email.from", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("email.smtp.host is not set", StringComparison.Ordinal));

        using var none = new DraughtsmanServerFactory();
        HttpClient noneAdmin = await none.CreateAuthenticatedClient();
        JsonNode noneBody = await (await noneAdmin.GetAsync("/api/admin/email")).Json();
        Assert.False(noneBody["configured"]!.GetValue<bool>());
        Assert.Null(noneBody["provider"]);
    }

    [Fact]
    public async Task The_test_message_goes_to_the_administrators_own_address_and_is_recorded()
    {
        using var factory = new ResetServerFactory(ResetServerFactory.EmailYaml);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/email/test", new { to = "someone-else@example.com" }, admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await response.Json();
        Assert.True(body["ok"]!.GetValue<bool>());
        Assert.Contains($"Test email to {AuthTestHelpers.DefaultEmail}", body["message"]!.GetValue<string>(), StringComparison.Ordinal);
        OutboxMessage mail = Assert.Single(factory.ReadOutbox());
        Assert.Equal(AuthTestHelpers.DefaultEmail, mail.To);
        Assert.Contains("test message", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("reset-password", mail.Body, StringComparison.Ordinal);
        Assert.Single(await admin.SecurityEvents("?type=email.test_sent"));
    }

    [Fact]
    public async Task A_failed_test_reports_the_precise_failure_and_not_the_password()
    {
        int closedPort;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using var factory = new ResetServerFactory(SmtpYaml(closedPort));
        Directory.CreateDirectory(factory.DataFolder);
        File.WriteAllText(Path.Combine(factory.DataFolder, "smtp-password"), SmtpPassword);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode body = await (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/email/test", null, admin))).Json();

        Assert.False(body["ok"]!.GetValue<bool>());
        string message = body["message"]!.GetValue<string>();
        Assert.Contains($"Could not connect to 127.0.0.1:{closedPort}", message, StringComparison.Ordinal);
        Assert.DoesNotContain(SmtpPassword, message, StringComparison.Ordinal);
        JsonNode failed = Assert.Single(await admin.SecurityEvents("?type=email.test_failed"))!;
        Assert.Contains("Could not connect", failed["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(SmtpPassword, await admin.WholeSecurityLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testing_without_email_configured_says_so_instead_of_pretending()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode body = await (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/email/test", null, admin))).Json();

        Assert.False(body["ok"]!.GetValue<bool>());
        Assert.Contains("not configured", body["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("email.provider", body["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_cookie_administrator_sees_the_page_or_sends_a_test()
    {
        using var factory = new ResetServerFactory(ResetServerFactory.EmailYaml);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await admin.CreateUser("plain-editor@example.com");
        HttpClient editor = await factory.SignIn("plain-editor@example.com");
        (_, string adminToken) = await admin.CreateToken("admin token");

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/admin/email")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/email/test", null, editor))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/admin/email")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync("/api/admin/email/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.Bearer(adminToken).GetAsync("/api/admin/email")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.Bearer(adminToken).PostAsync("/api/admin/email/test", null)).StatusCode);
        Assert.Empty(factory.ReadOutbox());
    }

    [Fact]
    public async Task Tests_are_limited_so_the_button_cannot_hammer_a_mail_server()
    {
        using var factory = new ResetServerFactory(ResetServerFactory.EmailYaml);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/email/test", null, admin))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/email/test", null, admin))).StatusCode);
        Assert.Equal(5, factory.ReadOutbox().Count);
    }

    private static string SmtpYaml(int port)
    {
        return $"""
            server:
              publicUrl: https://draughtsman.test
            email:
              provider: smtp
              from: Draughtsman <noreply@draughtsman.test>
              smtp:
                host: 127.0.0.1
                port: {port}
                tls: starttls
                user: mailer
                passwordFile: smtp-password
                timeoutSeconds: 30
            """;
    }
}
