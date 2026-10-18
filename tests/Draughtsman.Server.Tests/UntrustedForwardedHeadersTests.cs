using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Behind a proxy the server does not trust (<c>server.knownProxies</c> unset), every visitor shares the proxy's address, so the sign-in
/// throttle's per-address budget belongs to everybody at once. The server cannot know the proxy is a proxy, but it can see forwarding
/// headers arrive from a peer it does not believe, and say so: once an hour, naming the peer and the setting, and never repeating
/// anything the request said about itself.
/// </summary>
public class UntrustedForwardedHeadersTests
{
    private const string DockerGateway = "172.17.0.1";

    private static readonly DateTimeOffset Start = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Forwarding_headers_from_an_untrusted_peer_are_warned_about_once_naming_the_peer_and_the_setting_and_nothing_the_request_said()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new ResetServerFactory(logs: logs, clock: new TestClock(Start));

        await Get(factory, DockerGateway, ("X-Forwarded-For", "198.51.100.77, 203.0.113.9"), ("X-Forwarded-Proto", "https"), ("X-Forwarded-Host", "evil.example"));

        List<CapturingLoggerProvider.LogLine> warnings = ForwardingWarnings(logs);
        CapturingLoggerProvider.LogLine warning = Assert.Single(warnings);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(DockerGateway, warning.Message, StringComparison.Ordinal);
        Assert.Contains("server.knownProxies", warning.Message, StringComparison.Ordinal);
        // No header value from the request may be echoed into a log line.
        Assert.DoesNotContain("198.51.100.77", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_warns_once_an_hour_not_once_a_request_and_again_when_the_hour_has_passed()
    {
        var logs = new CapturingLoggerProvider();
        var clock = new TestClock(Start);
        using var factory = new ResetServerFactory(logs: logs, clock: clock);

        for (int i = 0; i < 5; i++)
        {
            await Get(factory, DockerGateway, ("X-Forwarded-For", "198.51.100.7"));
        }

        Assert.Single(ForwardingWarnings(logs));

        clock.Advance(TimeSpan.FromMinutes(59));
        await Get(factory, "172.17.0.9", ("X-Forwarded-For", "198.51.100.7"));
        Assert.Single(ForwardingWarnings(logs));

        clock.Advance(TimeSpan.FromMinutes(2));
        await Get(factory, "172.17.0.9", ("X-Forwarded-For", "198.51.100.7"));
        List<CapturingLoggerProvider.LogLine> after = ForwardingWarnings(logs);
        Assert.Equal(2, after.Count);
        Assert.Contains("172.17.0.9", after[1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("X-Forwarded-For", "198.51.100.7")]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("X-Forwarded-Host", "app.example")]
    [InlineData("CF-Connecting-IP", "198.51.100.7")]
    [InlineData("X-Real-IP", "198.51.100.7")]
    [InlineData("Forwarded", "for=198.51.100.7;proto=https")]
    public async Task Each_kind_of_forwarding_header_counts(string header, string value)
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new ResetServerFactory(logs: logs, clock: new TestClock(Start));

        await Get(factory, DockerGateway, (header, value));

        Assert.Single(ForwardingWarnings(logs));
    }

    [Fact]
    public async Task A_trusted_peer_a_loopback_peer_and_a_request_with_no_forwarding_headers_say_nothing()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new ResetServerFactory("server:\n  knownProxies:\n    - 172.17.0.0/16\n", new TestClock(Start), logs: logs);

        await Get(factory, DockerGateway, ("X-Forwarded-For", "198.51.100.7"), ("X-Forwarded-Proto", "https"));
        await Get(factory, "127.0.0.1", ("X-Forwarded-For", "198.51.100.7"));
        await Get(factory, "10.9.9.9");

        Assert.Empty(ForwardingWarnings(logs));

        // And the same peer, once it is no longer listed, is the one warned about (the rule can fail).
        await Get(factory, "10.9.9.9", ("X-Forwarded-For", "198.51.100.7"));
        Assert.Single(ForwardingWarnings(logs));
    }

    private static List<CapturingLoggerProvider.LogLine> ForwardingWarnings(CapturingLoggerProvider logs)
    {
        return logs.Lines.Where(l => l.Level == LogLevel.Warning && l.Message.Contains("knownProxies", StringComparison.Ordinal) && l.Message.Contains("forward", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static async Task Get(ResetServerFactory factory, string peer, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add(ResetServerFactory.IpHeader, peer);
        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using HttpResponseMessage response = await factory.CreateClient().SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"GET /api/health from {peer} answered {(int)response.StatusCode}");
    }
}
