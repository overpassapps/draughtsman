using System.Net;
using System.Net.Sockets;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Export;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-10: the page Chromium prints is a document's drawing, so it gets no JavaScript, no network, a sandbox where the
/// platform allows one, and none of the server's environment. The browser tests plant a probe on a loopback port and feed
/// the renderer an SVG that tries every way out; a control run proves the probe would have heard an unhardened Chromium.
/// They are reported Skipped where no Chromium is installed, like ExportEndpointTests.
/// </summary>
[Collection(RealChromiumCollection.Name)]
public class ChromiumHardeningTests
{
    [Fact]
    public void The_browser_is_launched_headless_with_the_sandbox_asked_for_and_an_allow_listed_environment()
    {
        string secret = "DRAUGHTSMAN_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(secret, "value-that-must-not-reach-chromium");
        try
        {
            var renderer = new ChromiumPdfRenderer(new ChromiumConfig { Enabled = true }, NullLogger<ChromiumPdfRenderer>.Instance);

            BrowserTypeLaunchPersistentContextOptions withSandbox = renderer.LaunchOptions(sandbox: true);
            BrowserTypeLaunchPersistentContextOptions without = renderer.LaunchOptions(sandbox: false);

            Assert.True(withSandbox.ChromiumSandbox);
            Assert.False(without.ChromiumSandbox);
            Assert.True(withSandbox.Headless);
            Dictionary<string, string> env = withSandbox.Env!.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, env.Keys);
            Assert.DoesNotContain(env.Values, v => v.Contains("must-not-reach-chromium", StringComparison.Ordinal));
            Assert.True(env.ContainsKey("PATH"), "the browser still needs PATH");
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, null);
        }
    }

    [Fact]
    public void The_context_has_no_javascript_no_network_no_downloads_and_no_service_workers()
    {
        BrowserNewContextOptions options = ChromiumPdfRenderer.ContextOptions();

        Assert.False(options.JavaScriptEnabled);
        Assert.True(options.Offline);
        Assert.False(options.AcceptDownloads);
        Assert.Equal(ServiceWorkerPolicy.Block, options.ServiceWorkers);
    }

    [Fact]
    public void The_printed_page_carries_a_policy_that_forbids_script_and_any_request()
    {
        string page = PdfPage.Html("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"/>", new Draughtsman.Mcp.Rendering.PdfOptions());

        Assert.Contains("http-equiv=\"Content-Security-Policy\"", page, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", ChromiumPdfRenderer.PageContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", ChromiumPdfRenderer.PageContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("connect-src", ChromiumPdfRenderer.PageContentSecurityPolicy, StringComparison.Ordinal);
    }

    [RequiresChromiumFact]
    public async Task An_svg_that_tries_to_run_script_or_fetch_from_the_network_does_neither_and_still_prints()
    {
        using var probe = new Probe();
        string svg = HostileSvg(probe.Url);
        var renderer = new ChromiumPdfRenderer(new ChromiumConfig { Enabled = true, Path = ChromiumPath()! }, NullLogger<ChromiumPdfRenderer>.Instance);

        byte[] pdf = await renderer.RenderAsync(svg, null, CancellationToken.None);
        await Task.Delay(1500, CancellationToken.None);

        Assert.True(pdf.AsSpan().StartsWith("%PDF"u8), "the hardened render must still produce a PDF");
        Assert.Empty(probe.Requests);
    }

    [RequiresChromiumFact]
    public async Task Control_an_unhardened_chromium_does_reach_the_probe_so_silence_above_means_something()
    {
        using var probe = new Probe();
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, ExecutablePath = ChromiumPath() });
        IPage page = await browser.NewPageAsync();

        await page.SetContentAsync("<!DOCTYPE html><html><body>" + HostileSvg(probe.Url) + "</body></html>", new PageSetContentOptions { WaitUntil = WaitUntilState.Load });
        await Task.Delay(1500, CancellationToken.None);

        Assert.NotEmpty(probe.Requests);
    }

    [RequiresChromiumFact]
    public async Task The_sandbox_modes_on_and_off_both_print_and_auto_prints_too()
    {
        foreach (string mode in new[] { ChromiumConfig.SandboxOn, ChromiumConfig.SandboxOff, ChromiumConfig.SandboxAuto })
        {
            var renderer = new ChromiumPdfRenderer(new ChromiumConfig { Enabled = true, Path = ChromiumPath()!, Sandbox = mode }, NullLogger<ChromiumPdfRenderer>.Instance);

            byte[] pdf = await renderer.RenderAsync("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"40\" viewBox=\"0 0 100 40\"><text x=\"5\" y=\"20\">Sandbox</text></svg>", null, CancellationToken.None);

            Assert.True(pdf.AsSpan().StartsWith("%PDF"u8), $"sandbox: {mode} did not print");
        }
    }

    private static string HostileSvg(string probeUrl)
    {
        return "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"200\" height=\"120\">" +
               "<script>var x = new XMLHttpRequest(); x.open('GET', '" + probeUrl + "/script'); x.send(); fetch('" + probeUrl + "/fetch');</script>" +
               "<image href=\"" + probeUrl + "/image\" width=\"10\" height=\"10\"/>" +
               "<rect width=\"10\" height=\"10\" fill=\"red\" onload=\"fetch('" + probeUrl + "/onload')\"/>" +
               "<foreignObject width=\"50\" height=\"50\"><img xmlns=\"http://www.w3.org/1999/xhtml\" src=\"" + probeUrl + "/fo\"/></foreignObject>" +
               "<style>rect{fill:url(" + probeUrl + "/css)}</style>" +
               "<text x=\"5\" y=\"100\">Hostile</text></svg>";
    }

    private static string? ChromiumPath()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("DRAUGHTSMAN_TEST_CHROMIUM");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        const string macChrome = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        return File.Exists(macChrome) ? macChrome : null;
    }

    private sealed class RequiresChromiumFactAttribute : FactAttribute
    {
        public RequiresChromiumFactAttribute()
        {
            if (ChromiumPath() is null)
            {
                Skip = "No Chromium executable found; set DRAUGHTSMAN_TEST_CHROMIUM or install Google Chrome on this Mac.";
            }
        }
    }

    /// <summary>A loopback HTTP listener that records every request path it is sent.</summary>
    private sealed class Probe : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<string> _requests = [];

        public Probe()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var reader = new StreamReader(client.GetStream());
                    string? line = await reader.ReadLineAsync(_stop.Token);
                    lock (_requests)
                    {
                        _requests.Add(line ?? string.Empty);
                    }

                    await client.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
                {
                    if (_stop.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }
    }
}
