using System.Text.Encodings.Web;
using Draughtsman.Core.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Draughtsman.Server.Auth;

/// <summary>
/// Authenticates the <see cref="SessionCookie"/>. Every request re-checks the session row (not a signed/encrypted
/// ticket carrying stale claims), so a revoke or sign-out takes effect on the very next request, and a deactivated
/// user's existing cookie stops working immediately.
/// </summary>
public sealed class CookieSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IUserStore userStore)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (SessionCookie.Read(Request) is not { } secret)
        {
            return AuthenticateResult.NoResult();
        }

        string hash = TokenGenerator.Hash(secret);
        Session? session = await userStore.GetSessionByHash(hash, Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.NoResult();
        }

        // Read the user first, even for a session already known to be dead, so the reason can be told apart (S9): a
        // deactivated account is not a password change, and neither is a session that simply ran out.
        User? user = await userStore.GetUserById(session.UserId, Context.RequestAborted);
        if (user is null || !user.Active)
        {
            SessionEnded.Note(Context, SessionEnded.Deactivated);
            return AuthenticateResult.NoResult();
        }

        if (session.Revoked)
        {
            SessionEnded.Note(Context, SessionEnded.Revoked);
            return AuthenticateResult.NoResult();
        }

        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            SessionEnded.Note(Context, SessionEnded.Expired);
            return AuthenticateResult.NoResult();
        }

        var ticket = new AuthenticationTicket(PrincipalFactory.ForUser(user, AuthSchemes.Cookie, sessionId: session.Id), AuthSchemes.Cookie);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        SessionEnded.WriteHeader(Context);
        return BearerChallenge.WriteAsync(Response);
    }
}
