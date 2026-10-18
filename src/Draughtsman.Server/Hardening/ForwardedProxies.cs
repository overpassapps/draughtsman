using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// Reads <c>server.knownProxies</c> (AU-02): the addresses whose <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> are
/// believed. Until now only single IP addresses were understood and any other entry, a CIDR range such as a Docker
/// network's <c>172.18.0.0/16</c> or a hostname, was silently dropped, so a proxy whose address changes on redeploy quietly
/// lost trust and every cookie lost its Secure flag. An entry is now an address (<c>10.0.0.1</c>, <c>::1</c>) or a range in
/// CIDR form (<c>10.0.0.0/8</c>, <c>fd00::/8</c>; an address with host bits set, <c>10.0.0.5/8</c>, means the network it is in); anything else is reported by name in the log rather than dropped without a word.
/// </summary>
public static class ForwardedProxies
{
    public sealed record Parsed(IReadOnlyList<IPAddress> Addresses, IReadOnlyList<IPNetwork> Networks, IReadOnlyList<string> Rejected);

    public static Parsed Parse(IEnumerable<string> entries)
    {
        var addresses = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        var rejected = new List<string>();
        foreach (string raw in entries)
        {
            string entry = raw?.Trim() ?? string.Empty;
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry.Contains('/', StringComparison.Ordinal))
            {
                if (IPNetwork.TryParse(entry, out IPNetwork network))
                {
                    networks.Add(network);
                }
                else
                {
                    rejected.Add(Describe(entry));
                }
            }
            else if (IPAddress.TryParse(entry, out IPAddress? address))
            {
                addresses.Add(address);
            }
            else
            {
                rejected.Add($"{entry} (a hostname or other non-address: list the proxy's IP address or a CIDR range)");
            }
        }

        return new Parsed(addresses, networks, rejected);
    }

    /// <summary>Configures the forwarded-headers middleware from <c>server.knownProxies</c>; the entries that cannot be read are logged when the pipeline is built.</summary>
    public static IServiceCollection ConfigureKnownProxies(this IServiceCollection services, IEnumerable<string> entries)
    {
        Parsed parsed = Parse(entries);
        services.AddOptions<ForwardedHeadersOptions>().Configure<ILoggerFactory>((options, loggers) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            Apply(options, parsed, loggers.CreateLogger("Draughtsman.Server.KnownProxies"));
        });
        return services;
    }

    /// <summary>
    /// Trusts exactly the parsed entries, plus what the framework trusts on its own (a loopback peer: a proxy on the same machine).
    /// Logs each entry it could not read, so a typo is found at start-up instead of as a missing Secure flag.
    /// </summary>
    public static void Apply(ForwardedHeadersOptions options, Parsed parsed, ILogger logger)
    {
        options.KnownProxies.Clear();
        foreach (IPAddress address in parsed.Addresses)
        {
            options.KnownProxies.Add(address);
        }

        foreach (IPNetwork network in parsed.Networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        foreach (string entry in parsed.Rejected)
        {
            logger.LogWarning("server.knownProxies entry {Entry} was ignored; the proxy it names is not trusted", entry);
        }
    }

    private static string Describe(string entry)
    {
        return $"{entry} (not a CIDR range such as 10.0.0.0/8 or fd00::/8)";
    }
}
