using Draughtsman.Core.Auth;

namespace Draughtsman.Server.Auth.Endpoints;

/// <summary>A user as the API shows it. Never the password hash.</summary>
public sealed record UserResponse(string Id, string Email, string DisplayName, string Role, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt, bool Active)
{
    public static UserResponse From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new UserResponse(user.Id, user.Email, user.DisplayName, user.Role.ToString(), user.CreatedAt, user.LastSignInAt, user.Active);
    }
}

/// <summary>Body of GET /api/auth/status. The SPA uses this on load to choose first-run, login, or the app shell.</summary>
public sealed record AuthStatusResponse(bool FirstRunRequired, UserResponse? User, FirstRunHintsResponse? FirstRun = null, PasswordResetHintsResponse? PasswordReset = null);

/// <summary>
/// What the sign-in page needs to know about password recovery: whether "Forgot password?" is offered (emailed links are set up and
/// on). When it is not, the page says to ask an administrator for a reset link. Nothing else about the mail configuration is
/// given to an anonymous caller.
/// </summary>
public sealed record PasswordResetHintsResponse(bool SelfService);

/// <summary>
/// What the first-run page tells the person standing at an unconfigured instance (PR-02): the data folder, and which kind
/// of host this looks like so the matching "where is the setup code" hint opens. Sent only while no user exists; never the
/// code itself. <c>Host</c> is console | container | systemd | launchd | windowsService.
/// </summary>
public sealed record FirstRunHintsResponse(string DataFolder, string Host);

// Every constructor parameter here needs an explicit default (= null), not just a nullable type: the shared
// System.Text.Json options (DocumentJson.Configure) set RespectRequiredConstructorParameters, which treats every
// record-primary-constructor parameter as required for deserialisation unless it has one - nullability alone does
// not make a field optional to bind. Without the default, an omitted field fails model binding before the handler
// ever runs, surfacing as a generic framework 400 instead of this file's own, specific validation messages.
public sealed record FirstRunRequest(string? Email = null, string? DisplayName = null, string? Password = null, string? SetupCode = null);

public sealed record LoginRequest(string? Email = null, string? Password = null);

public sealed record CreateUserRequest(string? Email = null, string? DisplayName = null, string? Role = null, string? Password = null);

/// <summary>Every field optional: PATCH applies only the ones present.</summary>
public sealed record UpdateUserRequest(string? Role = null, string? Password = null, bool? Active = null);

/// <summary>
/// An API token as the API lists it. Never the secret or its hash. <c>expiresAt</c> is omitted for a token that
/// pre-dates expiry (grandfathered: it never expires until revoked); <c>scope</c> is <c>full</c> or <c>read</c>.
/// </summary>
public sealed record ApiTokenResponse(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked, DateTimeOffset? ExpiresAt, bool Expired, string Scope)
{
    public static ApiTokenResponse From(ApiToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        bool expired = token.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= DateTimeOffset.UtcNow;
        return new ApiTokenResponse(token.Id, token.Name, token.CreatedAt, token.LastUsedAt, token.Revoked, token.ExpiresAt, expired, ScopeName(token.Scope));
    }

    public static string ScopeName(ApiTokenScope scope)
    {
        return scope == ApiTokenScope.Read ? "read" : "full";
    }
}

/// <summary>One row of GET /api/admin/tokens: a token and who owns it. The secret and its hash are never part of it.</summary>
public sealed record AdminApiTokenResponse(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked, DateTimeOffset? ExpiresAt, bool Expired, string Scope, string OwnerId, string? OwnerEmail, string? OwnerName)
{
    public static AdminApiTokenResponse From(ApiToken token, User? owner)
    {
        ApiTokenResponse own = ApiTokenResponse.From(token);
        return new AdminApiTokenResponse(own.Id, own.Name, own.CreatedAt, own.LastUsedAt, own.Revoked, own.ExpiresAt, own.Expired, own.Scope, token.UserId, owner?.Email, owner?.DisplayName);
    }
}

/// <summary>Body of POST /api/tokens: the only time the raw secret is ever sent.</summary>
public sealed record ApiTokenCreatedResponse(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Scope, string Token);

/// <summary><c>expiresInDays</c> defaults to <c>auth.tokens.defaultLifetimeDays</c> and may not exceed <c>auth.tokens.maxLifetimeDays</c>; <c>scope</c> defaults to <c>full</c>.</summary>
public sealed record CreateApiTokenRequest(string? Name = null, int? ExpiresInDays = null, string? Scope = null);

/// <summary>Body of POST /api/auth/password: a signed-in user changing their own password.</summary>
public sealed record ChangePasswordRequest(string? CurrentPassword = null, string? NewPassword = null);

/// <summary>One row of GET /api/admin/security-events.</summary>
public sealed record SecurityEventResponse(long Id, DateTimeOffset At, string Type, string? ActorUserId, string? ActorLabel, string? TargetUserId, string? TargetLabel, string? SourceIp, string? Method, string? Detail)
{
    public static SecurityEventResponse From(SecurityEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new SecurityEventResponse(e.Id, e.At, e.Type, e.ActorUserId, e.ActorLabel, e.TargetUserId, e.TargetLabel, e.SourceIp, e.Method, e.Detail);
    }
}
