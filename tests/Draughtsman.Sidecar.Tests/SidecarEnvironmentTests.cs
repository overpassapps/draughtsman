using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Sidecar.Tests;

/// <summary>AU-10 against a real child: the Node process the sidecar spawns does not see the server's secrets.</summary>
public sealed class SidecarEnvironmentTests
{
    [Fact]
    public async Task The_sidecar_process_gets_an_allow_listed_environment_and_not_the_servers_secrets()
    {
        string dump = Path.Combine(Path.GetTempPath(), $"sidecar-env-{Guid.NewGuid():N}.json");
        string script = Path.Combine(Path.GetTempPath(), $"sidecar-env-probe-{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(script, $"require('fs').writeFileSync({System.Text.Json.JsonSerializer.Serialize(dump)}, JSON.stringify(process.env)); process.exit(0);", CancellationToken.None);
        string[] secrets = ["DRAUGHTSMAN_AI_KEY", "DRAUGHTSMAN_KEY_ENCRYPTION_KEY", "DRAUGHTSMAN_TEST_UNLISTED_SECRET"];
        foreach (string name in secrets)
        {
            Environment.SetEnvironmentVariable(name, "sk-secret-value-" + name);
        }

        try
        {
            SidecarProcess process = SidecarProcess.Start("node", script, NullLogger.Instance);
            await process.Exited.WaitAsync(TimeSpan.FromSeconds(120), CancellationToken.None);
            await process.DisposeAsync();

            // Windows names the variable "Path" and does not distinguish case; Node reports whichever spelling it was started with.
            var seen = new Dictionary<string, string>(
                System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(dump, CancellationToken.None))!,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            Assert.False(string.IsNullOrEmpty(seen.GetValueOrDefault("PATH")), "the child still needs PATH to work");
            if (OperatingSystem.IsWindows())
            {
                // Without these Node cannot load its own system libraries or resolve a command (a Windows-only way to break the sidecar).
                foreach (string required in new[] { "SystemRoot", "TEMP" })
                {
                    Assert.False(string.IsNullOrEmpty(seen.GetValueOrDefault(required)), $"{required} is missing from the sidecar's environment");
                }
            }

            foreach (string name in secrets)
            {
                Assert.False(seen.ContainsKey(name), $"{name} leaked into the sidecar's environment");
            }

            Assert.DoesNotContain(seen.Values, value => value.StartsWith("sk-secret-value-", StringComparison.Ordinal));
        }
        finally
        {
            foreach (string name in secrets)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            File.Delete(script);
            File.Delete(dump);
        }
    }
}
