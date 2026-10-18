using System.Diagnostics.Tracing;
using System.Globalization;
using System.Net;

namespace Draughtsman.Server.Tests;

/// <summary>One place the process reached for: which framework channel announced it, where to, and whether that is off this machine.</summary>
public sealed record NetworkAttempt(string Channel, string Destination, bool OffMachine)
{
    public override string ToString()
    {
        return $"{Channel} -> {Destination}{(OffMachine ? " (OFF THE MACHINE)" : " (this machine)")}";
    }
}

/// <summary>
/// Watches every channel through which .NET code in this process can start talking to another host, and records where it was
/// pointed. It listens to the framework's own event sources, so it needs no cooperation from the code under test: a
/// <c>new HttpClient()</c>, an <c>IHttpClientFactory</c> client, an <c>SmtpClient</c>, a raw <c>Socket</c> or a <c>Dns</c> lookup are all
/// seen, wherever in the server (or a dependency) they sit:
/// <list type="bullet">
/// <item><c>System.Net.NameResolution</c> <c>ResolutionStart(hostNameOrAddress)</c>: a DNS lookup, which precedes any connection by name.</item>
/// <item><c>System.Net.Sockets</c> <c>ConnectStart(address)</c>: an outgoing TCP connection by address (a literal IP needs no lookup).</item>
/// <item><c>System.Net.Http</c> <c>RequestStart(host, ...)</c>: an HTTP request, before it resolves or connects.</item>
/// <item><c>System.Net.Security</c> <c>HandshakeStart(targetHost)</c>: a TLS client handshake.</item>
/// </list>
/// A destination counts as "this machine" only when it is a loopback name or address; everything else, and anything the observer
/// cannot read, is off the machine (it fails closed). It sees this process only: the Node sidecar and Chromium are separate
/// processes, covered by <c>scripts/check-offline-bundles.mjs</c> and the PDF tests. Start it before the server so startup is
/// watched too, and keep the tests that use it in <see cref="OutboundNetworkCollection"/> so no other test's traffic is mixed in.
/// </summary>
public sealed class OutboundNetworkObserver : EventListener
{
    private static readonly string[] s_watched = ["System.Net.NameResolution", "System.Net.Sockets", "System.Net.Http", "System.Net.Security"];

    private readonly object _gate = new();
    private readonly List<NetworkAttempt> _attempts = [];

    /// <summary>Every attempt seen, on this machine or off it, in the order they happened.</summary>
    public IReadOnlyList<NetworkAttempt> Attempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _attempts];
            }
        }
    }

    /// <summary>The attempts that are not to this machine: what an air-gapped install must never show.</summary>
    public IReadOnlyList<NetworkAttempt> OffMachine => [.. Attempts.Where(a => a.OffMachine)];

    /// <summary>A readable list for an assertion message.</summary>
    public string Describe(IEnumerable<NetworkAttempt> attempts)
    {
        string[] lines = [.. attempts.Select(a => "  " + a)];
        return lines.Length == 0 ? "  (none)" : string.Join('\n', lines);
    }

    /// <summary>Reads an event the way the observer does, so the classification can be tested without making a connection.</summary>
    public static NetworkAttempt? Interpret(string source, string eventName, IReadOnlyDictionary<string, object?> payload)
    {
        string? Text(string name)
        {
            return payload.TryGetValue(name, out object? value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        switch (source, eventName)
        {
            case ("System.Net.NameResolution", "ResolutionStart"):
                string name = Text("hostNameOrAddress") ?? string.Empty;
                return new NetworkAttempt("dns lookup", name, !NetworkDestination.IsThisMachine(name));
            case ("System.Net.Sockets", "ConnectStart"):
                string address = Text("address") ?? string.Empty;
                return new NetworkAttempt("tcp connect", address, !NetworkDestination.SocketAddressIsThisMachine(address));
            case ("System.Net.Http", "RequestStart"):
                string host = Text("host") ?? string.Empty;
                return new NetworkAttempt("http request", host + ":" + Text("port"), !NetworkDestination.IsThisMachine(host));
            case ("System.Net.Security", "HandshakeStart"):
                string target = Text("targetHost") ?? string.Empty;
                return payload.TryGetValue("isServer", out object? isServer) && Equals(isServer, true)
                    ? null
                    : new NetworkAttempt("tls handshake", target, !NetworkDestination.IsThisMachine(target));
            default:
                return null;
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (Array.IndexOf(s_watched, eventSource.Name) >= 0)
        {
            EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName is null || eventData.Payload is null || eventData.PayloadNames is null)
        {
            return;
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < eventData.PayloadNames.Count && i < eventData.Payload.Count; i++)
        {
            payload[eventData.PayloadNames[i]] = eventData.Payload[i];
        }

        NetworkAttempt? attempt = Interpret(eventData.EventSource.Name, eventData.EventName, payload);
        if (attempt is null)
        {
            return;
        }

        lock (_gate)
        {
            _attempts.Add(attempt);
        }
    }
}

/// <summary>Where a name or socket address points: this machine (loopback) or somewhere else.</summary>
public static class NetworkDestination
{
    /// <summary>True for <c>localhost</c> (and <c>*.localhost</c>) and any loopback address, with or without brackets or a port.</summary>
    public static bool IsThisMachine(string host)
    {
        string text = host.Trim().Trim('[', ']');
        if (text.Length == 0)
        {
            return false;
        }

        if (text.Equals("localhost", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(text, out IPAddress? address) && IsLoopback(address);
    }

    /// <summary>
    /// A <c>SocketAddress</c> as the framework prints it in the Sockets event: <c>InterNetwork:16:{port-hi,port-lo,a,b,c,d,0...}</c> or the
    /// 28-byte <c>InterNetworkV6</c> form. A Unix-domain socket never leaves the machine. Anything else is unreadable, so off the machine.
    /// </summary>
    public static bool SocketAddressIsThisMachine(string text)
    {
        int firstColon = text.IndexOf(':', StringComparison.Ordinal);
        if (firstColon < 0)
        {
            return false;
        }

        string family = text[..firstColon];
        if (family.Equals("Unix", StringComparison.Ordinal))
        {
            return true;
        }

        int open = text.IndexOf('{', StringComparison.Ordinal);
        int close = text.LastIndexOf('}');
        if (open < 0 || close < open)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = [.. text[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(b => byte.Parse(b, CultureInfo.InvariantCulture))];
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        // The printed bytes start after the two-byte family: port (2), then the address (IPv4) or flow label (4) and address (IPv6).
        if (family.Equals("InterNetwork", StringComparison.Ordinal) && bytes.Length >= 6)
        {
            return IsLoopback(new IPAddress(bytes[2..6]));
        }

        if (family.Equals("InterNetworkV6", StringComparison.Ordinal) && bytes.Length >= 22)
        {
            return IsLoopback(new IPAddress(bytes[6..22]));
        }

        return false;
    }

    private static bool IsLoopback(IPAddress address)
    {
        return IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }
}

/// <summary>
/// Serialises the tests that watch the process's network traffic. xUnit runs a collection that disables parallelisation on its own,
/// so no other test class is making connections while an observer is listening.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OutboundNetworkCollection
{
    public const string Name = "outbound-network";
}
