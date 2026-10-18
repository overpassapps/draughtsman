using System.Diagnostics;

namespace Draughtsman.Tests.Shared;

/// <summary>
/// How much slower this machine is running right now than the reference, for a test that compares a measured time with a number of
/// milliseconds. A CPU-bound test measures the host as much as the code: on a Mac running other builds at load average 100 or more every
/// thread gets a small share of the CPU, so an import that takes 370 ms idle took many seconds and an absolute budget failed for no fault of the
/// code (the SQL import tests, in a verification run at load 79 to 142).
/// <para>
/// The scale is the median of a fixed calibration workload measured now over its median on an idle reference machine
/// (<see cref="ReferenceMilliseconds"/>), never below 1, so an idle or faster machine keeps the budget exactly as written. A regression in the
/// code under test does not change the calibration, so a budget multiplied by the scale still fails on the same slowdown it always did.
/// Prefer a count of operations or of allocated bytes to any time at all, and never a ratio of two timings: garbage collection is not proportional to
/// the work, so the ratio of a linear algorithm at two sizes wandered from 1x to 27x for 4x the work (SchemaValidatorTests, the linear-time test).
/// </para>
/// </summary>
internal static class LoadScale
{
    /// <summary>Median of <see cref="Calibrate"/> on an idle Apple M-series Mac under .NET 10 (Release).</summary>
    private const double ReferenceMilliseconds = 1.1;

    private const int Samples = 31;

    /// <summary>The slowdown factor, at least 1. Takes about 30 ms idle.</summary>
    public static double Now()
    {
        Calibrate();
        var times = new double[Samples];
        for (int i = 0; i < Samples; i++)
        {
            var timer = Stopwatch.StartNew();
            Calibrate();
            times[i] = timer.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        return Math.Max(1.0, times[Samples / 2] / ReferenceMilliseconds);
    }

    /// <summary><paramref name="budget"/> stretched by <see cref="Now"/>.</summary>
    public static TimeSpan Scaled(TimeSpan budget) => budget * Now();

    /// <summary>A fixed amount of allocation, sorting and string work, the mix the importers and renderers do.</summary>
    private static long Calibrate()
    {
        var items = new List<(string Id, int X, int Y)>(20_000);
        for (int i = 0; i < 20_000; i++)
        {
            items.Add(("n_" + i, (int)((i * 7919L) % 10_007), (int)((i * 104_729L) % 10_009)));
        }

        items.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        long total = 0;
        foreach ((string id, int x, _) in items)
        {
            total += id.Length + x;
        }

        return total;
    }
}
