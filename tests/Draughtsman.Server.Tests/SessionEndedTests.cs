using System.Net;
using System.Net.Http.Json;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// QA S9: a person whose session was ended behind their back is told why (a coarse reason header on the 401 and on the
/// status call a reload makes), never which administrator did it, and an unauthenticated caller learns nothing.
/// </summary>
public class SessionEndedTests : IClassFixture<DraughtsmanServerFactory>
{
    private const string Header = "X-Draughtsman-Session-Ended";

    private readonly DraughtsmanServerFactory _factory;

    public SessionEndedTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_password_reset_ends_the_session_with_the_reason_revoked_and_names_nobody()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("s9-reset@example.com");
        HttpClient person = await _factory.SignIn("s9-reset@example.com");
        Assert.Equal(HttpStatusCode.OK, (await person.GetAsync("/api/documents")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{id}", new { password = "a brand new long password" }, admin))).StatusCode);

        HttpResponseMessage refused = await person.GetAsync("/api/documents");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("revoked", refused.Headers.GetValues(Header).Single());

        // A reload asks the anonymous status route, which answers 200 with no user and the same reason.
        HttpResponseMessage status = await person.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("revoked", status.Headers.GetValues(Header).Single());

        // The reason is one bare word: nothing of the admin's identity is in any header or body.
        string everything = string.Join('\n', refused.Headers.Select(h => $"{h.Key}: {string.Join(',', h.Value)}")) + await refused.Content.ReadAsStringAsync() + await status.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AuthTestHelpers.DefaultEmail, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AuthTestHelpers.DefaultDisplayName + "@", everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deactivating_an_account_reports_deactivated_not_revoked()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("s9-deactivated@example.com");
        HttpClient person = await _factory.SignIn("s9-deactivated@example.com");

        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{id}", new { active = false }, admin))).StatusCode);

        HttpResponseMessage refused = await person.GetAsync("/api/documents");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("deactivated", refused.Headers.GetValues(Header).Single());
    }

    [Fact]
    public async Task A_session_past_its_expiry_reports_expired()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("s9-expired@example.com");
        string secret = TokenGenerator.NewApiTokenSecret();
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IUserStore users = scope.ServiceProvider.GetRequiredService<IUserStore>();
            await users.CreateSession(id, TokenGenerator.Hash(secret), DateTimeOffset.UtcNow.AddDays(-31), DateTimeOffset.UtcNow.AddDays(-1));
        }

        HttpClient bare = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/documents");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={secret}");
        HttpResponseMessage response = await bare.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("expired", response.Headers.GetValues(Header).Single());
    }

    [Fact]
    public async Task A_caller_with_no_session_or_an_unknown_one_gets_no_reason()
    {
        await _factory.CreateAuthenticatedClient();
        HttpClient anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        HttpResponseMessage none = await anonymous.GetAsync("/api/documents");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.False(none.Headers.Contains(Header));

        var unknown = new HttpRequestMessage(HttpMethod.Get, "/api/documents");
        unknown.Headers.Add("Cookie", $"{SessionCookie.Name}=this-session-never-existed");
        HttpResponseMessage unknownResponse = await anonymous.SendAsync(unknown);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownResponse.StatusCode);
        Assert.False(unknownResponse.Headers.Contains(Header));
        Assert.False((await anonymous.GetAsync("/api/auth/status")).Headers.Contains(Header));
    }

    [Fact]
    public async Task A_live_session_gets_no_reason()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        HttpResponseMessage ok = await admin.GetAsync("/api/documents");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(ok.Headers.Contains(Header));
        Assert.False((await admin.GetAsync("/api/auth/status")).Headers.Contains(Header));
    }
}
