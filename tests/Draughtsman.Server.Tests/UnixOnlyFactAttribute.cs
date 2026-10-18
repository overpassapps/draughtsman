namespace Draughtsman.Server.Tests;

/// <summary>A fact that is reported Skipped, not Passed, on Windows: file modes and owner ids are a Unix idea.</summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "File modes and numeric owner ids do not exist on Windows.";
        }
    }
}
