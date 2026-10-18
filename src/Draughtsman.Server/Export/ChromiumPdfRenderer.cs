using System.Reflection;
using Draughtsman.Core.Hosting;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Configuration;
using Microsoft.Playwright;

namespace Draughtsman.Server.Export;

/// <summary>
/// Turns a document's already-rendered SVG into a single-page PDF through headless Chromium (build-spec-v0.1.md
/// section 7). The component is optional and separately installed: when <see cref="ChromiumConfig.Enabled"/> is
/// false, or no browser can be launched from the configured path or Playwright's own location, <see
/// cref="IsAvailable"/> is false / a launch throws <see cref="PdfUnavailableException"/>, and every PDF caller
/// reports it as absent rather than crashing. <see cref="IsAvailable"/> is only the cheap configuration check; <see
/// cref="ProbeAsync"/> proves a launch works (lazily, cached: <see cref="PositiveTtl"/> on success, <see cref="NegativeTtl"/> on
/// failure) and is what health and the editor report. The render keeps text as vector text (Chromium's print pipeline, not a
/// raster snapshot) over a white page, which is every shipped diagram theme's export background.
/// </summary>
public class ChromiumPdfRenderer : IPdfRenderer
{
    /// <summary>How long a successful launch counts as proof. Long, because a browser that launched once rarely stops working.</summary>
    public static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(5);

    /// <summary>How long a failed probe is remembered: short, so installing the browser is noticed without a restart, but long enough that health polling cannot launch a failing Chromium continuously.</summary>
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    /// <summary>A health probe never waits longer than this, however long a real render may: health must answer.</summary>
    /// <summary>
    /// How long a probe or render that merely ran out of time is remembered: much shorter than <see cref="NegativeTtl"/>, because a
    /// timeout says the host was too slow just then (a loaded machine, a cold first launch of the browser), not that the browser is absent,
    /// so the next health check should try again soon rather than report PDF unavailable for half a minute. Still long enough that a browser
    /// that really hangs is not relaunched on every poll (a probe is single-flight and takes the whole limit to fail).
    /// </summary>
    public static readonly TimeSpan TimeoutTtl = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ProbeTimeoutCap = TimeSpan.FromSeconds(30);

    /// <summary>How long after stopping a hung render's processes to let it unwind (release its slot, delete its profile folder) before moving on.</summary>
    private static readonly TimeSpan UnwindGrace = TimeSpan.FromSeconds(5);

    /// <summary>A profile folder older than this is a leftover of a crashed run and is swept.</summary>
    private static readonly TimeSpan StaleProfileAge = TimeSpan.FromMinutes(15);

    /// <summary>Chromium's own arguments on top of Playwright's defaults: no GPU process (a service has no GPU or desktop to attach one to, and it was among the processes left behind by the hung Edge) and no browser sync.</summary>
    internal static readonly string[] LaunchArguments = ["--disable-gpu", "--disable-sync"];

    private const string ProbeSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\" height=\"40\" viewBox=\"0 0 120 40\"><text x=\"6\" y=\"26\" font-size=\"14\">Draughtsman</text></svg>";

    /// <summary>
    /// The Playwright version this server is built against. The browser revision Playwright launches is tied to it, so the
    /// install command must name the same version (an unpinned CLI installs a different revision and the launch reports "absent").
    /// </summary>
    public static string PlaywrightVersion { get; } = ReadPlaywrightVersion();

    /// <summary>The exact command that installs the browser this server's Playwright library will look for.</summary>
    public static string InstallCommand => $"npx playwright@{PlaywrightVersion} install chromium";

    /// <summary>Tells the caller how to enable the component, for both "disabled" and "browser not found".</summary>
    public static string EnableHint =>
        "server-side PDF export needs the optional Chromium component. " +
        $"Install the browser with `{InstallCommand}`, then set export.chromium.enabled to true in draughtsman.yaml " +
        "(or set export.chromium.path to an existing Chromium or Chrome executable). See docs/pdf-export.md.";

    /// <summary>
    /// What is actually missing, for the state this server is in: not enabled, a configured executable that does not exist or will
    /// not start, or no browser where Playwright looks. Always ends with the pinned install command's context, never tells an
    /// admin to enable what is already enabled.
    /// </summary>
    public string Hint
    {
        get
        {
            if (!_config.Enabled)
            {
                return EnableHint;
            }

            if (!string.IsNullOrWhiteSpace(_config.Path))
            {
                return File.Exists(_config.Path)
                    ? $"server-side PDF export is enabled, but the browser at export.chromium.path ({_config.Path}) could not be launched. Point it at a working Chromium or Chrome executable, or clear it and install Playwright's browser with `{InstallCommand}`."
                    : $"server-side PDF export is enabled, but export.chromium.path points at {_config.Path}, which does not exist. Fix the path, or clear it and install Playwright's browser with `{InstallCommand}`. See docs/pdf-export.md.";
            }

            return $"server-side PDF export is enabled, but no Chromium browser was found for Playwright {PlaywrightVersion}. Install it on the server with `{InstallCommand}` (it is checked again within {(int)NegativeTtl.TotalSeconds} seconds), or set export.chromium.path to an existing Chromium or Chrome executable. A host with no Node can run the same installer with the driver shipped beside the executable. See docs/pdf-export.md.";
        }
    }

    /// <summary>
    /// What is missing when the driver (not the browser) is the problem: names the file, says it is the <c>.playwright</c> folder of the
    /// release archive, and says outright that installing Chromium will not help, so an admin is not sent down the wrong path (QA B1).
    /// </summary>
    internal static string DriverHint(DriverCheck check, string? startFailure = null)
    {
        string what = check.Ready
            ? $"it could not be started ({FirstLine(startFailure ?? "no reason given")})"
            : check.Detail ?? "it was not found";
        return $"server-side PDF export is enabled, but Playwright's driver is not usable on this server: {what}. " +
            "The driver is the .playwright folder that ships beside the server executable (its Node runtime and the playwright package, kept together): " +
            "restore it by unpacking the release archive again, keep the Node file executable, then restart. " +
            "This is not a browser problem, so installing Chromium will not fix it. See docs/pdf-export.md.";
    }

    private string ProblemWhenUnavailable => _config.Enabled ? PdfProblems.Browser : PdfProblems.Disabled;

    /// <summary>The driver check. Virtual so a test can stand in for a broken archive; the default looks for real files and points Playwright at them.</summary>
    protected virtual DriverCheck CheckDriver() => PlaywrightDriver.Prepare();

    /// <summary>Thrown when Playwright's driver process could not be started, as opposed to the browser it would have launched.</summary>
    internal sealed class DriverStartException(Exception inner) : Exception(inner.Message, inner);

    private static async Task<IPlaywright> StartDriverAsync()
    {
        try
        {
            return await Playwright.CreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or IOException or System.ComponentModel.Win32Exception)
        {
            throw new DriverStartException(ex);
        }
    }

    private async Task<IPlaywright> StartDriverOrRefuseAsync(DriverCheck driver, OwnedProcessTree processes, CancellationToken abandon)
    {
        try
        {
            // Through the process tree, so the driver this starts is known by id and can be stopped if the render hangs.
            return await processes.StartAsync(StartDriverAsync, abandon).ConfigureAwait(false);
        }
        catch (DriverStartException ex)
        {
            throw new PdfUnavailableException(DriverStatus(driver, ex).Hint!);
        }
    }

    private PdfStatus DriverStatus(DriverCheck check, DriverStartException? start = null)
    {
        if (start is null)
        {
            _logger.LogWarning("PDF export is enabled but Playwright's driver is not usable: {Detail}", check.Detail);
        }
        else
        {
            _logger.LogWarning(start.InnerException, "PDF export is enabled but Playwright's driver could not be started from {Root}", check.Root);
        }

        return Remember(new PdfStatus(false, DriverHint(check, start?.Message), PdfProblems.Driver));
    }

    private readonly ChromiumConfig _config;
    private readonly ILogger<ChromiumPdfRenderer> _logger;
    private readonly TimeProvider _time;
    private readonly PdfRenderGate _gate;
    private readonly string _profileRoot;
    private int _profilesSwept;
    private int _sandboxRefused;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private PdfStatus? _cached;
    private DateTimeOffset _cachedAt;
    private TimeSpan _cachedTtl;

    /// <param name="limits">Where <see cref="LimitsConfig.PdfConcurrency"/> comes from; the stdio host registers none, and gets the default.</param>
    /// <param name="dataFolder">Where the browser's profile folders go (<c>&lt;data&gt;/chromium</c>); none means the system temp folder.</param>
    public ChromiumPdfRenderer(ChromiumConfig config, ILogger<ChromiumPdfRenderer> logger, TimeProvider? time = null, LimitsConfig? limits = null, DataFolder? dataFolder = null)
    {
        _config = config;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _gate = new PdfRenderGate((limits ?? new LimitsConfig()).PdfConcurrency);
        _profileRoot = Path.Combine(dataFolder?.Path ?? Path.Combine(Path.GetTempPath(), "draughtsman"), "chromium");
    }

    /// <summary>The limit on one render: <see cref="ChromiumConfig.TimeoutSeconds"/>, or the default when it is zero or negative.</summary>
    internal TimeSpan RenderTimeout => TimeSpan.FromSeconds(_config.TimeoutSeconds > 0 ? _config.TimeoutSeconds : ChromiumConfig.DefaultTimeoutSeconds);

    /// <summary>What an admin is told when a render overran its limit and the browser was stopped.</summary>
    internal string TimeoutHint(TimeSpan limit) =>
        $"PDF export did not finish within {(int)limit.TotalSeconds} seconds, so the browser it was using was stopped. A browser that launches but never prints is the usual cause, " +
        $"most often the Microsoft Edge on a Windows service (set export.chromium.path to Playwright's Chromium instead: install it with `{InstallCommand}`, see docs/pdf-export.md). " +
        "A very large diagram can need longer: raise export.chromium.timeoutSeconds.";

    /// <inheritdoc/>
    /// <remarks>A configured path that does not exist counts as absent; an empty path means Playwright's own browser,
    /// which is only known to be present once a launch succeeds (<see cref="ProbeAsync"/>).</remarks>
    public bool IsAvailable => _config.Enabled && (string.IsNullOrWhiteSpace(_config.Path) || File.Exists(_config.Path));

    /// <inheritdoc/>
    public async Task<PdfStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return new PdfStatus(false, Hint, ProblemWhenUnavailable);
        }

        if (Fresh() is { } cached)
        {
            return cached;
        }

        // One probe at a time: health is anonymous and polled, and every launch is a whole browser.
        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh() is { } raced)
            {
                return raced;
            }

            // The driver first: if it is not there, no browser was ever tried, and saying so is the difference between
            // "unpack the archive again" and "install Chromium".
            DriverCheck driver = CheckDriver();
            if (!driver.Ready)
            {
                return DriverStatus(driver);
            }

            try
            {
                await ProbeLaunchAsync(cancellationToken).ConfigureAwait(false);
                return Remember(PdfStatus.Ready);
            }
            catch (DriverStartException ex) when (!cancellationToken.IsCancellationRequested)
            {
                return DriverStatus(driver, ex);
            }
            catch (PdfTimeoutException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("The PDF probe did not finish: {Reason}", ex.Message);
                return Remember(new PdfStatus(false, ex.Message, PdfProblems.Browser), TimeoutTtl);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Chromium could not be launched for PDF export; reporting the component unavailable");
                return Remember(new PdfStatus(false, Hint, PdfProblems.Browser));
            }
        }
        finally
        {
            _probeLock.Release();
        }
    }

    /// <summary>
    /// Prints a tiny page through the same path a real render takes (driver, browser, page, print) under a time limit, and requires a PDF
    /// back: the proof <see cref="ProbeAsync"/> caches. A browser that only launches proves nothing about printing, which is where a hung
    /// one stops. Virtual so a test can stand in for a real browser.
    /// </summary>
    protected virtual async Task ProbeLaunchAsync(CancellationToken cancellationToken)
    {
        DriverCheck driver = CheckDriver();
        if (!driver.Ready)
        {
            throw new InvalidOperationException(driver.Detail ?? "the PDF driver is not usable");
        }

        TimeSpan limit = RenderTimeout < ProbeTimeoutCap ? RenderTimeout : ProbeTimeoutCap;
        byte[] pdf = await RenderBoundedAsync(ProbeSvg, new PdfOptions(), driver, limit, cancellationToken).ConfigureAwait(false);
        if (!pdf.AsSpan().StartsWith("%PDF"u8))
        {
            throw new InvalidOperationException("the browser answered the probe but not with a PDF");
        }
    }

    /// <inheritdoc/>
    public async Task<byte[]> RenderAsync(string svg, PdfOptions? options, CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            throw new PdfUnavailableException(Hint);
        }

        if (!string.IsNullOrWhiteSpace(_config.Path) && !File.Exists(_config.Path))
        {
            _logger.LogWarning("export.chromium.path points at {ChromiumPath}, which does not exist", _config.Path);
            throw new PdfUnavailableException(Hint);
        }

        DriverCheck driver = CheckDriver();
        if (!driver.Ready)
        {
            throw new PdfUnavailableException(DriverStatus(driver).Hint!);
        }

        using IDisposable slot = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        byte[] pdf = await RenderBoundedAsync(svg, options ?? new PdfOptions(), driver, RenderTimeout, cancellationToken).ConfigureAwait(false);
        Remember(PdfStatus.Ready);
        return pdf;
    }

    /// <summary>
    /// Runs <see cref="PrintAsync"/> under <paramref name="limit"/>. Playwright's calls cannot be cancelled, so a render that overruns (or
    /// whose caller left) is ended by stopping the driver and browser it started, by id; the render then fails inside Playwright, unwinds, and
    /// releases what it held. A limit that expires raises <see cref="PdfTimeoutException"/>; a caller who left gets its own cancellation back.
    /// </summary>
    private async Task<byte[]> RenderBoundedAsync(string svg, PdfOptions options, DriverCheck driver, TimeSpan limit, CancellationToken cancellationToken)
    {
        var processes = new OwnedProcessTree(Path.Combine(driver.Root, ".playwright", "node", PlaywrightDriver.PlatformFolder(), PlaywrightDriver.NodeFileName));
        using var abandon = new CancellationTokenSource();
        Task<byte[]> work = Task.Run(() => PrintAsync(svg, options, driver, processes, abandon.Token), CancellationToken.None);
        try
        {
            return await work.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            await abandon.CancelAsync().ConfigureAwait(false);
            IReadOnlyList<int> stopped = processes.KillAll();
            _logger.LogWarning(
                "PDF render {Outcome} after {Seconds:0.#} s; stopped {Count} browser process(es) by id ({Ids})",
                ex is TimeoutException ? "timed out" : "was abandoned by its caller",
                limit.TotalSeconds,
                stopped.Count,
                string.Join(", ", stopped));
            // The render now fails inside Playwright; give it a moment to release its slot and delete its profile folder, and never leave its exception unobserved.
            await Task.WhenAny(work, Task.Delay(UnwindGrace, CancellationToken.None)).ConfigureAwait(false);
            _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (ex is TimeoutException)
            {
                string hint = TimeoutHint(limit);
                Remember(new PdfStatus(false, hint, PdfProblems.Browser), TimeoutTtl);
                throw new PdfTimeoutException(hint);
            }

            throw;
        }
    }

    /// <summary>The Playwright half of a render: start the driver, launch the browser on a profile folder of its own, print, and clean up. Virtual so a test can stand in for a browser that hangs.</summary>
    internal virtual async Task<byte[]> PrintAsync(string svg, PdfOptions options, DriverCheck driver, OwnedProcessTree processes, CancellationToken abandon)
    {
        using IPlaywright playwright = await StartDriverOrRefuseAsync(driver, processes, abandon).ConfigureAwait(false);
        string profile = NewProfileFolder();
        try
        {
            IBrowserContext context;
            try
            {
                context = await LaunchAsync(playwright, profile, abandon).ConfigureAwait(false);
            }
            catch (PlaywrightException ex) when (!abandon.IsCancellationRequested && IsTimeout(ex))
            {
                // The browser did not come up within the launch limit: slow (a loaded host, a cold start), which is not the same as absent,
                // so it is a timeout with the short memory, never "install Chromium".
                _logger.LogWarning(ex, "Chromium did not launch in time for PDF export");
                string hint = TimeoutHint(RenderTimeout);
                Remember(new PdfStatus(false, hint, PdfProblems.Browser), TimeoutTtl);
                throw new PdfTimeoutException(hint);
            }
            catch (PlaywrightException ex) when (!abandon.IsCancellationRequested)
            {
                // Not installed, wrong revision, or an executable that will not start: in every case the component is absent, not broken.
                // (A launch that failed because this render was given up on and its processes stopped is neither: it is not reported at all.)
                _logger.LogWarning(ex, "Chromium could not be launched for PDF export; reporting the component absent");
                Remember(new PdfStatus(false, Hint, PdfProblems.Browser));
                throw new PdfUnavailableException(Hint);
            }

            await using (context.ConfigureAwait(false))
            {
                // Offline already stops every request; aborting them in the route as well means a browser that ignored the flag
                // would still have nothing to fetch with (defence in depth, AU-10).
                await context.RouteAsync("**/*", route => route.AbortAsync()).ConfigureAwait(false);
                IPage page = await context.NewPageAsync().ConfigureAwait(false);

                await page.SetContentAsync(PdfPage.Html(svg, options), new PageSetContentOptions { WaitUntil = WaitUntilState.Load }).ConfigureAwait(false);
                return await page.PdfAsync(new PagePdfOptions { PrintBackground = true, PreferCSSPageSize = true }).ConfigureAwait(false);
            }
        }
        finally
        {
            DeleteProfileFolder(profile);
        }
    }

    /// <summary>A new, empty profile folder for one launch, under <c>&lt;data&gt;/chromium</c>. Leftovers of crashed runs are swept the first time.</summary>
    internal string NewProfileFolder()
    {
        Directory.CreateDirectory(_profileRoot);
        if (Interlocked.Exchange(ref _profilesSwept, 1) == 0)
        {
            SweepStaleProfiles();
        }

        string folder = Path.Combine(_profileRoot, "profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void SweepStaleProfiles()
    {
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(_profileRoot, "profile-*"))
            {
                if (_time.GetUtcNow() - Directory.GetLastWriteTimeUtc(folder) > StaleProfileAge)
                {
                    DeleteProfileFolder(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not sweep old browser profile folders in {Folder}", _profileRoot);
        }
    }

    /// <summary>
    /// Deletes a profile folder, trying again a few times: a browser that was just stopped (a hung render) still holds a file for a moment on
    /// Windows, and one folder per killed render would otherwise sit there until the next start sweeps it. Never throws.
    /// </summary>
    /// <param name="delete">What deletes; a test passes one that fails first.</param>
    internal void DeleteProfileFolder(string folder, Action<string>? delete = null, int attempts = 10, TimeSpan? pause = null)
    {
        delete ??= static path => Directory.Delete(path, recursive: true);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                delete(folder);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == attempts)
                {
                    _logger.LogDebug(ex, "Could not delete the browser profile folder {Folder}; the next start sweeps it", folder);
                    return;
                }

                Thread.Sleep(pause ?? TimeSpan.FromMilliseconds(250));
            }
        }
    }

    /// <summary>
    /// What the printed page may do: paint itself from what is inside it. No script, no request to anywhere: the drawing's
    /// images are data URIs, its fonts are the system's, its style is inline. A third layer under the context's
    /// JavaScript-off and offline settings, and the only one that also covers a Chromium started by some other route.
    /// </summary>
    internal const string PageContentSecurityPolicy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:";

    /// <summary>
    /// Launch settings for the browser on its own profile folder: headless, the sandbox as far as it is wanted, an allow-listed environment so
    /// the browser never sees the server's secrets, the page rules of <see cref="ContextOptions"/> (no JavaScript, no network, no downloads, no
    /// service workers), and a launch limit of the render's own so a browser that never starts cannot outlast it.
    /// </summary>
    internal BrowserTypeLaunchPersistentContextOptions LaunchOptions(bool sandbox)
    {
        BrowserNewContextOptions page = ContextOptions();
        var launch = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = true,
            ChromiumSandbox = sandbox,
            Env = ScrubbedEnvironment.FromCurrentProcess(),
            Args = LaunchArguments,
            Timeout = (float)RenderTimeout.TotalMilliseconds,
            JavaScriptEnabled = page.JavaScriptEnabled,
            Offline = page.Offline,
            AcceptDownloads = page.AcceptDownloads,
            ServiceWorkers = page.ServiceWorkers,
        };
        if (!string.IsNullOrWhiteSpace(_config.Path))
        {
            launch.ExecutablePath = _config.Path;
        }

        return launch;
    }

    /// <summary>The page being printed is a document's drawing, never a web app: no JavaScript, no network, no downloads, no service workers.</summary>
    internal static BrowserNewContextOptions ContextOptions()
    {
        return new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            Offline = true,
            AcceptDownloads = false,
            ServiceWorkers = ServiceWorkerPolicy.Block,
        };
    }

    /// <summary>
    /// Launches with Chromium's sandbox unless <c>export.chromium.sandbox</c> says not to. In <c>auto</c> a refusal (a container
    /// without user namespaces or the seccomp profile the sandbox needs) drops to no sandbox once, loudly, and is
    /// remembered, so the cost of finding out is paid once; in <c>on</c> it is an error, never a quiet downgrade.
    /// </summary>
    private async Task<IBrowserContext> LaunchAsync(IPlaywright playwright, string profile, CancellationToken abandon)
    {
        string mode = _config.Sandbox?.Trim().ToLowerInvariant() ?? ChromiumConfig.SandboxAuto;
        if (mode == ChromiumConfig.SandboxOff || (mode != ChromiumConfig.SandboxOn && Volatile.Read(ref _sandboxRefused) == 1))
        {
            return await playwright.Chromium.LaunchPersistentContextAsync(profile, LaunchOptions(sandbox: false)).ConfigureAwait(false);
        }

        try
        {
            return await playwright.Chromium.LaunchPersistentContextAsync(profile, LaunchOptions(sandbox: true)).ConfigureAwait(false);
        }
        catch (PlaywrightException ex) when (!abandon.IsCancellationRequested && mode != ChromiumConfig.SandboxOn && !IsBrowserNotFound(ex))
        {
            // Not when the render was abandoned: its browser was stopped on purpose, and that "Process exited" says nothing about the sandbox.
            Volatile.Write(ref _sandboxRefused, 1);
            _logger.LogWarning(
                "Chromium would not start with its sandbox ({Reason}); PDF export now runs it without one. The page it prints has no JavaScript and no network access, but for the full protection run the container with a seccomp profile that allows user namespaces, or set export.chromium.sandbox: off to silence this.",
                FirstLine(ex.Message));
            return await playwright.Chromium.LaunchPersistentContextAsync(profile, LaunchOptions(sandbox: false)).ConfigureAwait(false);
        }
    }

    private static string FirstLine(string message)
    {
        int end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }

    /// <summary>Playwright reports its own launch limit as "Timeout 15000ms exceeded." (the exception type is not public in every version).</summary>
    private static bool IsTimeout(PlaywrightException ex)
    {
        return ex.Message.Contains("Timeout", StringComparison.Ordinal) && ex.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowserNotFound(PlaywrightException ex)
    {
        return ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private PdfStatus? Fresh()
    {
        PdfStatus? status = _cached;
        if (status is null)
        {
            return null;
        }

        TimeSpan age = _time.GetUtcNow() - _cachedAt;
        return age < (status.Available ? PositiveTtl : _cachedTtl) ? status : null;
    }

    private PdfStatus Remember(PdfStatus status, TimeSpan? failureTtl = null)
    {
        _cachedAt = _time.GetUtcNow();
        _cachedTtl = failureTtl ?? NegativeTtl;
        _cached = status;
        return status;
    }

    private static string ReadPlaywrightVersion()
    {
        // Microsoft.Playwright's own assembly reports 1.0.0, so the version is the csproj's PlaywrightVersion, baked into this assembly.
        return typeof(ChromiumPdfRenderer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "PlaywrightVersion")?.Value ?? "latest";
    }
}
