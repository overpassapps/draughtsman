using System.Diagnostics;
using System.Net;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Export;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// D3 of the Windows install proof: PDF export through Edge hung for ever under a Windows service, leaving the request, a render slot
/// and a driver process behind per attempt, while health said PDF worked. Now every render and every probe has a hard limit; an
/// overrun stops the processes that render started, by id, and says what to do; health only says PDF works after a page was printed.
/// The browser is stood in for by a subclass whose "render" owns real child processes, so the kill is exercised on real pids.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class PdfTimeoutTests : IDisposable
{
    private readonly List<Process> _spawned = [];

    public void Dispose()
    {
        foreach (Process process in _spawned)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }
    }

    private Process Spawn(string script)
    {
        var start = new ProcessStartInfo("sh", ["-c", script]) { UseShellExecute = false };
        Process process = Process.Start(start)!;
        _spawned.Add(process);
        return process;
    }

    /// <summary>A renderer whose browser is a real child process this test controls. <see cref="Behaviour"/> decides what the "render" does after it adopted it.</summary>
    private sealed class FakeBrowserRenderer : ChromiumPdfRenderer
    {
        public FakeBrowserRenderer(ChromiumConfig config, LimitsConfig? limits = null, DataFolder? data = null)
            : base(config, NullLogger<ChromiumPdfRenderer>.Instance, TimeProvider.System, limits, data)
        {
        }

        /// <summary>The processes this render's fake driver owns (adopted into the tree, as the real driver is).</summary>
        public List<Process> Owned { get; } = [];

        public Func<Task<byte[]>> Behaviour { get; set; } = () => Task.FromResult("%PDF-1.4 fake"u8.ToArray());

        protected override DriverCheck CheckDriver() => new(DriverState.Ready, "/opt/draughtsman", null);

        internal override async Task<byte[]> PrintAsync(string svg, PdfOptions options, DriverCheck driver, OwnedProcessTree processes, CancellationToken abandon)
        {
            foreach (Process process in Owned)
            {
                Assert.True(processes.Adopt(process.Id), "the fake driver must be adoptable");
            }

            return await Behaviour().ConfigureAwait(false);
        }
    }

    private static ChromiumConfig Config(int seconds) => new() { Enabled = true, TimeoutSeconds = seconds };

    [Fact]
    public async Task A_render_that_overruns_stops_the_browser_it_started_by_id_and_nothing_else_and_fails_with_advice()
    {
        // The "browser": a shell with a child of its own (like Edge and its helpers); the bystander has the same program name and is not ours.
        Process browser = Spawn("sleep 300 & wait");
        Process bystander = Spawn("sleep 300");
        await Task.Delay(300);
        IReadOnlyList<int> helpers = ProcessTable.Descendants(ProcessTable.Snapshot(), browser.Id);
        Assert.NotEmpty(helpers);
        var renderer = new FakeBrowserRenderer(Config(2));
        renderer.Owned.Add(browser);
        renderer.Behaviour = async () =>
        {
            await browser.WaitForExitAsync(); // hangs until the processes are stopped, as the Playwright calls do once the browser is gone
            throw new InvalidOperationException("the browser went away");
        };
        var watch = Stopwatch.StartNew();

        PdfTimeoutException timeout = await Assert.ThrowsAsync<PdfTimeoutException>(() => renderer.RenderAsync("<svg/>", null, CancellationToken.None));

        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 15);
        Assert.Contains("did not finish within 2 seconds", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("export.chromium.timeoutSeconds", timeout.Message, StringComparison.Ordinal);
        Assert.True(browser.HasExited, "the browser was left running");
        foreach (int helper in helpers)
        {
            Assert.False(IsRunning(helper), $"the browser's helper process {helper} was left running");
        }

        Assert.False(bystander.HasExited, "a process of the same name that this render did not start was killed");
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task A_render_that_finishes_in_time_returns_the_pdf_and_kills_nothing()
    {
        Process browser = Spawn("sleep 300");
        await Task.Delay(200);
        var renderer = new FakeBrowserRenderer(Config(20));
        renderer.Owned.Add(browser);

        byte[] pdf = await renderer.RenderAsync("<svg/>", null, CancellationToken.None);

        Assert.True(pdf.AsSpan().StartsWith("%PDF"u8));
        Assert.False(browser.HasExited, "a render that finished was stopped as if it had hung");
    }

    [Fact]
    public async Task A_caller_who_gives_up_has_the_render_stopped_instead_of_left_running()
    {
        Process browser = Spawn("sleep 300");
        await Task.Delay(200);
        var renderer = new FakeBrowserRenderer(Config(60));
        renderer.Owned.Add(browser);
        renderer.Behaviour = async () =>
        {
            await browser.WaitForExitAsync();
            throw new InvalidOperationException("the browser went away");
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync("<svg/>", null, cts.Token));

        Assert.True(browser.HasExited, "the abandoned render's browser kept running");
    }

    [Fact]
    public async Task A_hung_render_gives_its_slot_back_so_the_next_render_is_not_refused_as_busy()
    {
        Process browser = Spawn("sleep 300");
        await Task.Delay(200);
        var renderer = new FakeBrowserRenderer(Config(1), new LimitsConfig { PdfConcurrency = 1 });
        renderer.Owned.Add(browser);
        renderer.Behaviour = async () =>
        {
            await browser.WaitForExitAsync();
            throw new InvalidOperationException("the browser went away");
        };
        await Assert.ThrowsAsync<PdfTimeoutException>(() => renderer.RenderAsync("<svg/>", null, CancellationToken.None));

        renderer.Owned.Clear();
        renderer.Behaviour = () => Task.FromResult("%PDF-1.4 again"u8.ToArray());
        byte[] second = await renderer.RenderAsync("<svg/>", null, CancellationToken.None);

        Assert.True(second.AsSpan().StartsWith("%PDF"u8));
    }

    [Fact]
    public void The_limit_is_the_configured_seconds_and_a_zero_or_negative_value_means_the_default()
    {
        Assert.Equal(TimeSpan.FromSeconds(17), new FakeBrowserRenderer(Config(17)).RenderTimeout);
        Assert.Equal(TimeSpan.FromSeconds(ChromiumConfig.DefaultTimeoutSeconds), new FakeBrowserRenderer(Config(0)).RenderTimeout);
        Assert.Equal(TimeSpan.FromSeconds(ChromiumConfig.DefaultTimeoutSeconds), new FakeBrowserRenderer(Config(-5)).RenderTimeout);
        Assert.Equal(60, new ChromiumConfig().TimeoutSeconds);
    }

    // --- health: only a printed page counts ---------------------------------------------------------------------------

    [Fact]
    public async Task Health_says_PDF_works_only_when_a_page_was_printed_and_a_hung_probe_says_it_does_not()
    {
        // Prints: available.
        var printing = new FakeBrowserRenderer(Config(20));
        Assert.True((await printing.ProbeAsync(CancellationToken.None)).Available);

        // Launches (the old, weaker proof) but answers with something that is not a PDF: not available.
        var notPdf = new FakeBrowserRenderer(Config(20)) { Behaviour = () => Task.FromResult("<html>not a pdf</html>"u8.ToArray()) };
        PdfStatus refused = await notPdf.ProbeAsync(CancellationToken.None);
        Assert.False(refused.Available);
        Assert.Equal(PdfProblems.Browser, refused.Problem);

        // Hangs: unavailable within the limit, with the timeout advice, and the browser it started is stopped.
        Process browser = Spawn("sleep 300");
        await Task.Delay(200);
        var hung = new FakeBrowserRenderer(Config(2));
        hung.Owned.Add(browser);
        hung.Behaviour = async () =>
        {
            await browser.WaitForExitAsync();
            throw new InvalidOperationException("gone");
        };
        var watch = Stopwatch.StartNew();
        PdfStatus status = await hung.ProbeAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Contains("did not finish within 2 seconds", status.Hint, StringComparison.Ordinal);
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 15);
        Assert.True(browser.HasExited);
    }

    // --- 504 over HTTP -------------------------------------------------------------------------------------------------

    private sealed class TimingOutRenderer : IPdfRenderer
    {
        public bool IsAvailable => true;

        public Task<PdfStatus> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(PdfStatus.Ready);

        public Task<byte[]> RenderAsync(string svg, PdfOptions? options, CancellationToken cancellationToken) =>
            throw new PdfTimeoutException("PDF export did not finish within 60 seconds, so the browser it was using was stopped.");
    }

    [Fact]
    public async Task The_PDF_route_answers_504_with_the_advice_for_a_hung_browser_not_a_bare_500()
    {
        using var factory = new DraughtsmanServerFactory();
        using var timingOut = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPdfRenderer>();
            services.AddSingleton<IPdfRenderer>(new TimingOutRenderer());
        }));
        HttpClient client = await timingOut.CreateAuthenticatedClient();
        HttpResponseMessage created = await client.PostAsync("/api/documents", new StringContent("{\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]}", System.Text.Encoding.UTF8, "application/json"), CancellationToken.None);
        string id = System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync(CancellationToken.None)).RootElement.GetProperty("id").GetString()!;

        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/pdf", CancellationToken.None);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("PDF export timed out", body, StringComparison.Ordinal);
        Assert.Contains("did not finish within 60 seconds", body, StringComparison.Ordinal);
    }

    // --- the browser's own profile folder ----------------------------------------------------------------------------------

    [Fact]
    public void Each_launch_gets_a_fresh_profile_folder_under_the_data_folder_and_old_leftovers_are_swept_once()
    {
        string data = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            string root = Path.Combine(data, "chromium");
            string stale = Path.Combine(root, "profile-" + Guid.NewGuid().ToString("N"));
            string recent = Path.Combine(root, "profile-" + Guid.NewGuid().ToString("N"));
            string unrelated = Path.Combine(root, "keep-me");
            foreach (string folder in new[] { stale, recent, unrelated })
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "x"), "x");
            }

            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));
            var renderer = new FakeBrowserRenderer(Config(20), data: new DataFolder(data));

            string first = renderer.NewProfileFolder();
            string second = renderer.NewProfileFolder();

            Assert.NotEqual(first, second);
            Assert.StartsWith(root, first, StringComparison.Ordinal);
            Assert.True(Directory.Exists(first));
            Assert.False(Directory.Exists(stale), "a profile left by a crashed run was not swept");
            Assert.True(Directory.Exists(recent), "a profile that may belong to a running render was deleted");
            Assert.True(Directory.Exists(unrelated), "something that is not a profile folder was deleted");
            renderer.DeleteProfileFolder(first);
            Assert.False(Directory.Exists(first));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void A_profile_folder_a_just_stopped_browser_still_holds_is_deleted_on_a_later_try_and_a_stuck_one_never_throws()
    {
        var renderer = new FakeBrowserRenderer(Config(20));
        int calls = 0;

        renderer.DeleteProfileFolder("x", _ => { if (++calls < 4) { throw new IOException("in use"); } }, attempts: 10, pause: TimeSpan.Zero);
        Assert.Equal(4, calls);

        calls = 0;
        renderer.DeleteProfileFolder("x", _ => { calls++; throw new UnauthorizedAccessException("in use"); }, attempts: 5, pause: TimeSpan.Zero);
        Assert.Equal(5, calls);

        calls = 0;
        renderer.DeleteProfileFolder("x", _ => { calls++; throw new DirectoryNotFoundException(); }, attempts: 5, pause: TimeSpan.Zero);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void The_browser_launch_has_no_GPU_runs_on_its_own_profile_and_keeps_the_page_hardening()
    {
        var renderer = new FakeBrowserRenderer(Config(45));

        Microsoft.Playwright.BrowserTypeLaunchPersistentContextOptions options = renderer.LaunchOptions(sandbox: true);

        Assert.Contains("--disable-gpu", options.Args!);
        Assert.Equal(45_000f, options.Timeout);
        Assert.True(options.Headless);
        Assert.True(options.Offline);
        Assert.False(options.JavaScriptEnabled);
        Assert.False(options.AcceptDownloads);
    }
}
