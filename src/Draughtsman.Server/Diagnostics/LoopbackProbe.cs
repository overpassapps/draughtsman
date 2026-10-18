using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Draughtsman.Server.Diagnostics;

/// <summary>
/// Asks the server on THIS machine whether it is answering, for <c>draughtsman doctor</c> and <c>support-bundle</c> (which must work with the server
/// down, so they cannot rely on it): one connection to the loopback address (never a name lookup, never another host) and one anonymous
/// <c>GET /api/health</c> written by hand over the socket. It sends no credential and reads at most 8 KiB. This is the only network code
/// the command line has; it is listed in the air-gap inventory (docs/data-flow.md) and can only ever reach 127.0.0.1 and ::1.
/// </summary>
public static class LoopbackProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private const int MaxResponseBytes = 8 * 1024;

    public static async Task<LoopbackAnswer> ProbeAsync(int port, string? hostHeader = null, CancellationToken cancellationToken = default)
    {
        foreach (IPAddress address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            LoopbackAnswer? answer = await TryAsync(address, port, hostHeader ?? "localhost", cancellationToken);
            if (answer is not null)
            {
                return answer;
            }
        }

        return new LoopbackAnswer(port, false, false, null, null);
    }

    private static async Task<LoopbackAnswer?> TryAsync(IPAddress address, int port, string host, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, timeout.Token);
            NetworkStream stream = client.GetStream();
            byte[] request = Encoding.ASCII.GetBytes($"GET /api/health HTTP/1.1\r\nHost: {host}\r\nAccept: application/json\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, timeout.Token);

            var buffer = new MemoryStream();
            byte[] chunk = new byte[2048];
            while (buffer.Length < MaxResponseBytes)
            {
                int read = await stream.ReadAsync(chunk, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            return Parse(port, Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Reads the status line and, if the body is a Draughtsman health document, its <c>status</c>. Anything else that answered HTTP is "something else".</summary>
    internal static LoopbackAnswer Parse(int port, string response)
    {
        int? status = null;
        int firstSpace = response.IndexOf(' ', StringComparison.Ordinal);
        if (response.StartsWith("HTTP/", StringComparison.Ordinal) && firstSpace > 0 && response.Length >= firstSpace + 4
            && int.TryParse(response.AsSpan(firstSpace + 1, 3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
        {
            status = parsed;
        }

        int bodyStart = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string body = bodyStart >= 0 ? response[(bodyStart + 4)..] : string.Empty;
        // A chunked body wraps the JSON in size lines: take the first object.
        int open = body.IndexOf('{', StringComparison.Ordinal);
        int close = body.LastIndexOf('}');
        if (status == 200 && open >= 0 && close > open)
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(body[open..(close + 1)]);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("status", out JsonElement health) && health.ValueKind == JsonValueKind.String
                    && health.GetString() is "ok" or "degraded")
                {
                    return new LoopbackAnswer(port, true, true, health.GetString(), status);
                }
            }
            catch (JsonException)
            {
                // Not ours.
            }
        }

        // A Draughtsman server that refuses this Host header (allowedHosts) still answers: 400 from the framework's host filtering is not "something else" in practice,
        // but it cannot be told from one, so it is reported as what it is - something that answered, with its status.
        return new LoopbackAnswer(port, true, false, null, status);
    }
}
