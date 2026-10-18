namespace Draughtsman.Server.Tests;

/// <summary>A test that needs file modes to keep the account out: skipped on Windows and when run as root, which can write a read-only file.</summary>
public sealed class UnixNonRootFactAttribute : FactAttribute
{
    public UnixNonRootFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "File modes do not keep an account out on Windows.";
        }
        else if (Environment.UserName == "root")
        {
            Skip = "Running as root, which can write a read-only file.";
        }
    }
}
