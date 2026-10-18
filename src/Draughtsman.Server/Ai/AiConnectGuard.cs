using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Draughtsman.Server.Hardening;

namespace Draughtsman.Server.Ai;

/// <summary>
/// Thrown, from inside the HTTP client's connect step, when the AI endpoint policy refuses a connection. The HTTP stack wraps it in an
/// <see cref="HttpRequestException"/>, which the providers pass on as <see cref="Draughtsman.Ai.ModelProviderException.InnerException"/>,
/// so <see cref="AiSettingsService"/> can recognise it and say why. The message is fixed text, never an address.
/// </summary>
internal sealed class AiEndpointRefusedException(string message) : InvalidOperationException(message)
{
    /// <summary>The refusal in <paramref name="error"/>'s chain of inner exceptions, if any.</summary>
    public static AiEndpointRefusedException? Find(Exception? error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is AiEndpointRefusedException refused)
            {
                return refused;
            }
        }

        return null;
    }
}

/// <summary>
/// The two things the guard needs from the network, as a seam: name resolution and the TCP connect itself. Production is
/// <see cref="SystemAiEndpointNetwork"/>; tests substitute a resolver that answers with a chosen address and a connect that records
/// where it was asked to go, so a policy test never touches DNS or a socket.
/// </summary>
internal interface IAiEndpointNetwork
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);

    ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken);
}

internal sealed class SystemAiEndpointNetwork : IAiEndpointNetwork
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        return await Dns.GetHostAddressesAsync(host, cancellationToken);
    }

    public async ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
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
/// Enforces <see cref="AiEndpointPolicy"/> where it cannot be talked round: in the HTTP client's connect step, on the address that is
/// about to be connected to.
/// <para>
/// <b>DNS rebinding.</b> The name is resolved here, every answer is checked, and the connection is then made to one of the addresses
/// that was checked, by IP, never by name again. There is no second lookup between the check and the connect for an attacker's DNS to
/// answer differently (the classic rebinding window), and a name that resolves differently on the next request is simply checked again,
/// because every new connection comes through here. TLS is untouched: the handler still negotiates it with the request's host name over
/// the stream returned from here, so the certificate is checked against the name, not the address.
/// </para>
/// <para>
/// <b>Residual risk: a proxy.</b> When the system proxy carries the request (HTTP_PROXY / HTTPS_PROXY, no bypass) the handler dials the
/// proxy and the proxy resolves the target itself, so this code cannot pin the address it connects to. It still resolves and judges the
/// target's name first (so a name that points at a blocked address is refused when the proxy is configured too), but that check and the
/// proxy's own lookup are two lookups, and DNS rebinding between them is not closed. Where a keyed provider is involved the destination
/// is limited to the published or listed hosts anyway, and the proxy is the operator's own; a deployment that needs the guarantee
/// leaves the proxy variables unset for this process.
/// </para>
/// </summary>
internal sealed class AiConnectGuard(string provider, IReadOnlyList<string> allowedHosts, IAiEndpointNetwork network)
{
    /// <summary>The handler the AI providers' <see cref="HttpClient"/>s use: the policy at connect time, and redirects never followed.</summary>
    public static SocketsHttpHandler CreateHandler(string provider, IReadOnlyList<string> allowedHosts, IAiEndpointNetwork network)
    {
        var guard = new AiConnectGuard(provider, allowedHosts, network);
        return new SocketsHttpHandler
        {
            // A redirect would carry the request, key header included, to whatever host the answer names, past every check above.
            AllowAutoRedirect = false,
            ConnectCallback = guard.ConnectAsync,
        };
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        DnsEndPoint dial = context.DnsEndPoint;
        Uri? target = context.InitialRequestMessage?.RequestUri;

        // The request's own host: what the policy judges. Normally it is also what is dialled; through a proxy it is not.
        string host = target is null ? dial.Host.Trim('[', ']').ToLowerInvariant() : AiEndpointPolicy.HostOf(target);
        int port = target?.Port ?? dial.Port;
        bool viaProxy = target is not null && (!string.Equals(host, dial.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) || port != dial.Port);

        // Defence in depth: the endpoint was vetted when it was saved, but a client built any other way is held to the same rules.
        if (target is not null && AiEndpointPolicy.CheckEndpoint(provider, target, allowedHosts) is { } refusal)
        {
            throw new AiEndpointRefusedException(refusal);
        }

        bool listed = AiEndpointPolicy.IsListed(host, allowedHosts);
        IReadOnlyList<IPAddress> judged = await AddressesOf(host, cancellationToken);
        if (AiEndpointPolicy.CheckAddresses(provider, listed, judged) is { } blocked)
        {
            throw new AiEndpointRefusedException(blocked);
        }

        // Plain http to a name that says it is this machine is what lets a key travel unencrypted (AiEndpointPolicy.MayCarryKey judges by
        // name), so the name has to really be this machine: "localhost" that resolves elsewhere is refused.
        if (target is not null && target.Scheme == Uri.UriSchemeHttp && AiEndpointPolicy.IsLoopbackHost(target) && judged.Any(a => OutboundAddressGuard.Classify(a) != AddressKind.Loopback))
        {
            throw new AiEndpointRefusedException("The host name says it is this machine (localhost) but does not resolve to a loopback address.");
        }

        IReadOnlyList<IPAddress> toDial = viaProxy ? await AddressesOf(dial.Host.Trim('[', ']'), cancellationToken) : judged;
        int dialPort = dial.Port;

        Exception? last = null;
        foreach (IPAddress address in toDial)
        {
            try
            {
                return await network.ConnectAsync(address, dialPort, cancellationToken);
            }
            catch (Exception error) when (error is SocketException or IOException)
            {
                last = error;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private async ValueTask<IReadOnlyList<IPAddress>> AddressesOf(string host, CancellationToken cancellationToken)
    {
        return IPAddress.TryParse(host, out IPAddress? literal) ? [literal] : await network.ResolveAsync(host, cancellationToken);
    }
}
