using System.Reflection;
using System.Text.RegularExpressions;
using Draughtsman.Core;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-08 across the product: <c>ImporterComplexityTests</c> sweeps Core, and this sweeps every other shipped assembly the
/// server is built from, so a regular expression added to the theme loader, the AI prompt or the PDF page cannot skip
/// <see cref="RegexLimits"/> either. The limit's value is pinned too: it is deliberately long enough to survive a loaded host
/// and a change to it is a product decision (the importers refuse a source on a timeout), not a tidy-up.
/// </summary>
public class RegexTimeoutSweepTests
{
    [Fact]
    public void Every_regex_in_every_shipped_assembly_has_a_match_timeout()
    {
        Assembly[] assemblies =
        [
            typeof(RegexLimits).Assembly,
            typeof(Program).Assembly,
            typeof(Draughtsman.Ai.IModelProvider).Assembly,
            typeof(Draughtsman.Mcp.Tools.DraughtsmanTools).Assembly,
            typeof(Draughtsman.Storage.EfStore).Assembly,
            typeof(Draughtsman.Sidecar.ISidecarClient).Assembly,
        ];

        var unbounded = new List<string>();
        foreach (Assembly assembly in assemblies.Distinct())
        {
            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    GeneratedRegexAttribute? attribute = method.GetCustomAttribute<GeneratedRegexAttribute>();
                    if (attribute is not null && attribute.MatchTimeoutMilliseconds <= 0)
                    {
                        unbounded.Add($"{type.FullName}.{method.Name}");
                    }
                }

                foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType == typeof(Regex) && ((Regex?)field.GetValue(null))?.MatchTimeout == Regex.InfiniteMatchTimeout)
                    {
                        unbounded.Add($"{type.FullName}.{field.Name}");
                    }
                }
            }
        }

        Assert.True(unbounded.Count == 0, "Regular expressions with no match timeout: " + string.Join(", ", unbounded));
    }

    [Fact]
    public void The_limit_is_long_enough_to_survive_a_loaded_host_and_still_bounded()
    {
        // Under CPU load a thread can go unscheduled for hundreds of milliseconds, so 500 ms refused valid imports; and a
        // limit with no bound at all is the denial of service the sweep above exists to prevent.
        Assert.InRange(RegexLimits.TimeoutMilliseconds, 5_000, 10_000);
        Assert.Equal(TimeSpan.FromMilliseconds(RegexLimits.TimeoutMilliseconds), RegexLimits.Timeout);
    }
}
