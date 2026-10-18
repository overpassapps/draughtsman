using System.Diagnostics;
using System.Text.Json;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The real executable, started as an agent would start it: <c>draughtsman mcp --stdio</c> must answer JSON-RPC on stdout
/// and write nothing else there, even with extra arguments (PK-05: they used to start the web host, whose startup logging
/// on stdout corrupts the stream).
/// </summary>
public class McpStdioCliTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
            // Best effort on Windows, where a pooled handle can outlive the process briefly.
        }
    }

    private Process Start(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DRAUGHTSMAN_DATA"] = _data;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return Process.Start(start)!;
    }

    [Theory]
    [InlineData("mcp", "--stdio")]
    [InlineData("mcp", "--stdio", "--an-option-nothing-knows")]
    [InlineData("mcp", "--stdio", "stray", "words")]
    public async Task Stdio_answers_initialize_on_stdout_and_leaves_no_other_output_there(params string[] arguments)
    {
        using Process process = Start(arguments);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"cli-test","version":"1"}}}""");
            await process.StandardInput.FlushAsync();

            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);

            Assert.NotNull(line);
            using JsonDocument reply = JsonDocument.Parse(line);
            Assert.Equal(1, reply.RootElement.GetProperty("id").GetInt32());
            Assert.True(reply.RootElement.GetProperty("result").TryGetProperty("serverInfo", out _));

            process.StandardInput.Close();
            Assert.True(process.WaitForExit(30_000), "stdio host did not stop when stdin closed");
            Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync(timeout.Token));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task Mcp_without_stdio_exits_2_without_starting_anything()
    {
        using Process process = Start("mcp");
        try
        {
            Assert.True(process.WaitForExit(30_000));
            Assert.Equal(2, process.ExitCode);
            Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync());
            Assert.Contains("--stdio", await process.StandardError.ReadToEndAsync());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task Version_prints_and_exits_without_creating_a_data_folder_or_binding_a_port()
    {
        using Process process = Start("--version");
        try
        {
            Assert.True(process.WaitForExit(30_000));
            Assert.Equal(0, process.ExitCode);
            Assert.StartsWith("Draughtsman ", await process.StandardOutput.ReadToEndAsync());
            Assert.False(Directory.Exists(_data));
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
