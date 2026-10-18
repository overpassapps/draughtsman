using System.Text.RegularExpressions;

namespace Draughtsman.Core;

/// <summary>
/// Bounds for every regular expression that reads text a caller sent (AU-08): the importers and the DSL read up to a
/// megabyte of untrusted source, and a backtracking engine can take time quadratic or worse in it. Every expression has
/// a match timeout, so the worst case is an exception the caller reports as a refusal rather than a thread pinned for
/// minutes, and one that could be hit with real input is built <see cref="Linear"/>. A test sweeps every shipped assembly
/// for a <c>[GeneratedRegex]</c> or <c>Regex</c> field without a timeout, so a new one cannot skip this. Public so that the
/// server and AI assemblies take the same limit instead of each choosing its own.
/// </summary>
public static class RegexLimits
{
    /// <summary>
    /// A match timeout is wall-clock time, and an ordinary match takes microseconds, so it only ever fires for input that
    /// backtracks without bound (a runaway one is still freed within five seconds) or for a thread the scheduler did not
    /// run: 500 ms refused valid SQL imports on a host at load average 100+ (a descheduled thread loses hundreds of
    /// milliseconds at a time). A whole 1 MB legitimate import (tens of thousands of matches) took 85 to 470 ms here at load
    /// average 90, so 5 s is at least ten times the entire import and thousands of times any one match; and the first
    /// timeout aborts the whole import, so a hostile source still costs one timeout (5.0 s measured for a megabyte of
    /// spaces behind a backtracking message expression), not one per line.
    /// </summary>
    public const int TimeoutMilliseconds = 5000;

    /// <summary><see cref="TimeoutMilliseconds"/> as the <see cref="TimeSpan"/> the <see cref="Regex"/> constructors take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(TimeoutMilliseconds);

    /// <summary>
    /// A regular expression that runs in time linear in the input whatever the input is (.NET's non-backtracking engine),
    /// with the same matches and captures as the backtracking one for a pattern without look-arounds or back-references.
    /// Not available to <c>[GeneratedRegex]</c>, so built at start-up and held in a static field.
    /// </summary>
    public static Regex Linear(string pattern, RegexOptions options = RegexOptions.None)
    {
        return new Regex(pattern, options | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, Timeout);
    }
}
