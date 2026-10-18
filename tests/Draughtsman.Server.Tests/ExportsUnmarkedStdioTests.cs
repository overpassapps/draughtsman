using System.Diagnostics;
using System.Text.Json;

namespace Draughtsman.Server.Tests;

/// <summary>
/// <c>draughtsman mcp --stdio</c> is a second host over the same tools: an agent's <c>render_document</c> there draws the
/// picture and nothing else, as the web host's does. The real executable, on an empty data folder and then on a folder an
/// earlier build left a licence file and a <c>licence:</c> setting in, which nothing reads now.
/// </summary>
public sealed class ExportsUnmarkedStdioTests : IDisposable
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

    private async Task<string> RenderOverStdio()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("mcp");
        start.ArgumentList.Add("--stdio");
        start.Environment["DRAUGHTSMAN_DATA"] = _data;
        start.Environment["DRAUGHTSMAN_KEY_RING_PROTECTION"] = "none";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using Process process = Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            async Task<JsonElement> Call(int id, string method, string parameters)
            {
                await process.StandardInput.WriteLineAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{parameters}}}""");
                await process.StandardInput.FlushAsync(timeout.Token);
                while (true)
                {
                    string line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("the stdio host closed its output");
                    JsonElement message = JsonDocument.Parse(line).RootElement.Clone();
                    if (message.TryGetProperty("id", out JsonElement reply) && reply.ValueKind == JsonValueKind.Number && reply.GetInt32() == id)
                    {
                        Assert.False(message.TryGetProperty("error", out JsonElement error), error.ToString());
                        return message.GetProperty("result");
                    }
                }
            }

            await Call(1, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"notice-test","version":"1"}}""");
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            string create = JsonSerializer.Serialize(new { name = "create_document", arguments = new { title = "Stdio", type = "flowchart", format = "dsl", content = "draughtsman 1\ntype flowchart\ntitle \"Stdio\"\nnode n_a process \"Start\" @ 0,0 120x60\n" } });
            JsonElement created = await Call(2, "tools/call", create);
            Assert.False(created.TryGetProperty("isError", out JsonElement createdError) && createdError.GetBoolean());
            string id = JsonDocument.Parse(created.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.GetProperty("id").GetString()!;

            JsonElement rendered = await Call(3, "tools/call", JsonSerializer.Serialize(new { name = "render_document", arguments = new { id, format = "svg" } }));
            Assert.False(rendered.TryGetProperty("isError", out JsonElement renderedError) && renderedError.GetBoolean());
            return JsonDocument.Parse(rendered.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.GetProperty("svg").GetString()!;
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
    public async Task Render_document_over_stdio_carries_no_notice_and_an_old_licence_file_changes_nothing()
    {
        string plain = await RenderOverStdio();
        Assert.Contains("data-id=\"n_a\"", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("valuation", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("evaluation-notice", plain, StringComparison.Ordinal);

        // What a data folder from a build that checked a licence holds: a file at licence.key and the setting that named it.
        await File.WriteAllTextAsync(Path.Combine(_data, "licence.key"), "{ \"not\": \"a licence this build reads\" }", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_data, "draughtsman.yaml"), "licence:\n  path: licence.key\n", CancellationToken.None);
        string upgraded = await RenderOverStdio();
        Assert.Equal(plain, upgraded);
    }
}
