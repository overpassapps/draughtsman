using System.Security.Claims;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth.PasswordReset;
using Draughtsman.Server.Hosting;
using Microsoft.AspNetCore.Antiforgery;

namespace Draughtsman.Server.Auth.Endpoints;

/// <summary>
/// First run, sign-in and sign-out (build-spec-v0.1.md section 7; design/atlas/login-admin.md). Anonymous except
/// logout, which needs an identity to sign out of. See README's Sign-in section for the flows this implements.
/// </summary>
public static class AuthEndpoints
{
    private const int MaxEmailLength = 254; // RFC 5321 5.3.1.1 - also caps how long a throttle dictionary key can be.

    /// <summary>
    /// Serialises first-run end to end within this process (security review F1.1): the atomic
    /// <see cref="IUserStore.TryCreateFirstUser"/> is what actually guarantees at most one admin ever gets created
    /// (proven against real concurrent callers over both providers), but there is no reason to let concurrent
    /// first-run requests all pay for a database round trip when one process can just queue them.
    /// </summary>
    private static readonly SemaphoreSlim FirstRunLock = new(1, 1);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/auth");

        group.MapGet("/status", Status).WithName("AuthStatus").AllowAnonymous();
        group.MapPost("/first-run", FirstRun).WithName("AuthFirstRun").AllowAnonymous();
        group.MapPost("/login", Login).WithName("AuthLogin").AllowAnonymous();
        group.MapPost("/logout", Logout).WithName("AuthLogout");
        // Cookie-only like the account and token endpoints (security review F3): a token, however privileged its owner, never manages the account behind it.
        group.MapPost("/password", ChangePassword).WithName("AuthChangePassword").RequireAuthorization(AuthPolicies.CookieOnly);
        return app;
    }

    private static async Task<IResult> Status(HttpContext context, IUserStore users, IAntiforgery antiforgery, DataFolder data, PasswordResetPolicy resetPolicy, CancellationToken cancellationToken)
    {
        bool firstRunRequired = !await users.HasAnyUsers(cancellationToken);
        if (context.User.Identity?.IsAuthenticated != true)
        {
            // A reload after the session was ended (password reset, deactivation, expiry) still holds the dead cookie: say why (S9).
            SessionEnded.WriteHeader(context);
            // The data folder and host kind go out only while the instance has no administrator (PR-02): that is the one
            // moment the page needs them, and this route is anonymous, so they are not offered once the instance is set up.
            return Results.Ok(new AuthStatusResponse(firstRunRequired, null, firstRunRequired ? new FirstRunHintsResponse(data.Path, HostKinds.Name(HostKinds.Detect())) : null, new PasswordResetHintsResponse(resetPolicy.SelfServiceAvailable)));
        }

        User? user = await users.GetUserById(context.User.UserId() ?? string.Empty, cancellationToken);
        // Re-issued on every authenticated status check (security review F7): the SPA calls this on every load, and
        // the antiforgery cookie is not tied to the session cookie's own 30-day lifetime - a browser that dropped it
        // between visits (e.g. it was never given an explicit expiry) would otherwise leave a still-signed-in caller
        // unable to save anything until they signed out and back in.
        CsrfMiddleware.IssueTokenCookie(context, antiforgery);
        return Results.Ok(new AuthStatusResponse(firstRunRequired, user is null ? null : UserResponse.From(user), null, new PasswordResetHintsResponse(resetPolicy.SelfServiceAvailable)));
    }

    /// <summary>
    /// Creates the one admin a fresh instance needs. Closed for good the moment any user exists (§7), gated by a
    /// one-time setup code printed to the server log at startup (security review F1.2/F1.1) rather than by "this
    /// request came from the local machine" - a check Docker and reverse-proxy deployments would let any caller
    /// through regardless. F1.2's own bootstrap-only-from-this-machine alternative is deliberately not built: the
    /// setup code is the sole gate.
    /// </summary>
    private static async Task<IResult> FirstRun(FirstRunRequest? body, HttpContext context, IUserStore users, SetupCodeGate setupCode, FailedSignInThrottle throttle, IAntiforgery antiforgery, SecurityEventLog events, CancellationToken cancellationToken)
    {
        if (await users.HasAnyUsers(cancellationToken))
        {
            return AlreadyCompleted();
        }

        if (body is null)
        {
            return MalformedBody();
        }

        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string throttleKey = "setup:" + ip;
        if (throttle.IsBlocked(throttleKey))
        {
            if (throttle.TryReportTrip(throttleKey))
            {
                await events.Record(context, SecurityEventTypes.SignInThrottled, detail: "first-run setup code guesses");
            }

            return TooManyAttempts();
        }

        // Recorded before verifying (security review F4, same reasoning as Login below): a burst of concurrent
        // guesses must not all land before the first one is counted. Handed back below when the code was right: the
        // budget is for wrong codes, and a right code with something else wrong (a password too short) must not spend it.
        long ticket = throttle.RecordFailure(throttleKey);
        if (!setupCode.Verify(body.SetupCode))
        {
            await events.Record(context, SecurityEventTypes.FirstRunFailed, detail: "invalid setup code");
            return InvalidSetupCode();
        }

        throttle.Refund(throttleKey, ticket);

        if (!TryValidateNewAccount(body.Email, body.DisplayName, body.Password, out IResult? problem))
        {
            return problem!;
        }

        await FirstRunLock.WaitAsync(cancellationToken);
        try
        {
            User? admin = await users.TryCreateFirstUser(body.Email!, body.DisplayName!, UserRole.Admin, PasswordHasher.Hash(body.Password!), DateTimeOffset.UtcNow, cancellationToken);
            if (admin is null)
            {
                return AlreadyCompleted();
            }

            throttle.Clear(throttleKey);
            setupCode.Consume();
            await StartSession(admin, context, users, antiforgery, cancellationToken);
            await events.Record(context, SecurityEventTypes.FirstRunCompleted, actor: admin, method: SecurityEventMethods.Password, detail: "first administrator created");
            return Results.Created("/api/auth/status", UserResponse.From(admin));
        }
        finally
        {
            FirstRunLock.Release();
        }
    }

    /// <summary>
    /// Generic failure for a wrong password or an unknown email alike (design/atlas/login-admin.md: never "user not
    /// found"). Throttled on the (email, IP) pair, not on email alone (security review F4): keying purely on email
    /// would let an attacker lock a real user out of their own account just by guessing wrong from anywhere, whereas
    /// this pair requires the attacker's own IP to eat the budget. A separate, IP-only bucket still catches one
    /// source spraying many different accounts, and it is never cleared by a success (below) - an attacker
    /// interleaving one known-good login must not be able to reset their own spray budget. A success only refunds its
    /// own single charge, so correct logins never count toward either budget (AU-01: an office NAT signing in six
    /// times used to get a 429).
    /// </summary>
    private static async Task<IResult> Login(LoginRequest? body, HttpContext context, IUserStore users, FailedSignInThrottle throttle, IAntiforgery antiforgery, SecurityEventLog events, CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return MalformedBody();
        }

        string email = (body.Email ?? string.Empty).Trim();
        if (email.Length > MaxEmailLength)
        {
            email = email[..MaxEmailLength];
        }

        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string? pairKey = email.Length == 0 ? null : "pair:" + email.ToLowerInvariant() + "|" + ip;
        string ipKey = "ip:" + ip;

        bool pairBlocked = pairKey is not null && throttle.IsBlocked(pairKey);
        if (pairBlocked || throttle.IsBlocked(ipKey, FailedSignInThrottle.IpMaxAttempts))
        {
            // Once per throttle window per bucket, not per refused request: see FailedSignInThrottle.TryReportTrip.
            string tripKey = pairBlocked ? pairKey! : ipKey;
            if (throttle.TryReportTrip(tripKey))
            {
                await events.Record(context, SecurityEventTypes.SignInThrottled, detail: pairBlocked ? "too many failed sign-ins for one account from this address" : "too many failed sign-ins from this address", targetLabel: AttemptedEmail(email));
            }

            return TooManyAttempts();
        }

        // Recorded before the (possibly dummy, F6) Argon2 verify runs, not after: Argon2's own cost is tens of
        // milliseconds, long enough for a burst of concurrent requests to all pass the IsBlocked check above before
        // any of them reached the old post-verify RecordFailure call, letting far more than MaxAttempts guesses
        // through in one window (security review F4).
        if (pairKey is not null)
        {
            throttle.RecordFailure(pairKey);
        }

        long ipTicket = throttle.RecordFailure(ipKey);

        User? user = email.Length == 0 ? null : await users.GetUserByEmail(email, cancellationToken);
        bool active = user is not null && user.Active;
        // Verified against a fixed dummy hash when there is no active account to check against (security review
        // F6), so Argon2's cost runs either way and a caller cannot distinguish "no such account" from "wrong
        // password" by response latency.
        string hashToVerify = active ? user!.PasswordHash : PasswordHasher.DummyHash.Value;
        bool passwordOk = !string.IsNullOrEmpty(body.Password) && PasswordHasher.Verify(body.Password, hashToVerify);

        if (!active || !passwordOk)
        {
            // The reason is recorded for the admin reading the log; the response stays identical either way.
            string reason = user is null ? "unknown account" : !user.Active ? "inactive account" : "wrong password";
            await events.Record(context, SecurityEventTypes.SignInFailed, target: user, detail: reason, method: SecurityEventMethods.Password, targetLabel: AttemptedEmail(email));
            return InvalidCredentials();
        }

        // Correct password: hand back the charges this attempt took. The pair bucket is cleared outright (this caller
        // proved they own the account); the IP bucket only loses this one attempt.
        if (pairKey is not null)
        {
            throttle.Clear(pairKey);
        }

        throttle.Refund(ipKey, ipTicket);

        await users.RecordSignIn(user!.Id, DateTimeOffset.UtcNow, cancellationToken);
        await StartSession(user, context, users, antiforgery, cancellationToken);
        await events.Record(context, SecurityEventTypes.SignInSucceeded, actor: user, method: SecurityEventMethods.Password);
        return Results.Ok(UserResponse.From(user));
    }

    /// <summary>
    /// The email a sign-in attempt named, for the security event log, only when it looks like an email. People do
    /// paste a password into the email box, and that must never reach a log table.
    /// </summary>
    private static string? AttemptedEmail(string email)
    {
        return email.Contains('@', StringComparison.Ordinal) && !email.Any(char.IsWhiteSpace) ? email : null;
    }

    private static async Task<IResult> Logout(HttpContext context, IUserStore users, IAntiforgery antiforgery, SecurityEventLog events, CancellationToken cancellationToken)
    {
        // Recorded first, while HttpContext.User still says who is signing out.
        await events.Record(context, SecurityEventTypes.SignedOut);
        string? sessionId = context.User.FindFirst(PrincipalFactory.SessionIdClaim)?.Value;
        if (sessionId is not null)
        {
            await users.RevokeSession(sessionId, cancellationToken);
        }

        // Both names: the cookie may have been set before the __Host- prefix existed, or over plain HTTP.
        context.Response.Cookies.Delete(SessionCookie.NameFor(context.Request), SessionCookie.DeleteOptions(context.Request));
        if (context.Request.IsHttps)
        {
            context.Response.Cookies.Delete(SessionCookie.Name, SessionCookie.DeleteOptions(context.Request));
        }
        // Anonymous from here on, so the antiforgery pair reissued below is not still bound to the old identity
        // (CsrfMiddleware's doc comment explains why that would matter).
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        CsrfMiddleware.IssueTokenCookie(context, antiforgery);
        return Results.NoContent();
    }

    /// <summary>
    /// A signed-in user changing their own password (AU-03): the current password is required (so a hijacked session
    /// cannot take the account over for good), and every other session of theirs is revoked, keeping only the one making
    /// the request. Their API tokens are left alone - a token is revoked on its own page, and silently killing every
    /// agent configuration on a routine password change would teach people not to change it. A wrong current password is
    /// throttled per user (the same budget as sign-in) and refused with one generic message.
    /// </summary>
    private static async Task<IResult> ChangePassword(ChangePasswordRequest? body, HttpContext context, IUserStore users, IPasswordResetStore resets, TimeProvider clock, FailedSignInThrottle throttle, SecurityEventLog events, CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return MalformedBody();
        }

        if (string.IsNullOrEmpty(body.NewPassword) || body.NewPassword.Length < PasswordHasher.MinimumLength)
        {
            return ValidationProblem($"Password must be at least {PasswordHasher.MinimumLength} characters.");
        }

        if (string.IsNullOrEmpty(body.CurrentPassword))
        {
            return ValidationProblem("The current password is required.");
        }

        if (string.Equals(body.CurrentPassword, body.NewPassword, StringComparison.Ordinal))
        {
            return ValidationProblem("The new password must be different from the current one.");
        }

        string userId = context.User.UserId() ?? string.Empty;
        User? user = await users.GetUserById(userId, cancellationToken);
        if (user is null || !user.Active)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not signed in", detail: "Sign in again.");
        }

        string throttleKey = "pwchange:" + userId;
        if (throttle.IsBlocked(throttleKey))
        {
            if (throttle.TryReportTrip(throttleKey))
            {
                await events.Record(context, SecurityEventTypes.SignInThrottled, detail: "too many wrong current passwords while changing a password");
            }

            return TooManyAttempts();
        }

        // Charged before the Argon2 verify, refunded on success: the same shape as Login (security review F4).
        long ticket = throttle.RecordFailure(throttleKey);
        if (!PasswordHasher.Verify(body.CurrentPassword, user.PasswordHash))
        {
            await events.Record(context, SecurityEventTypes.PasswordChangeFailed, target: user, detail: "wrong current password");
            return ValidationProblem("The current password is incorrect.");
        }

        throttle.Refund(throttleKey, ticket);
        await users.UpdateUserPassword(userId, PasswordHasher.Hash(body.NewPassword), cancellationToken);
        // A reset link made before this change must not be able to undo it.
        await resets.InvalidateLive(userId, clock.GetUtcNow(), cancellationToken);
        string? keepSessionId = context.User.FindFirst(PrincipalFactory.SessionIdClaim)?.Value;
        int revoked = keepSessionId is null
            ? await users.RevokeSessionsForUser(userId, cancellationToken)
            : await users.RevokeOtherSessionsForUser(userId, keepSessionId, cancellationToken);
        await events.Record(context, SecurityEventTypes.PasswordChanged, target: user, detail: $"{revoked} other session(s) revoked");
        return Results.NoContent();
    }

    /// <summary>
    /// Sets the session cookie, updates <see cref="HttpContext.User"/> to the identity that now exists, and reissues
    /// the antiforgery pair for it - all before returning, since a minimal API result commits the response as soon as
    /// it runs and nothing after that point can still add a Set-Cookie header.
    /// </summary>
    private static async Task StartSession(User user, HttpContext context, IUserStore users, IAntiforgery antiforgery, CancellationToken cancellationToken)
    {
        string secret = TokenGenerator.NewSecret();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now + SessionCookie.Lifetime;
        Session session = await users.CreateSession(user.Id, TokenGenerator.Hash(secret), now, expiresAt, cancellationToken);
        context.Response.Cookies.Append(SessionCookie.NameFor(context.Request), secret, SessionCookie.Options(context.Request, expiresAt));
        context.User = PrincipalFactory.ForUser(user, AuthSchemes.Cookie, sessionId: session.Id);
        CsrfMiddleware.IssueTokenCookie(context, antiforgery);
    }

    private static bool TryValidateNewAccount(string? email, string? displayName, string? password, out IResult? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
        {
            problem = ValidationProblem("A valid email address is required.");
            return false;
        }

        if (email.Length > UserEndpoints.MaxEmailLength)
        {
            problem = ValidationProblem($"An email address may be at most {UserEndpoints.MaxEmailLength} characters.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            problem = ValidationProblem("A display name is required.");
            return false;
        }

        if (displayName.Length > UserEndpoints.MaxDisplayNameLength)
        {
            problem = ValidationProblem($"A display name may be at most {UserEndpoints.MaxDisplayNameLength} characters.");
            return false;
        }

        if (string.IsNullOrEmpty(password) || password.Length < PasswordHasher.MinimumLength)
        {
            problem = ValidationProblem($"Password must be at least {PasswordHasher.MinimumLength} characters.");
            return false;
        }

        return true;
    }

    private static IResult MalformedBody()
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be a JSON object.");
    }

    private static IResult ValidationProblem(string detail)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: detail);
    }

    private static IResult InvalidCredentials()
    {
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed", detail: "Email or password is incorrect.");
    }

    private static IResult AlreadyCompleted()
    {
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "First run already completed", detail: "An administrator already exists; sign in instead.");
    }

    private static IResult InvalidSetupCode()
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Invalid setup code", detail: "The setup code is incorrect or expired.");
    }

    private static IResult TooManyAttempts()
    {
        return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many attempts", detail: $"Too many failed attempts from this account or network address; try again in {FailedSignInThrottle.Window.TotalMinutes:0} minutes.");
    }
}
