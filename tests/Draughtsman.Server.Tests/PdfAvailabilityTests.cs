using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Export;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// QA-02: PDF is reported available only once a browser launch has succeeded, probed lazily and cached for a short time. The
/// launch is stood in for by a subclass, so these run without Chromium; <see cref="PdfHealthTests"/> drives the real launch.
/// </summary>
public class PdfAvailabilityTests
{
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ScriptedRenderer : ChromiumPdfRenderer
    {
        public ScriptedRenderer(ChromiumConfig config, TimeProvider clock)
            : base(config, NullLogger<ChromiumPdfRenderer>.Instance, clock)
        {
        }

        public bool LaunchWorks { get; set; }

        /// <summary>When set, the launch runs out of its time limit (a slow, loaded host) instead of failing outright.</summary>
        public bool LaunchTimesOut { get; set; }

        public int Launches { get; private set; }

        /// <summary>What the driver check reports; null leaves the real check (the test output's own .playwright folder).</summary>
        public DriverCheck? Driver { get; set; }

        /// <summary>When set, the launch fails the way Playwright does when its driver process cannot start, before any browser is tried.</summary>
        public Exception? DriverStartFailure { get; set; }

        protected override DriverCheck CheckDriver() => Driver ?? base.CheckDriver();

        protected override Task ProbeLaunchAsync(CancellationToken cancellationToken)
        {
            Launches++;
            if (DriverStartFailure is not null)
            {
                throw new DriverStartException(DriverStartFailure);
            }

            if (LaunchTimesOut)
            {
                throw new PdfTimeoutException("PDF export did not finish in time");
            }

            return LaunchWorks ? Task.CompletedTask : throw new InvalidOperationException("Executable doesn't exist");
        }
    }

    private static DriverCheck Broken(DriverState state, string detail) => new(state, "/opt/draughtsman", detail);

    private static ScriptedRenderer Enabled(TestClock clock) => new(new ChromiumConfig { Enabled = true }, clock);

    [Fact]
    public async Task Enabled_in_configuration_is_not_available_until_a_launch_has_succeeded()
    {
        var clock = new TestClock();
        ScriptedRenderer renderer = Enabled(clock);

        // The cheap configuration check says yes; the probe, which launches, says no: this gap is QA-02.
        Assert.True(renderer.IsAvailable);
        PdfStatus failed = await renderer.ProbeAsync(CancellationToken.None);
        Assert.False(failed.Available);

        renderer.LaunchWorks = true;
        clock.Advance(ChromiumPdfRenderer.NegativeTtl + TimeSpan.FromSeconds(1));
        Assert.True((await renderer.ProbeAsync(CancellationToken.None)).Available);
    }

    [Fact]
    public async Task A_success_is_cached_for_the_positive_ttl_and_then_probed_again()
    {
        var clock = new TestClock();
        ScriptedRenderer renderer = Enabled(clock);
        renderer.LaunchWorks = true;

        await renderer.ProbeAsync(CancellationToken.None);
        await renderer.ProbeAsync(CancellationToken.None);
        Assert.Equal(1, renderer.Launches);

        clock.Advance(ChromiumPdfRenderer.PositiveTtl - TimeSpan.FromSeconds(1));
        await renderer.ProbeAsync(CancellationToken.None);
        Assert.Equal(1, renderer.Launches);

        clock.Advance(TimeSpan.FromSeconds(2));
        await renderer.ProbeAsync(CancellationToken.None);
        Assert.Equal(2, renderer.Launches);
    }

    [Fact]
    public async Task A_failure_is_cached_briefly_so_polling_cannot_launch_a_failing_browser_continuously()
    {
        var clock = new TestClock();
        ScriptedRenderer renderer = Enabled(clock);

        await renderer.ProbeAsync(CancellationToken.None);
        await renderer.ProbeAsync(CancellationToken.None);
        Assert.Equal(1, renderer.Launches);

        clock.Advance(ChromiumPdfRenderer.NegativeTtl + TimeSpan.FromSeconds(1));
        await renderer.ProbeAsync(CancellationToken.None);
        Assert.Equal(2, renderer.Launches);
    }

    [Fact]
    public async Task A_probe_that_only_ran_out_of_time_is_tried_again_soon_not_reported_unavailable_for_the_full_failure_ttl()
    {
        // Under CPU load a healthy browser can overrun the probe's limit once; that is slowness, not absence, so health must not say "no PDF" for 30 s.
        var clock = new TestClock();
        ScriptedRenderer renderer = Enabled(clock);
        renderer.LaunchTimesOut = true;

        Assert.False((await renderer.ProbeAsync(CancellationToken.None)).Available);
        Assert.False((await renderer.ProbeAsync(CancellationToken.None)).Available);
        Assert.Equal(1, renderer.Launches); // still single-flight and cached for a moment: polling cannot relaunch a hung browser continuously

        clock.Advance(ChromiumPdfRenderer.TimeoutTtl + TimeSpan.FromSeconds(1));
        Assert.True(ChromiumPdfRenderer.TimeoutTtl < ChromiumPdfRenderer.NegativeTtl);
        renderer.LaunchTimesOut = false;
        renderer.LaunchWorks = true;

        Assert.True((await renderer.ProbeAsync(CancellationToken.None)).Available);
        Assert.Equal(2, renderer.Launches);
    }

    [Fact]
    public async Task A_probe_that_failed_for_another_reason_keeps_the_full_failure_ttl()
    {
        var clock = new TestClock();
        ScriptedRenderer renderer = Enabled(clock);

        await renderer.ProbeAsync(CancellationToken.None);
        clock.Advance(ChromiumPdfRenderer.TimeoutTtl + TimeSpan.FromSeconds(1));
        await renderer.ProbeAsync(CancellationToken.None);

        Assert.Equal(1, renderer.Launches);
    }

    [Fact]
    public async Task A_concurrent_burst_of_probes_launches_once()
    {
        ScriptedRenderer renderer = Enabled(new TestClock());
        renderer.LaunchWorks = true;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => renderer.ProbeAsync(CancellationToken.None)));

        Assert.Equal(1, renderer.Launches);
    }

    [Fact]
    public async Task A_disabled_component_is_unavailable_without_launching_anything()
    {
        var renderer = new ScriptedRenderer(new ChromiumConfig { Enabled = false }, new TestClock()) { LaunchWorks = true };

        PdfStatus status = await renderer.ProbeAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Equal(0, renderer.Launches);
    }

    [Fact]
    public async Task The_hint_carries_the_install_command_pinned_to_the_playwright_version_the_server_references()
    {
        PdfStatus status = await Enabled(new TestClock()).ProbeAsync(CancellationToken.None);

        // The version of the driver package Playwright actually ships beside the server (.playwright/package), read from its
        // package.json: the hint must name it, or the CLI installs a different browser revision than the one the server launches.
        string packageJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "package.json"));
        string shipped = System.Text.RegularExpressions.Regex.Match(packageJson, @"""version""\s*:\s*""([^""]+)""").Groups[1].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", shipped);
        Assert.Contains($"npx playwright@{shipped} install chromium", status.Hint, StringComparison.Ordinal);
        Assert.Equal($"npx playwright@{shipped} install chromium", ChromiumPdfRenderer.InstallCommand);
    }

    [Fact]
    public async Task The_hint_never_tells_an_admin_to_enable_what_is_already_enabled_and_says_what_is_missing()
    {
        // Disabled: enabling is the fix.
        PdfStatus disabled = await new ScriptedRenderer(new ChromiumConfig { Enabled = false }, new TestClock()).ProbeAsync(CancellationToken.None);
        Assert.Contains("set export.chromium.enabled to true", disabled.Hint, StringComparison.Ordinal);

        // Enabled, no path, launch fails: the browser is what is missing, and the pinned command is named.
        PdfStatus noBrowser = await Enabled(new TestClock()).ProbeAsync(CancellationToken.None);
        Assert.DoesNotContain("set export.chromium.enabled", noBrowser.Hint, StringComparison.Ordinal);
        Assert.Contains("no Chromium browser was found", noBrowser.Hint, StringComparison.Ordinal);
        Assert.Contains(ChromiumPdfRenderer.InstallCommand, noBrowser.Hint, StringComparison.Ordinal);

        // Enabled with a path that does not exist: the path is named.
        string missing = Path.Combine(Path.GetTempPath(), "no-such-chrome-" + Guid.NewGuid().ToString("N"));
        PdfStatus badPath = await new ScriptedRenderer(new ChromiumConfig { Enabled = true, Path = missing }, new TestClock()).ProbeAsync(CancellationToken.None);
        Assert.Contains(missing, badPath.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("set export.chromium.enabled", badPath.Hint, StringComparison.Ordinal);

        // Enabled with a path that exists but will not launch: says the executable could not be launched.
        string exists = typeof(PdfAvailabilityTests).Assembly.Location;
        PdfStatus wontLaunch = await new ScriptedRenderer(new ChromiumConfig { Enabled = true, Path = exists }, new TestClock()).ProbeAsync(CancellationToken.None);
        Assert.Contains("could not be launched", wontLaunch.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DriverState.NodeMissing, "the Node runtime /opt/draughtsman/.playwright/node/darwin-arm64/node is missing")]
    [InlineData(DriverState.PackageMissing, "the playwright package /opt/draughtsman/.playwright/package/cli.js is missing")]
    [InlineData(DriverState.NotExecutable, "the Node runtime /opt/draughtsman/.playwright/node/darwin-arm64/node is not executable")]
    [InlineData(DriverState.NotFound, "there is no .playwright folder in /opt/draughtsman")]
    public async Task A_missing_or_unusable_driver_is_reported_as_the_driver_and_never_as_the_browser(DriverState state, string detail)
    {
        // QA B1: the archive's driver could not be found, and the hint blamed the browser ("install chromium"), which cannot help.
        ScriptedRenderer renderer = Enabled(new TestClock());
        renderer.LaunchWorks = true;
        renderer.Driver = Broken(state, detail);

        PdfStatus status = await renderer.ProbeAsync(CancellationToken.None);

        Assert.False(status.Available);
        Assert.Equal(PdfProblems.Driver, status.Problem);
        Assert.Contains(detail, status.Hint, StringComparison.Ordinal);
        Assert.Contains("not a browser problem", status.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain(ChromiumPdfRenderer.InstallCommand, status.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("no Chromium browser was found", status.Hint, StringComparison.Ordinal);
        Assert.Equal(0, renderer.Launches);
    }

    [Fact]
    public async Task A_driver_that_will_not_start_is_the_driver_not_the_browser()
    {
        ScriptedRenderer renderer = Enabled(new TestClock());
        renderer.Driver = new DriverCheck(DriverState.Ready, "/opt/draughtsman", null);
        renderer.DriverStartFailure = new InvalidOperationException("Process exited\nwith detail on a second line");

        PdfStatus status = await renderer.ProbeAsync(CancellationToken.None);

        Assert.Equal(PdfProblems.Driver, status.Problem);
        Assert.Contains("could not be started (Process exited)", status.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("second line", status.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_working_driver_with_no_browser_is_the_browser_problem_and_the_other_states_carry_their_own_problem()
    {
        // The other direction of the split: with the driver fine, a failed launch is the browser, and says so.
        ScriptedRenderer noBrowser = Enabled(new TestClock());
        noBrowser.Driver = new DriverCheck(DriverState.Ready, "/opt/draughtsman", null);
        PdfStatus browser = await noBrowser.ProbeAsync(CancellationToken.None);
        Assert.Equal(PdfProblems.Browser, browser.Problem);
        Assert.Contains(ChromiumPdfRenderer.InstallCommand, browser.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("driver is not usable", browser.Hint, StringComparison.Ordinal);

        // A disabled component is its own problem, and a configured path that is not there is the browser's.
        var disabled = new ScriptedRenderer(new ChromiumConfig { Enabled = false }, new TestClock());
        Assert.Equal(PdfProblems.Disabled, (await disabled.ProbeAsync(CancellationToken.None)).Problem);
        string missing = Path.Combine(Path.GetTempPath(), "no-such-chrome-" + Guid.NewGuid().ToString("N"));
        var badPath = new ScriptedRenderer(new ChromiumConfig { Enabled = true, Path = missing }, new TestClock());
        Assert.Equal(PdfProblems.Browser, (await badPath.ProbeAsync(CancellationToken.None)).Problem);
        Assert.Null(PdfStatus.Ready.Problem);
    }

    [Fact]
    public async Task Rendering_with_a_missing_driver_is_refused_with_the_driver_hint_before_any_launch()
    {
        ScriptedRenderer renderer = Enabled(new TestClock());
        renderer.Driver = Broken(DriverState.PackageMissing, "the playwright package /opt/draughtsman/.playwright/package/cli.js is missing");

        PdfUnavailableException refused = await Assert.ThrowsAsync<PdfUnavailableException>(() => renderer.RenderAsync("<svg/>", null, CancellationToken.None));

        Assert.Contains("package/cli.js is missing", refused.Message, StringComparison.Ordinal);
        Assert.Contains("not a browser problem", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, renderer.Launches);
    }

    [Fact]
    public void The_documented_install_command_is_the_one_the_server_reports()
    {
        string doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "pdf-export.md"));
        Assert.Contains(ChromiumPdfRenderer.InstallCommand, doc, StringComparison.Ordinal);
    }

    internal static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Draughtsman.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Draughtsman.sln not found above the test output folder.");
    }
}
