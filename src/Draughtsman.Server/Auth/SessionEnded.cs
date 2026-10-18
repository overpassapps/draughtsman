namespace Draughtsman.Server.Auth;

/// <summary>
/// Why a session cookie that used to work no longer does (QA S9), so the sign-in screen can say so instead of appearing
/// from nowhere. It is only ever a coarse reason - <c>revoked</c> (a password was changed or reset, or the person signed
/// out in another window), <c>deactivated</c> or <c>expired</c> - and never who did it: an Editor must not learn which
/// administrator reset their password. It is carried in the <see cref="HeaderName"/> response header on the 401 an API
/// call gets and on the anonymous <c>GET /api/auth/status</c> a reload makes, and only when the request presented a real
/// (once-valid) session cookie, so it tells an unauthenticated caller nothing about any account.
/// </summary>
internal static class SessionEnded
{
    public const string HeaderName = "X-Draughtsman-Session-Ended";

    public const string Revoked = "revoked";

    public const string Deactivated = "deactivated";

    public const string Expired = "expired";

    private static readonly object ItemKey = new();

    /// <summary>Called by the cookie handler when it refuses a session it had a row for.</summary>
    public static void Note(HttpContext context, string reason)
    {
        context.Items[ItemKey] = reason;
    }

    /// <summary>Adds the header when this request's session was refused for a known reason; otherwise does nothing.</summary>
    public static void WriteHeader(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out object? reason) && reason is string text)
        {
            context.Response.Headers[HeaderName] = text;
        }
    }
}
