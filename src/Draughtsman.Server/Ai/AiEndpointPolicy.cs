using System.Net;
using Draughtsman.Server.Hardening;

namespace Draughtsman.Server.Ai;

/// <summary>
/// Who may the AI endpoint point at (AU-11, option A; docs/ai-endpoint-policy.md). Pure rules, no network: the same
/// functions judge draughtsman.yaml, the admin AI page and, through <see cref="AiConnectGuard"/>, the address a connection is
/// really about to be made to. What an IP address <i>is</i> (public, private, loopback, blocked) is not decided here but by the shared
/// <see cref="OutboundAddressGuard"/>; this class only says what each provider may do with that answer.
/// <list type="bullet">
/// <item>A keyed provider (openai, anthropic, azure-openai) may use only its published host unless the operator lists the host in
/// <c>ai.allowedEndpointHosts</c> (draughtsman.yaml only, never the admin page).</item>
/// <item>Ollama may use loopback and private addresses. Anything else needs listing too, because Ollama can carry a bearer token
/// (a proxied install), so "any public host" would put the key-exfiltration hole straight back.</item>
/// <item>Link-local and cloud-metadata ranges are refused for every provider, listed or not, as a literal address, as the address a
/// name resolves to, and in the IPv6 forms that embed an IPv4 address (::ffff:169.254.169.254, NAT64, 6to4, ::a.b.c.d).</item>
/// <item>A key is never carried over plain http except to loopback.</item>
/// </list>
/// Every message is fixed text: none echoes a configured host or address, which can be hostile or secret.
/// </summary>
internal static class AiEndpointPolicy
{
    /// <summary>The Azure OpenAI wildcard: every resource has its own host under it.</summary>
    private const string AzureSuffix = ".openai.azure.com";

    /// <summary>
    /// Why <paramref name="endpoint"/> may not be used by <paramref name="provider"/>, judged from the URL alone (no DNS), or null.
    /// Run wherever an endpoint is accepted; <see cref="AiConnectGuard"/> repeats it on the request that is actually sent.
    /// </summary>
    public static string? CheckEndpoint(string provider, Uri endpoint, IReadOnlyList<string> allowedHosts)
    {
        string host = HostOf(endpoint);
        bool listed = IsListed(host, allowedHosts);

        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            AddressKind kind = OutboundAddressGuard.Classify(literal);
            if (kind == AddressKind.Blocked)
            {
                return BlockedMessage;
            }

            if (provider == AiProviders.Ollama && !listed && kind == AddressKind.Public)
            {
                return OllamaPublicMessage;
            }
        }

        if (!AiProviders.RequiresKey(provider))
        {
            return null;
        }

        if (!listed && !IsPublishedHost(provider, host))
        {
            return $"{ProviderLabel(provider)} may only use {PublishedHostText(provider)}. To use another host, an operator lists it under ai.allowedEndpointHosts in draughtsman.yaml; it cannot be changed from this page.";
        }

        return endpoint.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(endpoint)
            ? "An API key is never sent over plain http except to this machine (a loopback address). Use an https endpoint."
            : null;
    }

    /// <summary>
    /// Why a connection to a name that resolved to <paramref name="addresses"/> must not be made, or null. Every address is judged, not
    /// the first: a name that also answers with a blocked address is refused whole, so an attacker's DNS cannot mix a good record with a bad one.
    /// </summary>
    public static string? CheckAddresses(string provider, bool hostIsListed, IEnumerable<IPAddress> addresses)
    {
        foreach (IPAddress address in addresses)
        {
            AddressKind kind = OutboundAddressGuard.Classify(address);
            if (kind == AddressKind.Blocked)
            {
                return BlockedMessage;
            }

            if (provider == AiProviders.Ollama && !hostIsListed && kind == AddressKind.Public)
            {
                return OllamaPublicMessage;
            }
        }

        return null;
    }

    public const string BlockedMessage = "The endpoint is a link-local or cloud-metadata address, which is never allowed.";

    public const string OllamaPublicMessage = "Ollama may use only loopback and private addresses. To use another host, an operator lists it under ai.allowedEndpointHosts in draughtsman.yaml.";

    /// <summary>The host of <paramref name="endpoint"/> as DNS and the allow-list compare it: lower case, punycode, IPv6 without brackets.</summary>
    public static string HostOf(Uri endpoint)
    {
        return endpoint.IdnHost.Trim('[', ']').ToLowerInvariant();
    }

    /// <summary>Host and effective port, e.g. <c>api.openai.com:443</c>. "Changed the host" and "the same host" both mean this.</summary>
    public static string AuthorityOf(Uri endpoint)
    {
        return $"{HostOf(endpoint)}:{endpoint.Port}";
    }

    /// <summary>Same as <see cref="AuthorityOf(Uri)"/> for an endpoint written as text; empty for text that is not an absolute URL.</summary>
    public static string AuthorityOf(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ? AuthorityOf(uri) : string.Empty;
    }

    public static bool IsListed(string host, IReadOnlyList<string> allowedHosts)
    {
        foreach (string entry in allowedHosts)
        {
            if (Matches(Normalise(entry), host))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for an entry <see cref="IsListed"/> can match: a host name, an IP address or <c>*.suffix</c>. A bare <c>*</c>, a URL or a path is not one.</summary>
    public static bool IsUsableEntry(string? entry)
    {
        string normal = Normalise(entry);
        if (normal.Length == 0 || normal.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '@' or '?' or '#' or '%'))
        {
            return false;
        }

        if (normal.StartsWith("*.", StringComparison.Ordinal))
        {
            string suffix = normal[2..];
            return suffix.Length > 0 && !suffix.Contains('*', StringComparison.Ordinal) && !suffix.StartsWith('.');
        }

        return !normal.Contains('*', StringComparison.Ordinal) && (IPAddress.TryParse(normal, out _) || !normal.Contains(':', StringComparison.Ordinal));
    }

    public static bool IsPublishedHost(string provider, string host)
    {
        return provider switch
        {
            AiProviders.OpenAi => host == "api.openai.com",
            AiProviders.Anthropic => host == "api.anthropic.com",
            AiProviders.AzureOpenAi => host.Length > AzureSuffix.Length && host.EndsWith(AzureSuffix, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// A key may leave over https, or over http to this machine only. Loopback is a literal 127.0.0.0/8, ::1 (or ::ffff:127.x) or the name
    /// <c>localhost</c> (and <c>*.localhost</c>); <see cref="AiConnectGuard"/> then insists the name really resolves to loopback.
    /// </summary>
    public static bool MayCarryKey(Uri endpoint)
    {
        return endpoint.Scheme == Uri.UriSchemeHttps || IsLoopbackHost(endpoint);
    }

    public static bool IsLoopbackHost(Uri endpoint)
    {
        string host = HostOf(endpoint);
        return IPAddress.TryParse(host, out IPAddress? address)
            ? OutboundAddressGuard.Classify(address) == AddressKind.Loopback
            : host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal);
    }

    private static string Normalise(string? entry)
    {
        return (entry ?? string.Empty).Trim().Trim('[', ']').ToLowerInvariant();
    }

    private static bool Matches(string entry, string host)
    {
        if (entry.Length == 0)
        {
            return false;
        }

        if (entry.StartsWith("*.", StringComparison.Ordinal))
        {
            // *.example.com matches a.example.com and a.b.example.com, never example.com itself.
            string suffix = entry[1..];
            return entry.Length > 2 && host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.Ordinal);
        }

        return entry == host;
    }

    private static string ProviderLabel(string provider)
    {
        return provider switch
        {
            AiProviders.OpenAi => "openai",
            AiProviders.Anthropic => "anthropic",
            _ => "azure-openai",
        };
    }

    private static string PublishedHostText(string provider)
    {
        return provider switch
        {
            AiProviders.OpenAi => "api.openai.com",
            AiProviders.Anthropic => "api.anthropic.com",
            _ => "*.openai.azure.com",
        };
    }
}
