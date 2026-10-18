using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Sidecar;

/// <summary>
/// Starts the sidecar with the server so /api/health can report it. A sidecar that will not start is logged and
/// reported as degraded; it never stops the server from coming up. Stopping is the container's job: it disposes
/// the client once the host has drained, so a request in the shutdown window still gets an answer.
/// </summary>
internal sealed class SidecarHostedService : IHostedService
{
    private readonly NodeSidecarClient _client;
    private readonly ILogger<SidecarHostedService> _logger;

    public SidecarHostedService(NodeSidecarClient client, ILogger<SidecarHostedService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SidecarException ex)
        {
            _logger.LogWarning("Sidecar not available at startup; server-side layout and Yjs snapshots are degraded until it is: {Message}", ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
