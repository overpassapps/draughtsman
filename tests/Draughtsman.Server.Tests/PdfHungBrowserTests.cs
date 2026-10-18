using System.Diagnostics;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Export;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>
/// D3 end to end with a real browser: a Chromium that stops answering (frozen with SIGSTOP, the Unix cousin of the hung Edge on the
/// Windows service) must fail the render within its limit with the timeout advice, leave no browser process of that render
/// behind, leave a bystander browser alone, not be mistaken for a sandbox refusal, and not stop the next render from working.
/// Reported Skipped where no Chromium is installed, like ChromiumHardeningTests.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
[Collection(RealChromiumCollection.Name)]
public sealed class PdfHungBrowserTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
    private readonly List<int> _frozen = [];

    public void Dispose()
    {
        foreach (int pid in _frozen)
        {
            Signal("CONT", pid);
        }

        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [RequiresChromiumFact]
    public async Task A_frozen_browser_times_out_is_stopped_by_id_and_the_next_render_still_works_with_its_sandbox_untouched()
    {
        string chromium = ChromiumLocator.Path()!;
        var logs = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([logs]);
        // 20 s, not the 8 s the first version used: a Chromium launch on a Mac running other builds can take longer than 8 s, and the test
        // needs the render's browser to exist before the limit ends so there is something to freeze. The frozen render still fails at the limit.
        var config = new ChromiumConfig { Enabled = true, Path = chromium, TimeoutSeconds = 20 };
        var renderer = new ChromiumPdfRenderer(config, factory.CreateLogger<ChromiumPdfRenderer>(), TimeProvider.System, null, new DataFolder(_data));
        string profiles = Path.Combine(_data, "chromium");

        // The bystander: a browser of the same kind, on a profile of its own, started by nobody in this render.
        using Process bystander = StartBrowser(chromium, Path.Combine(_data, "bystander-profile"));
        try
        {
            await Task.Delay(1500);
            Task<byte[]> render = renderer.RenderAsync("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"50\" height=\"20\"/>", null, CancellationToken.None);
            // Freeze the browser the render launched (and only it: it is the one started with a pipe to the driver) the moment it exists.
            int frozen = await FreezeRenderBrowser(profiles, TimeSpan.FromSeconds(15));
            Assert.True(frozen > 0, "the render's browser never appeared, so there was nothing to freeze");
            var watch = Stopwatch.StartNew();

            PdfTimeoutException timeout = await Assert.ThrowsAsync<PdfTimeoutException>(() => render);

            Assert.InRange(watch.Elapsed.TotalSeconds, 0, 40);
            Assert.Contains("did not finish within 20 seconds", timeout.Message, StringComparison.Ordinal);
            Assert.Empty(BrowserProcessesOn(profiles)); // nothing of that render survived, helpers included
            Assert.False(bystander.HasExited, "the bystander browser was stopped with the hung one");
            Assert.Contains(logs.Lines, line => line.Message.Contains("stopped ", StringComparison.Ordinal) && line.Message.Contains("by id", StringComparison.Ordinal));
            Assert.DoesNotContain(logs.Lines, line => line.Message.Contains("would not start with its sandbox", StringComparison.Ordinal));

            // Health says so, once; and the next real render works (given the time a launch needs on a busy host: the limit above was for the frozen one).
            config.TimeoutSeconds = 120;
            PdfStatus status = await renderer.ProbeAsync(CancellationToken.None);
            Assert.False(status.Available);
            byte[] pdf = await RenderUntilCached(renderer);
            Assert.True(pdf.AsSpan().StartsWith("%PDF"u8));
            Assert.DoesNotContain(logs.Lines, line => line.Message.Contains("would not start with its sandbox", StringComparison.Ordinal));
            Assert.Empty(Directory.EnumerateDirectories(profiles, "profile-*"));
        }
        finally
        {
            if (!bystander.HasExited)
            {
                bystander.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<byte[]> RenderUntilCached(ChromiumPdfRenderer renderer)
    {
        return await renderer.RenderAsync("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"40\" viewBox=\"0 0 100 40\"><text x=\"5\" y=\"20\">After</text></svg>", null, CancellationToken.None);
    }

    private static Process StartBrowser(string chromium, string profile)
    {
        var start = new ProcessStartInfo(chromium, ["--headless=new", $"--user-data-dir={profile}", "--no-first-run", "about:blank"]) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        return Process.Start(start)!;
    }

    private async Task<int> FreezeRenderBrowser(string profileRoot, TimeSpan wait)
    {
        int count = 0;
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline && count == 0)
        {
            foreach (int pid in BrowserProcessesOn(profileRoot, mainOnly: true))
            {
                Signal("STOP", pid);
                _frozen.Add(pid);
                count++;
            }

            await Task.Delay(50);
        }

        return count;
    }

    /// <summary>The ids of processes whose command line names a profile folder under <paramref name="profileRoot"/>; with <paramref name="mainOnly"/>, only the browser's main process (no <c>--type=</c>).</summary>
    private static List<int> BrowserProcessesOn(string profileRoot, bool mainOnly = false)
    {
        var found = new List<int>();
        foreach (string line in Ps().Split('\n'))
        {
            string trimmed = line.Trim();
            int space = trimmed.IndexOf(' ');
            if (space < 0 || !int.TryParse(trimmed[..space], out int pid))
            {
                continue;
            }

            string command = trimmed[(space + 1)..];
            if (command.Contains($"--user-data-dir={profileRoot}", StringComparison.Ordinal) && (!mainOnly || !command.Contains("--type=", StringComparison.Ordinal)))
            {
                found.Add(pid);
            }
        }

        return found;
    }

    private static string Ps()
    {
        using Process ps = Process.Start(new ProcessStartInfo("ps", ["-A", "-ww", "-o", "pid=,command="]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        string output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return output;
    }

    private static void Signal(string name, int pid)
    {
        using Process kill = Process.Start(new ProcessStartInfo("kill", [$"-{name}", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]) { UseShellExecute = false })!;
        kill.WaitForExit();
    }
}

/// <summary>Where a Chromium for the tests lives: <c>DRAUGHTSMAN_TEST_CHROMIUM</c>, or Google Chrome on a Mac.</summary>
internal static class ChromiumLocator
{
    public static string? Path()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("DRAUGHTSMAN_TEST_CHROMIUM");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        const string macChrome = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        return File.Exists(macChrome) ? macChrome : null;
    }
}

internal sealed class RequiresChromiumFactAttribute : FactAttribute
{
    public RequiresChromiumFactAttribute()
    {
        if (ChromiumLocator.Path() is null)
        {
            Skip = "No Chromium executable found; set DRAUGHTSMAN_TEST_CHROMIUM or install Google Chrome on this Mac.";
        }
    }
}
