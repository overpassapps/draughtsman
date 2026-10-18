namespace Draughtsman.Server.Auth;

/// <summary>
/// The session cookie's name and lifetime. No "keep me signed in" option (design/atlas/login-admin.md): every session
/// is long-lived and ends only by sign-out, admin revoke, or this fixed expiry.
/// </summary>
public static class SessionCookie
{
    public const string Name = "ds_session";

    /// <summary>
    /// The name used once the cookie is Secure (AU-18): the <c>__Host-</c> prefix makes a browser refuse the cookie unless it
    /// is Secure, has Path=/ and no Domain, which also stops a sibling subdomain planting a session cookie of its own
    /// (cookie tossing). Only valid on HTTPS, so a plain-HTTP install keeps <see cref="Name"/>.
    /// </summary>
    public const string HostName = "__Host-ds_session";

    /// <summary>The name to set the cookie under for this request.</summary>
    public static string NameFor(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.IsHttps ? HostName : Name;
    }

    /// <summary>
    /// The session secret the request carries, under either name: a session issued before the prefix existed (or over plain
    /// HTTP) is still read, so the upgrade signs nobody out.
    /// </summary>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (string name in new[] { HostName, Name })
        {
            if (request.Cookies.TryGetValue(name, out string? secret) && !string.IsNullOrEmpty(secret))
            {
                return secret;
            }
        }

        return null;
    }

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// HttpOnly always; Secure follows the request scheme, which <c>server.forceHttps</c> (security review F5)
    /// overrides to always read true, before this ever runs, for an instance behind a proxy whose forwarded scheme
    /// this process cannot otherwise see.
    /// </summary>
    public static CookieOptions Options(HttpRequest request, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expiresAt,
        };
    }

    /// <summary>Options for deleting the cookie: same attributes minus an expiry, so the browser matches and clears it.</summary>
    public static CookieOptions DeleteOptions(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        };
    }
}
