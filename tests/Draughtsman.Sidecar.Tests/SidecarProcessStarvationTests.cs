using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Sidecar.Tests;

/// <summary>
/// Held apart from every other test class: saturating the process-wide thread pool would stall whatever runs beside it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadPoolStarvationCollection
{
    public const string Name = "Thread pool starvation";
}

/// <summary>
/// The CI flake (GitHub issue 16): on a busy 2-vCPU Windows runner the first sidecar ping of the Server test run took 9 to 19 s, the
/// 15 s startup timeout killed a sidecar that was running fine, and four <c>LayoutEndpointTests</c> answered 503. Instrumenting a Windows
/// VM showed the sidecar's answer read by its stdout thread at about 1.5 s while the request that was waiting for it resumed at 4 s: a redirected
/// pipe is a synchronous handle on Windows, so every "async" read and write on it was a thread-pool work item (two readers parked for good per
/// sidecar, a write queued behind whatever else the pool was running), and the pool adds a thread every half second at best.
/// </summary>
[Collection(ThreadPoolStarvationCollection.Name)]
public sealed class SidecarProcessStarvationTests
{
    [Fact]
    public async Task An_answer_that_was_read_while_the_thread_pool_was_saturated_is_not_reported_as_a_timeout()
    {
        string node = SidecarLocator.ResolveNode(string.Empty, AppContext.BaseDirectory);
        string script = SidecarLocator.ResolveScript(string.Empty, AppContext.BaseDirectory);
        TimeSpan timeout = TimeSpan.FromSeconds(2);
        using var release = new ManualResetEventSlim();
        SidecarProcess? process = null;
        try
        {
            // Far more blocked work items than the pool can add threads for while the hold lasts (about two a second), so
            // anything queued to the pool after these waits until they are released.
            for (int i = 0; i < Math.Max(Environment.ProcessorCount * 8, 96); i++)
            {
                ThreadPool.QueueUserWorkItem(static gate => ((ManualResetEventSlim)gate!).Wait(), release, preferLocal: false);
            }

            process = SidecarProcess.Start(node, script, NullLogger.Instance);
            Task<JsonElement> ping = process.InvokeAsync("ping", null, timeout, CancellationToken.None);

            // The answer has to arrive with the pool still blocked: the stdout thread and the writer thread are not pool threads.
            var waited = Stopwatch.StartNew();
            while (process.ResponsesRead == 0 && waited.Elapsed < TimeSpan.FromSeconds(20))
            {
                Thread.Sleep(10);
            }

            bool answeredWithoutThePool = process.ResponsesRead > 0;

            // And the timeout is due before anything gets to run, so what resumes first decides whether a running sidecar is killed.
            while (waited.Elapsed < timeout + TimeSpan.FromSeconds(1))
            {
                Thread.Sleep(10);
            }

            release.Set();
            bool finished = await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(30))) == ping;

            Assert.True(answeredWithoutThePool, "the sidecar's answer was not read while the thread pool was saturated");
            Assert.True(finished, "the ping did not complete once the thread pool was free again");
            JsonElement result = await ping;
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.True(process.IsRunning, "a sidecar that had answered was stopped for being slow to be noticed");
        }
        finally
        {
            release.Set();
            if (process is not null)
            {
                await process.DisposeAsync();
            }
        }
    }
}
