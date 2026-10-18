using System.Net;
using System.Net.Http.Json;
using Draughtsman.Server.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The server's memory of which addresses sent proxy headers and whether it believed them: counted per peer, logged once an hour when a
/// peer that is not trusted sends them (issue: nothing said why every visitor shared one sign-in limit), written to the data folder so a
/// stopped server's picture can go in a support bundle, and never holding more than an address, a count and a scheme word.
/// </summary>
public class ForwardedObservationsTests
{
    private static ForwardedSnapshot Snapshot(string peer, bool trusted, string? xff = null, string? proto = null, string? host = null, string? cf = null)
    {
        return new ForwardedSnapshot(IPAddress.Parse(peer), "http", "localhost", proto, xff, host, cf, null, trusted);
    }

    private static ForwardedObservations Make()
    {
        return new ForwardedObservations(new ManualTime(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Requests_are_counted_per_peer_with_and_without_proxy_headers_and_trust_is_remembered()
    {
        ForwardedObservations observations = Make();

        observations.Record(Snapshot("172.17.0.1", trusted: false, xff: "198.51.100.7", proto: "https"));
        observations.Record(Snapshot("172.17.0.1", trusted: false));
        observations.Record(Snapshot("172.17.0.1", trusted: false, xff: "198.51.100.8", proto: "HTTP", host: "x.example.com", cf: "198.51.100.8"));
        observations.Record(Snapshot("10.0.0.2", trusted: true, xff: "198.51.100.9"));

        ForwardedObservationsReport report = observations.Report();
        PeerObservation docker = report.Peers.Single(p => p.Peer == "172.17.0.1");
        PeerObservation proxy = report.Peers.Single(p => p.Peer == "10.0.0.2");

        Assert.False(docker.Trusted);
        Assert.Equal(3, docker.Requests);
        Assert.Equal(2, docker.ForwardedRequests);
        Assert.Equal(2, docker.WithProto);
        Assert.Equal(2, docker.WithFor);
        Assert.Equal(1, docker.WithHost);
        Assert.Equal(1, docker.WithCfConnectingIp);
        Assert.Equal(["http", "https"], docker.ProtoValues);
        Assert.True(proxy.Trusted);
        Assert.Equal(1, proxy.ForwardedRequests);
        Assert.Equal(0, proxy.WithProto);
    }

    [Fact]
    public void Only_a_scheme_word_is_kept_from_the_proto_header_never_its_text()
    {
        ForwardedObservations observations = Make();

        observations.Record(Snapshot("203.0.113.5", trusted: false, proto: "javascript:alert(1)"));
        observations.Record(Snapshot("203.0.113.5", trusted: false, proto: "https, http"));

        PeerObservation peer = Assert.Single(observations.Report().Peers);
        Assert.Equal(["https", "other"], peer.ProtoValues);
    }

    [Fact]
    public void A_scan_from_many_addresses_cannot_grow_the_table_without_limit()
    {
        ForwardedObservations observations = Make();

        for (int i = 0; i < 500; i++)
        {
            observations.Record(Snapshot($"198.51.{i / 250}.{i % 250 + 1}", trusted: false, xff: "1.2.3.4"));
        }

        ForwardedObservationsReport report = observations.Report();
        Assert.Equal(ForwardedObservations.MaxPeers, report.Peers.Count);
        Assert.Equal(500 - ForwardedObservations.MaxPeers, report.OtherPeerRequests);
    }

    [Fact]
    public void The_picture_is_written_to_the_data_folder_and_read_back_and_holds_no_header_content()
    {
        ForwardedObservations observations = Make();
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            observations.Record(Snapshot("172.17.0.1", trusted: false, xff: "198.51.100.7 SECRET-IN-A-HEADER", proto: "https", host: "evil.example.com", cf: "198.51.100.7"));

            ForwardedObservations.Write(folder, observations.Report());
            ForwardedObservationsReport? back = ForwardedObservations.Read(folder);
            string text = File.ReadAllText(Path.Combine(folder, ForwardedObservations.FolderName, ForwardedObservations.FileName));

            Assert.NotNull(back);
            Assert.Equal("172.17.0.1", Assert.Single(back.Peers).Peer);
            Assert.DoesNotContain("SECRET-IN-A-HEADER", text, StringComparison.Ordinal);
            Assert.DoesNotContain("evil.example.com", text, StringComparison.Ordinal);
            Assert.DoesNotContain("198.51.100.7", text, StringComparison.Ordinal);
            Assert.Null(ForwardedObservations.Read(Path.Combine(folder, "nowhere")));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_running_server_keeps_the_count_per_peer_and_whether_the_peer_was_trusted_and_logs_nothing_of_its_own_about_it()
    {
        // The hourly Warning about an untrusted proxy is UntrustedForwardedHeadersMiddleware's (UntrustedForwardedHeadersTests); the observations are counts only.
        var logs = new CapturingLoggerProvider();
        using var untrusted = new FakeIpServerFactory(logs: logs);
        HttpClient client = untrusted.CreateClient();
        for (int i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
            request.Headers.TryAddWithoutValidation(FakeIpServerFactory.Header, "172.17.0.1");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.7");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            (await client.SendAsync(request)).EnsureSuccessStatusCode();
        }

        PeerObservation seen = untrusted.Services.GetRequiredService<ForwardedObservations>().Report().Peers.Single(p => p.Peer == "172.17.0.1");
        Assert.Equal(3, seen.ForwardedRequests);
        Assert.False(seen.Trusted);
        Assert.DoesNotContain(logs.Lines, l => l.Category == "Draughtsman.Server.Proxy");

        using var trusted = new FakeIpServerFactory("server:\n  knownProxies:\n    - 172.17.0.0/16\n");
        HttpClient trustedClient = trusted.CreateClient();
        using var believed = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        believed.Headers.TryAddWithoutValidation(FakeIpServerFactory.Header, "172.17.0.1");
        believed.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.7");
        (await trustedClient.SendAsync(believed)).EnsureSuccessStatusCode();

        Assert.True(trusted.Services.GetRequiredService<ForwardedObservations>().Report().Peers.Single(p => p.Peer == "172.17.0.1").Trusted);
    }

    [Theory]
    [InlineData("X-Real-IP")]
    [InlineData("True-Client-IP")]
    [InlineData("CF-Connecting-IP")]
    [InlineData("Forwarded")]
    public void Every_header_the_untrusted_proxy_warning_looks_for_counts_as_a_proxy_header(string header)
    {
        ForwardedSnapshot snapshot = header switch
        {
            "X-Real-IP" => new ForwardedSnapshot(IPAddress.Parse("172.17.0.1"), "http", "localhost", null, null, null, null, null, false, XRealIp: "198.51.100.7"),
            "True-Client-IP" => new ForwardedSnapshot(IPAddress.Parse("172.17.0.1"), "http", "localhost", null, null, null, null, null, false, TrueClientIp: "198.51.100.7"),
            "CF-Connecting-IP" => Snapshot("172.17.0.1", false, cf: "198.51.100.7"),
            _ => new ForwardedSnapshot(IPAddress.Parse("172.17.0.1"), "http", "localhost", null, null, null, null, "for=198.51.100.7", false),
        };

        Assert.True(snapshot.HasForwardedHeaders);
        Assert.False(Snapshot("172.17.0.1", false).HasForwardedHeaders);
    }

    [Fact]
    public async Task The_flusher_writes_the_picture_when_the_host_stops()
    {
        string folder;
        using (var factory = new FakeIpServerFactory())
        {
            folder = factory.DataFolder;
            HttpClient client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
            request.Headers.TryAddWithoutValidation(FakeIpServerFactory.Header, "172.17.0.9");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.7");
            (await client.SendAsync(request)).EnsureSuccessStatusCode();
            Assert.Null(ForwardedObservations.Read(folder));

            // Stopping the host is what writes it; disposing the factory does that, so read the file from a copy of the path afterwards.
            await factory.Services.GetRequiredService<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>().OfType<ForwardedObservationsFlusher>().Single().StopAsync(CancellationToken.None);
            ForwardedObservationsReport? written = ForwardedObservations.Read(folder);
            Assert.NotNull(written);
            Assert.Contains(written.Peers, p => p.Peer == "172.17.0.9" && p.ForwardedRequests == 1);
        }
    }

    [Fact]
    public async Task The_json_the_status_page_serves_carries_no_header_content()
    {
        using var factory = new FakeIpServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/status");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.7 SECRET-IN-A-HEADER");

        string raw = await (await admin.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("SECRET-IN-A-HEADER", raw, StringComparison.Ordinal);
    }
}
