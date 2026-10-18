using System.Net;
using System.Text.Json.Nodes;
using Draughtsman.Server.Auth;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Throttling on using a link: failures are charged per address (and per account-and-address for a dead link), a success hands its
/// charge back, and nobody's repeated guesses can keep the owner of a live link out.
/// </summary>
public class PasswordResetThrottleTests
{
    private const string Attacker = "203.0.113.7";
    private const string Owner = "198.51.100.20";

    [Fact]
    public async Task One_address_guessing_links_is_stopped_and_a_real_owner_elsewhere_is_unaffected()
    {
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("target@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        HttpClient browser = factory.CreateClient();

        for (int i = 0; i < FailedSignInThrottle.IpMaxAttempts; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete("dpr_guess" + i, ip: Attacker)).StatusCode);
        }

        // The budget is spent: even the genuine secret is refused from that address now.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.Complete(secret, ip: Attacker)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.Complete("dpr_another", ip: Attacker)).StatusCode);
        Assert.False(await factory.CanSignIn("target@example.com", PasswordResetHelpers.NewPassword));

        // From anywhere else the owner's link still works: nothing about the account was locked.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(secret, ip: Owner)).StatusCode);
        Assert.True(await factory.CanSignIn("target@example.com", PasswordResetHelpers.NewPassword));
    }

    [Fact]
    public async Task The_throttle_trip_is_recorded_once_however_many_requests_it_refuses()
    {
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpClient browser = factory.CreateClient();
        for (int i = 0; i < FailedSignInThrottle.IpMaxAttempts + 15; i++)
        {
            await browser.Complete("dpr_guess" + i, ip: Attacker);
        }

        JsonArray throttled = await admin.SecurityEvents("?type=sign_in.throttled");

        Assert.Single(throttled);
        Assert.Contains("password reset links", throttled[0]!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        // And the refusals before the trip are the only ones that made rows: the log did not grow with the flood.
        Assert.Equal(FailedSignInThrottle.IpMaxAttempts, (await admin.SecurityEvents("?type=password_reset.link_refused&limit=200")).Count);
    }

    [Fact]
    public async Task A_correct_use_hands_back_its_charge_so_only_failures_stay_counted()
    {
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("refund@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        HttpClient browser = factory.CreateClient();
        for (int i = 0; i < FailedSignInThrottle.IpMaxAttempts - 1; i++)
        {
            await browser.Complete("dpr_guess" + i, ip: Owner);
        }

        // One charge short of the budget: the correct use goes through, and gives its charge back...
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(secret, ip: Owner)).StatusCode);

        // ...so one more failure is still answered as a failure, not as "too many".
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete("dpr_one_more", ip: Owner)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await browser.Complete("dpr_and_now_too_many", ip: Owner)).StatusCode);
    }

    [Fact]
    public async Task Repeating_a_dead_link_cannot_keep_the_owner_of_a_live_one_out_even_from_the_same_address()
    {
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("shared-address@example.com");
        (_, string? dead) = await admin.CreateLink(id);
        (_, string? live) = await admin.CreateLink(id);
        HttpClient browser = factory.CreateClient();

        // Eleven tries of the ended link: past the per-account budget, still refused identically, and the log did not grow with each one.
        string first = await (await browser.Complete(dead, ip: Attacker)).Problem();
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(first, await (await browser.Complete(dead, ip: Attacker)).Problem());
        }

        Assert.True((await admin.SecurityEvents($"?type=password_reset.link_refused&user={id}&limit=200")).Count <= FailedSignInThrottle.MaxAttempts + 1);
        // The live link is not subject to that budget.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(live, ip: Attacker)).StatusCode);
        Assert.True(await factory.CanSignIn("shared-address@example.com", PasswordResetHelpers.NewPassword));
    }

    [Fact]
    public async Task A_short_password_does_not_use_up_the_budget_so_typos_cannot_lock_anyone_out()
    {
        using var factory = new ResetServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("typos@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        HttpClient browser = factory.CreateClient();

        for (int i = 0; i < 3 * FailedSignInThrottle.IpMaxAttempts; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Complete(secret, "short", Owner)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(secret, ip: Owner)).StatusCode);
    }
}
