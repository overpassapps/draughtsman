using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Draughtsman.Server.Ai;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The AI endpoint policy's network seam for tests: names resolve to whatever the test says, and a "connect" to any address is made to
/// the one real loopback test server instead, while recording which address and port the guard asked for. So a test can show that a name
/// that resolves to a blocked address is never dialled, that an allowed one is dialled at exactly the address that was checked, and do
/// both with a real HTTP exchange, without a lookup or a packet leaving the machine.
/// </summary>
internal sealed class FakeAiNetwork(LoopbackHttpServer? server = null) : IAiEndpointNetwork
{
    private readonly ConcurrentDictionary<string, Queue<IPAddress[]>> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _resolved = new();
    private readonly ConcurrentQueue<(IPAddress Address, int Port)> _dialled = new();

    /// <summary>Every name looked up, in order.</summary>
    public IReadOnlyList<string> Resolved => [.. _resolved];

    /// <summary>Every address and port the guard asked to connect to, in order.</summary>
    public IReadOnlyList<(IPAddress Address, int Port)> Dialled => [.. _dialled];

    /// <summary>The answers <paramref name="host"/> gets, one lookup at a time; the last one repeats.</summary>
    public FakeAiNetwork Answer(string host, params string[][] answers)
    {
        _answers[host] = new Queue<IPAddress[]>(answers.Select(a => a.Select(IPAddress.Parse).ToArray()));
        return this;
    }

    public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        _resolved.Enqueue(host);
        if (!_answers.TryGetValue(host, out Queue<IPAddress[]>? queue))
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        lock (queue)
        {
            IPAddress[] answer = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return ValueTask.FromResult<IReadOnlyList<IPAddress>>(answer);
        }
    }

    public async ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        _dialled.Enqueue((address, port));
        if (server is null)
        {
            throw new SocketException((int)SocketError.ConnectionRefused);
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// A one-purpose HTTP/1.1 server on a loopback port: it reads each request's head, records it, and answers with whatever the test's
/// script says, closing the connection each time (so every request is a fresh connection through the guard). Written on a raw
/// listener so it behaves the same on every OS and needs no URL reservation.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public LoopbackHttpServer(Func<string, string> respond)
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptAsync(respond));
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The head (request line and headers) of every request received, in order.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    public static string Ok(string body, string contentType = "application/json")
    {
        return $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
    }

    public static string Redirect(string location)
    {
        return $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop ends with the listener.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync(Func<string, string> respond)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, respond));
        }
    }

    private async Task ServeAsync(TcpClient client, Func<string, string> respond)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0)
                    {
                        return;
                    }

                    head.Append((char)one[0]);
                }

                string request = head.ToString();
                _requests.Enqueue(request);

                // A POST's body has to be read off before the connection closes, or the client sees a reset instead of the answer.
                int length = ContentLengthOf(request);
                var body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = await stream.ReadAsync(body.AsMemory(read), _stop.Token);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                byte[] answer = Encoding.UTF8.GetBytes(respond(request));
                await stream.WriteAsync(answer, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The client went away, or the test is over.
            }
        }
    }

    private static int ContentLengthOf(string head)
    {
        foreach (string line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line["Content-Length:".Length..].Trim(), out int length))
            {
                return length;
            }
        }

        return 0;
    }
}
