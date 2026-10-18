using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>Security review fixes not already covered by a more specific test file: F1.2, F4 and F6.</summary>
public class SecurityReviewFixesTests
{
    [Fact]
    public async Task First_run_rejects_a_wrong_or_missing_setup_code_and_creates_nobody()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        factory.Services.GetRequiredService<SetupCodeGate>().Issue();

        HttpResponseMessage missing = await client.PostAsJsonAsync("/api/auth/first-run",
            new { email = "a@example.com", displayName = "A", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);

        HttpResponseMessage wrong = await client.PostAsJsonAsync("/api/auth/first-run",
            new { email = "a@example.com", displayName = "A", password = "correcthorsebattery", setupCode = "wrong-code" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        JsonNode status = await ReadJson(await client.GetAsync("/api/auth/status"));
        Assert.True(status["firstRunRequired"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_setup_code_is_invalidated_by_reissuing_a_new_one()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        string staleCode = factory.Services.GetRequiredService<SetupCodeGate>().Issue();
        factory.Services.GetRequiredService<SetupCodeGate>().Issue(); // simulates a restart printing a fresh code

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/first-run",
            new { email = "a@example.com", displayName = "A", password = "correcthorsebattery", setupCode = staleCode });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Security review F4: the per-IP bucket is a separate, coarser budget than the (email, IP) pair, and a success
    /// never resets it. Spraying unknown emails (each its own pair bucket, so only the IP budget can stop it) with one
    /// correct login in the middle must still be cut off at exactly <see cref="FailedSignInThrottle.IpMaxAttempts"/>
    /// failures: the success refunds only its own charge, it does not wipe the failures already counted.
    /// </summary>
    [Fact]
    public async Task The_per_ip_throttle_bucket_survives_a_successful_login_in_between()
    {
        using var factory = new DraughtsmanServerFactory();
        await factory.CreateAuthenticatedClient();

        for (int i = 0; i < FailedSignInThrottle.IpMaxAttempts; i++)
        {
            if (i == FailedSignInThrottle.IpMaxAttempts / 2)
            {
                HttpResponseMessage success = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword });
                Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            }

            HttpResponseMessage fail = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = $"spray{i}@example.com", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, fail.StatusCode);
        }

        HttpResponseMessage blocked = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "spray-last@example.com", password = "wrong" });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // The IP is exhausted, so even the real admin's correct password is refused from here.
        HttpResponseMessage correct = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword });
        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
    }

    /// <summary>
    /// AU-01: correct logins are never charged. Far more than either budget's worth of successful sign-ins from one
    /// IP (an office NAT, or a proxy not in knownProxies) must all succeed.
    /// </summary>
    [Fact]
    public async Task Many_correct_logins_from_one_ip_never_lock_anyone_out()
    {
        using var factory = new DraughtsmanServerFactory();
        await factory.CreateAuthenticatedClient();

        for (int i = 0; i < FailedSignInThrottle.IpMaxAttempts * 2; i++)
        {
            HttpResponseMessage response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// AU-01, proxy case: many different users behind one address each sign in correctly, and a few typos along the
    /// way, without the shared IP bucket ever tripping; only the IP budget's worth of failures does.
    /// </summary>
    [Fact]
    public async Task Users_behind_one_shared_ip_can_all_sign_in_despite_a_few_typos()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string xsrf = admin.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();
        const int users = 12;
        for (int u = 0; u < users; u++)
        {
            await CreateUser(admin, xsrf, $"user{u}@example.com", $"correcthorsebattery-{u}");
        }

        // Every user signs in twice, and every third sign-in is preceded by one typo: 24 successes, 8 failures.
        int failures = 0;
        for (int round = 0; round < 2; round++)
        {
            for (int u = 0; u < users; u++)
            {
                if ((round * users + u) % 3 == 0)
                {
                    HttpResponseMessage typo = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = $"user{u}@example.com", password = "typo" });
                    Assert.Equal(HttpStatusCode.Unauthorized, typo.StatusCode);
                    failures++;
                }

                HttpResponseMessage ok = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = $"user{u}@example.com", password = $"correcthorsebattery-{u}" });
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            }
        }

        Assert.True(failures < FailedSignInThrottle.IpMaxAttempts);
    }

    /// <summary>
    /// AU-01: wrong passwords against one account still lock that account at <see cref="FailedSignInThrottle.MaxAttempts"/>,
    /// while a different account from the same IP still signs in (the per-account bucket, not the IP, did the blocking).
    /// </summary>
    [Fact]
    public async Task Wrong_passwords_still_lock_one_account_but_not_a_neighbour_on_the_same_ip()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string xsrf = admin.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();
        await CreateUser(admin, xsrf, "victim@example.com", "correcthorsebattery-victim");

        for (int i = 0; i < FailedSignInThrottle.MaxAttempts; i++)
        {
            HttpResponseMessage fail = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "victim@example.com", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, fail.StatusCode);
        }

        HttpResponseMessage locked = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "victim@example.com", password = "correcthorsebattery-victim" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

        HttpResponseMessage neighbour = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword });
        Assert.Equal(HttpStatusCode.OK, neighbour.StatusCode);
    }

    /// <summary>The 429 says what was counted: failed attempts, not sign-ins in general.</summary>
    [Fact]
    public async Task The_429_wording_names_failed_attempts()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        for (int i = 0; i < FailedSignInThrottle.MaxAttempts; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.com", password = "wrong" });
        }

        HttpResponseMessage blocked = await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.com", password = "wrong" });

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        JsonNode body = await ReadJson(blocked);
        Assert.Contains("failed attempts", body["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Security review F4: an absurdly long email must not crash the throttle or the lookup - it is capped before
    /// becoming a dictionary key - and still answers the same generic 401.
    /// </summary>
    [Fact]
    public async Task A_wildly_long_email_is_capped_rather_than_crashing_the_throttle()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        string longEmail = new string('a', 5000) + "@example.com";

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new { email = longEmail, password = "whatever" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Security review F6: a login attempt for an email that does not exist must still pay Argon2's real cost -
    /// proof that Verify actually runs against the dummy hash rather than the call being short-circuited away by
    /// "user is null" before ever touching PasswordHasher. The old code returned in well under a millisecond here;
    /// Argon2id at the configured cost cannot complete that fast, so a generous floor distinguishes the two without
    /// being sensitive to normal machine-to-machine speed variance.
    /// </summary>
    [Fact]
    public async Task Login_for_an_unknown_email_still_pays_argon2s_real_cost()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        // Warms up host startup, JIT and the first EF Core query plan, none of which this is trying to measure.
        await client.GetAsync("/api/health");
        await client.PostAsJsonAsync("/api/auth/login", new { email = "warmup@example.com", password = "irrelevant-password" });
        await client.PostAsync("/api/auth/login", new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"));

        // A malformed body fails model binding before the handler runs at all - the same POST/routing pipeline as a
        // real login, but with no database lookup or Argon2 verify, so it is a fair "fast path" reference for this
        // process and machine rather than an arbitrary constant. The minimum of several samples rather than one,
        // since a single sample is vulnerable to an unrelated GC pause or scheduling hiccup when many tests run
        // concurrently, which would inflate the baseline and could mask the very short-circuit this test looks for.
        var baselineSamples = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var sample = Stopwatch.StartNew();
            HttpResponseMessage malformed = await client.PostAsync("/api/auth/login", new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"));
            sample.Stop();
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            baselineSamples.Add(sample.ElapsedMilliseconds);
        }

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody-at-all@example.com", password = "irrelevant-password" });
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        long baseline = baselineSamples.Min();
        long floor = Math.Max(20, baseline * 3);
        Assert.True(stopwatch.ElapsedMilliseconds >= floor,
            $"Login for an unknown email returned in {stopwatch.ElapsedMilliseconds}ms (the fastest of several malformed-body requests through the same pipeline took {baseline}ms) - too close to baseline for a real Argon2id verify, suggesting it was short-circuited.");
    }

    private static async Task CreateUser(HttpClient admin, string xsrf, string email, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users") { Content = JsonContent.Create(new { email, displayName = email, role = "Editor", password }) };
        request.Headers.Add(AuthTestHelpers.XsrfHeaderName, xsrf);
        HttpResponseMessage response = await admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }
}
