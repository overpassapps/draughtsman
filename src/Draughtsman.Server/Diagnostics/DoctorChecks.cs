using System.Globalization;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Hardening;

namespace Draughtsman.Server.Diagnostics;

/// <summary>One entry in the data folder, as <c>draughtsman doctor</c> sees it: its name, whether it is a folder, its size, its mode and who owns it.</summary>
public sealed record DataEntry(string Name, bool IsFolder, long Size, string Mode, string Octal, FileOwner? Owner);

/// <summary>What the doctor learned about who runs the server: this process's own account, the account of a server found running here (Linux: read from <c>/proc</c>), and whether this is a container.</summary>
public sealed record AccountFacts(long? CurrentUid, string CurrentName, long? ServerUid, bool Container, bool Windows);

/// <summary>What the doctor learned about the listening side: the URLs the server would bind and whether anything answered on the port.</summary>
public sealed record ListeningFacts(IReadOnlyList<string> Urls, bool UrlsFromEnvironment, bool AllLoopback, bool Container, LoopbackAnswer Probe);

/// <summary>What answered on the server's own port on this machine: nothing, something that is not Draughtsman, or a Draughtsman server and what its health says.</summary>
public sealed record LoopbackAnswer(int Port, bool Reachable, bool IsDraughtsman, string? Health, int? Status);

/// <summary>
/// The doctor's rules that need no network and no sign-in, as plain functions of what was found, so each is tested in both directions
/// without a machine. The doctor's own command (<c>Cli/DoctorCommand</c>) gathers the facts; nothing here touches the disk or the network.
/// </summary>
public static class DoctorChecks
{
    /// <summary>The account the official image runs as (Dockerfile: <c>USER draughtsman</c>, uid 10001): the one a server in a container is expected to be.</summary>
    public const long ImageUid = 10001;

    // ------------------------------------------------------------------------------------------------------ account

    public static DiagnosticItem Account(AccountFacts a)
    {
        const string id = "account";
        const string title = "Account the server runs as";
        string current = a.CurrentName + (a.CurrentUid is { } uid ? " (uid " + uid.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty);
        bool serverIsRoot = a.ServerUid == 0;
        var facts = new Dictionary<string, string>
        {
            ["This command runs as"] = current,
            ["Server process found here"] = a.ServerUid is { } s ? "yes, uid " + s.ToString(CultureInfo.InvariantCulture) : "no",
            ["Container"] = a.Container ? "yes" : "no",
        };

        if (serverIsRoot)
        {
            return DiagnosticItem.Warn(
                id,
                title,
                a.Container
                    ? "The server process in this container runs as root. The official image runs as uid 10001; something overrode it (docker run --user 0, or user: root in compose). A bug in the server would then be a bug with root's permissions, and the data folder fills with root-owned files that the image's own user cannot later write."
                    : "The server process runs as root. Run it as a dedicated, unprivileged account: a systemd service with a user, or a macOS/Windows service account (docs/install-linux.md).",
                facts);
        }

        if (a.CurrentUid == 0 && !a.Windows)
        {
            return DiagnosticItem.Info(
                id,
                title,
                a.ServerUid is { } server
                    ? $"This command runs as root, and the server runs as uid {server.ToString(CultureInfo.InvariantCulture)}. Root can open files that account cannot, so the file checks below compare the owners with uid {server.ToString(CultureInfo.InvariantCulture)} rather than trying to write as root."
                    : a.Container
                        ? $"This command runs as root (docker exec -u 0). The official image's server runs as uid {ImageUid.ToString(CultureInfo.InvariantCulture)}, so the file checks below compare the owners with that. Run it as the image's own user (the default for docker exec) to test exactly what the server can do."
                        : "This command runs as root, and no server is running here to compare with. Root can write anywhere, so a permission fault would not show. Run it as the account the server runs as (for example sudo -u draughtsman draughtsman doctor).",
                facts);
        }

        return DiagnosticItem.Pass(
            id,
            title,
            a.Container
                ? $"Running in a container as {current}, not root."
                : $"This command runs as {current}, not root." + (a.ServerUid is { } other && other != a.CurrentUid ? $" The server runs as uid {other.ToString(CultureInfo.InvariantCulture)}, a different account, so \"writable\" below means writable by {a.CurrentName}; the owners are compared with the server's account." : string.Empty),
            facts);
    }

    /// <summary>The user id the server is expected to run as, for comparing file owners: a server found running here, else the image's user in a container as root, else this command's own account (null where owners mean nothing or cannot be known).</summary>
    public static long? ExpectedServerUid(AccountFacts a)
    {
        if (a.Windows)
        {
            return null;
        }

        if (a.ServerUid is { } server)
        {
            return server;
        }

        if (a.CurrentUid == 0)
        {
            return a.Container ? ImageUid : null;
        }

        return a.CurrentUid;
    }

    // ------------------------------------------------------------------------------------------------------ ownership and modes

    public static DiagnosticItem Ownership(string dataFolder, FileOwner? folderOwner, string folderMode, string folderOctal, IReadOnlyList<DataEntry> entries, long? expectedUid, bool windows)
    {
        const string id = "ownership";
        const string title = "Data folder ownership and permissions";
        var facts = new Dictionary<string, string>
        {
            ["Data folder"] = dataFolder,
            ["Owner"] = FileOwners.Describe(folderOwner),
            ["Mode"] = windows ? "(Windows: see the folder's ACL)" : folderMode + " (" + folderOctal + ")",
        };
        if (expectedUid is { } expected)
        {
            facts["Server account expected"] = "uid " + expected.ToString(CultureInfo.InvariantCulture);
        }

        var problems = new List<string>();
        var warnings = new List<string>();
        if (!windows && expectedUid is { } uid)
        {
            if (folderOwner?.Uid is { } folderUid && folderUid != uid)
            {
                problems.Add($"{dataFolder} is owned by uid {folderUid.ToString(CultureInfo.InvariantCulture)}, not the server's uid {uid.ToString(CultureInfo.InvariantCulture)}");
            }

            foreach (DataEntry entry in entries.Where(e => e.Owner?.Uid is { } owned && owned != uid))
            {
                string what = $"{entry.Name}{(entry.IsFolder ? "/" : string.Empty)} is owned by uid {entry.Owner!.Uid!.Value.ToString(CultureInfo.InvariantCulture)}";
                // The database and the key ring are the ones whose wrong owner stops the server doing its job; the rest is untidy.
                if (IsCritical(entry.Name))
                {
                    problems.Add(what);
                }
                else
                {
                    warnings.Add(what);
                }
            }
        }

        if (!windows && folderMode.Length == 9 && folderMode[3..] != "------")
        {
            warnings.Add($"the data folder's mode is {folderOctal}, open to other accounts: it holds every document, the key ring and the password hashes; 0700 keeps it to the server's account");
        }

        facts["Entries listed"] = entries.Count.ToString(CultureInfo.InvariantCulture);
        IReadOnlyList<string> detail = [.. entries.Take(40).Select(e => $"{e.Mode,-9} {FileOwners.Describe(e.Owner),-26} {MachineChecks.Size(e.Size),9}  {e.Name}{(e.IsFolder ? "/" : string.Empty)}")];
        string account = expectedUid is { } e2 ? "uid " + e2.ToString(CultureInfo.InvariantCulture) : "the server's account";
        if (problems.Count > 0)
        {
            return DiagnosticItem.Fail(
                id,
                title,
                "Files the server needs are owned by the wrong account: " + string.Join("; ", problems) + $". The server (running as {account}) cannot write them, so it cannot save anything: /api/health reports degraded and a correct password is answered with an error instead of a session, while the page still loads. This usually follows a restore, a copy or a docker cp done as root.",
                facts,
                detail,
                new DiagnosticFix(Shell: $"# As root, hand everything back to the server's account:\n# chown -R {(expectedUid is { } owner ? owner.ToString(CultureInfo.InvariantCulture) : "<server account>")} {dataFolder}\n# Docker: docker exec -u 0 <container> chown -R {ImageUid.ToString(CultureInfo.InvariantCulture)}:{ImageUid.ToString(CultureInfo.InvariantCulture)} /data"));
        }

        if (warnings.Count > 0)
        {
            return DiagnosticItem.Warn(id, title, string.Join("; ", warnings) + ".", facts, detail);
        }

        return DiagnosticItem.Pass(id, title, windows ? "Owners are listed below; Windows keeps the protection in the folder's ACL." : $"Everything listed is owned by {account} and the folder is closed to other accounts.", facts, detail);
    }

    private static bool IsCritical(string name)
    {
        return name.StartsWith("data.db", StringComparison.OrdinalIgnoreCase) || name is "keys" or "draughtsman.yaml" or "ai" or "email";
    }

    // ------------------------------------------------------------------------------------------------------ listening

    public static DiagnosticItem Listening(ListeningFacts l)
    {
        const string id = "listening";
        const string title = "Listening address";
        var facts = new Dictionary<string, string>
        {
            ["The server binds"] = string.Join("; ", l.Urls),
            ["Set by"] = l.UrlsFromEnvironment ? "ASPNETCORE_URLS (or --urls), which wins over draughtsman.yaml" : "server.host and server.port in draughtsman.yaml",
            ["Answered on 127.0.0.1:" + l.Probe.Port.ToString(CultureInfo.InvariantCulture)] = !l.Probe.Reachable ? "nothing" : l.Probe.IsDraughtsman ? "a Draughtsman server (health: " + (l.Probe.Health ?? "unknown") + ")" : "something that is not Draughtsman (HTTP " + (l.Probe.Status?.ToString(CultureInfo.InvariantCulture) ?? "?") + ")",
        };

        if (l.Container && l.AllLoopback)
        {
            return DiagnosticItem.Fail(
                id,
                title,
                $"The server listens only on loopback ({string.Join(", ", l.Urls)}) inside a container. A published port (-p 5180:5180) reaches the container's own address, never its loopback, so nothing outside the container can connect. The image sets ASPNETCORE_URLS=http://0.0.0.0:5180; something replaced it.",
                facts,
                fix: new DiagnosticFix(Shell: "# Remove the ASPNETCORE_URLS override, or set the environment variable:\n# ASPNETCORE_URLS=http://0.0.0.0:5180"));
        }

        if (l.Probe.Reachable && !l.Probe.IsDraughtsman)
        {
            return DiagnosticItem.Warn(
                id,
                title,
                $"Something answers on port {l.Probe.Port.ToString(CultureInfo.InvariantCulture)} here, and it is not a Draughtsman server (HTTP {l.Probe.Status?.ToString(CultureInfo.InvariantCulture) ?? "?"}). Draughtsman cannot start on a port something else holds: choose another with server.port or --urls.",
                facts);
        }

        if (l.Probe.Reachable)
        {
            return DiagnosticItem.Pass(id, title, $"A Draughtsman server is running here and answers on port {l.Probe.Port.ToString(CultureInfo.InvariantCulture)} (health: {l.Probe.Health ?? "unknown"}). It binds {string.Join(", ", l.Urls)}.", facts);
        }

        return DiagnosticItem.Info(id, title, $"No server is answering on 127.0.0.1:{l.Probe.Port.ToString(CultureInfo.InvariantCulture)}: it is not running (this check still works), or it listens elsewhere. When started it would bind {string.Join(", ", l.Urls)}{(l.AllLoopback ? ", which only this machine can reach" : string.Empty)}.", facts);
    }

    // ------------------------------------------------------------------------------------------------------ proxy settings

    /// <summary>The settings that decide how a proxy in front of the server is treated, and the common mistakes in them.</summary>
    public static IReadOnlyList<DiagnosticItem> ProxySettings(ServerConfig server, bool container, ForwardedObservationsReport? observed)
    {
        var items = new List<DiagnosticItem>();
        string[] proxies = [.. server.KnownProxies.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())];
        ForwardedProxies.Parsed parsed = ForwardedProxies.Parse(proxies);
        var facts = new Dictionary<string, string>
        {
            ["server.forceHttps"] = server.ForceHttps ? "on" : "off",
            ["server.knownProxies"] = proxies.Length == 0 ? "(empty)" : string.Join(", ", proxies),
            ["server.allowedHosts"] = server.AllowedHosts.Any(h => !string.IsNullOrWhiteSpace(h)) ? string.Join(", ", server.AllowedHosts) : "(empty)",
        };

        if (parsed.Rejected.Count > 0)
        {
            items.Add(DiagnosticItem.Warn(
                "proxies-unreadable",
                "Reverse proxy settings",
                "server.knownProxies has entries the server cannot read, so the proxies they name are NOT trusted: " + string.Join("; ", parsed.Rejected) + ".",
                facts,
                fix: new DiagnosticFix(Yaml: "server:\n  knownProxies:\n    - \"172.17.0.1\"        # an address\n    - \"172.17.0.0/16\"      # or a range; never a host name")));
        }

        if (server.ForceHttps && proxies.Length == 0)
        {
            items.Add(DiagnosticItem.Warn(
                "proxies-missing",
                "Reverse proxy settings",
                "server.forceHttps is on but server.knownProxies is empty. Cookies are Secure, which is right behind a TLS proxy, but the server believes nobody: every visitor looks like the proxy's address" + (container ? " (in Docker, the bridge gateway such as 172.17.0.1)" : string.Empty) + ", so twenty failed sign-ins by anyone lock EVERYONE out for 15 minutes and the security log cannot tell people apart. Admin > Connection check shows the exact address to list.",
                facts,
                fix: new DiagnosticFix(Yaml: container ? "server:\n  forceHttps: true\n  knownProxies:\n    - \"172.17.0.0/16\"   # Docker's default bridge; use your proxy's address if it differs" : "server:\n  forceHttps: true\n  knownProxies:\n    - \"<your proxy's address>\"")));
        }

        if (observed is not null)
        {
            foreach (PeerObservation peer in observed.Peers.Where(p => !p.Trusted && p.ForwardedRequests > 0))
            {
                items.Add(DiagnosticItem.Warn(
                    "proxies-observed",
                    "Reverse proxy settings",
                    $"Since it last started, the server received proxy headers (X-Forwarded-For, X-Forwarded-Proto, X-Forwarded-Host or CF-Connecting-IP) on {peer.ForwardedRequests.ToString(CultureInfo.InvariantCulture)} of {peer.Requests.ToString(CultureInfo.InvariantCulture)} requests from {peer.Peer}, which is not in server.knownProxies, so they were ignored.",
                    facts,
                    fix: new DiagnosticFix(Yaml: ConnectionChecks.KnownProxiesYaml(peer.Peer))));
            }
        }

        if (items.Count == 0)
        {
            items.Add(DiagnosticItem.Pass(
                "proxies",
                "Reverse proxy settings",
                proxies.Length == 0
                    ? "No reverse proxy is configured (server.knownProxies is empty): the server treats each connection's own address as the visitor, which is right for a direct install. Behind a proxy, list it (see Admin > Connection check)."
                    : $"server.knownProxies lists {proxies.Length.ToString(CultureInfo.InvariantCulture)} entr{(proxies.Length == 1 ? "y" : "ies")}, all readable.",
                facts));
        }

        return items;
    }

    // ------------------------------------------------------------------------------------------------------ clock

    /// <summary>
    /// Whether the system clock is believable, without asking anyone: it must not be before the day this build was made (a clock reset to
    /// 1970 or an unset RTC), and it must not be behind files this data folder already holds (a clock that went backwards: the session cookie,
    /// logs are then wrong, and a cookie stamped 30 days from a clock 30 days behind a browser's is born expired).
    /// </summary>
    public static DiagnosticItem Clock(DateTimeOffset now, DateOnly buildDate, DateTimeOffset? newestFile, string? newestFileName)
    {
        const string id = "clock";
        const string title = "System clock";
        var facts = new Dictionary<string, string>
        {
            ["Now (UTC)"] = now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["This build is dated"] = buildDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        if (DateOnly.FromDateTime(now.UtcDateTime) < buildDate.AddDays(-1))
        {
            return DiagnosticItem.Fail(
                id,
                title,
                $"The clock says {now.UtcDateTime:yyyy-MM-dd}, which is before this build was made ({buildDate:yyyy-MM-dd}). It is wrong: session cookies, API token expiry and every log line are wrong with it, and a cookie stamped from a clock this far behind a visitor's is expired the moment it is set.",
                facts,
                ["Set the clock and keep it right with NTP (a container uses its host's clock)."]);
        }

        if (newestFile is { } file && file > now.AddMinutes(5))
        {
            facts["Newest file in the data folder"] = $"{newestFileName} ({file.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z)";
            return DiagnosticItem.Warn(
                id,
                title,
                $"{newestFileName} in the data folder is dated {file.UtcDateTime:yyyy-MM-dd HH:mm}Z, in the future of this machine's clock ({now.UtcDateTime:yyyy-MM-dd HH:mm}Z). The clock has gone backwards, or the data came from a machine whose clock was ahead.",
                facts,
                ["Check the clock (and NTP). A clock that is behind by 30 days or more makes the session cookie expire as it is set."]);
        }

        return DiagnosticItem.Pass(id, title, $"The clock reads {now.UtcDateTime:yyyy-MM-dd HH:mm}Z, after this build's date and not behind the files in the data folder. (Whether it matches the real time cannot be known offline; Admin > Connection check compares it with your browser's.)", facts);
    }

    // ------------------------------------------------------------------------------------------------------ the result

    /// <summary>0 when nothing is wrong, 1 when there are warnings, 2 when there are failures.</summary>
    public static int ExitCode(IEnumerable<DiagnosticItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        DiagnosticItem[] all = [.. items];
        return all.Any(i => i.Status == DiagnosticStatus.Fail) ? 2 : all.Any(i => i.Status == DiagnosticStatus.Warn) ? 1 : 0;
    }
}
