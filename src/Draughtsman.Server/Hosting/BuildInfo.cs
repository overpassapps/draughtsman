using System.Globalization;
using System.Reflection;
using Draughtsman.Server.Endpoints;

namespace Draughtsman.Server.Hosting;

/// <summary>
/// What this binary is: its version and the day it was built. The build date is stamped in by MSBuild
/// (<c>DraughtsmanBuildDate</c> in Draughtsman.Server.csproj), never read from the clock at run time, so a build reports
/// the day it was made however long it keeps running.
/// </summary>
public sealed record BuildInfo(string Version, DateOnly BuildDate)
{
    public const string MetadataKey = "DraughtsmanBuildDate";

    /// <summary>This assembly's stamped build. An unreadable stamp (it cannot happen through MSBuild) falls back to today.</summary>
    public static BuildInfo FromAssembly()
    {
        string? stamp = typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == MetadataKey)?.Value;
        DateOnly date = DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)
            ? parsed
            : DateOnly.FromDateTime(DateTime.UtcNow);
        return new BuildInfo(HealthEndpoints.ProductVersion, date);
    }
}
