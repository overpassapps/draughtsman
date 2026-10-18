using System.Diagnostics;
using System.Runtime.Versioning;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The real executable pointed at a data folder it cannot write: it must stop with one plain line on standard error, a non-zero exit code, no
/// stack trace and no "Unhandled exception", and it must not be listening. (The report that led to this saw SQLite error 14 and a trace.)
/// </summary>
public sealed class UnwritableDataFolderStartupTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public UnwritableDataFolderStartupTests()
    {
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(_data, recursive: true);
    }

    [UnixNonRootFact]
    [UnsupportedOSPlatform("windows")]
    public async Task The_server_stops_with_one_plain_line_naming_the_folder_the_user_and_the_fix()
    {
        File.SetUnixFileMode(_data, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.Environment["DRAUGHTSMAN_DATA"] = _data;
        start.Environment["DRAUGHTSMAN_KEY_RING_PROTECTION"] = "none";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using Process process = Process.Start(start)!;
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "the server did not stop");
            string error = await stderr;
            string output = await stdout;

            Assert.NotEqual(0, process.ExitCode);
            string everything = output + error;
            Assert.DoesNotContain("Unhandled exception", everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("   at ", everything, StringComparison.Ordinal);
            string[] lines = everything.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string line = Assert.Single(lines);
            Assert.Contains(_data, line, StringComparison.Ordinal);
            Assert.Contains(Environment.UserName, line, StringComparison.Ordinal);
            Assert.Contains("chown -R", line, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
