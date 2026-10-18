namespace Draughtsman.Core.Auth;

/// <summary>
/// One instance-level authentication or administration event (AU-04): a sign-in, a password change, a role change, a
/// token minted or revoked. Unlike the document <c>audit</c> table it is not tied to a document. Never holds a secret:
/// no password, no token, no session value, no hash. <see cref="ActorUserId"/> is whoever did it (null when nobody was
/// authenticated, e.g. a failed sign-in), <see cref="TargetUserId"/> is whose account it happened to, and the two
/// labels are the emails as they were at the time, so a row stays readable on its own. <see cref="Method"/> says how
/// the caller was authenticated (<see cref="SecurityEventMethods"/>); it is a record of the credential, never what
/// authorises anything. <see cref="Detail"/> is short human text (for example "Editor to Admin").
/// </summary>
public sealed record SecurityEvent(
    string Type,
    DateTimeOffset At,
    string? ActorUserId = null,
    string? ActorLabel = null,
    string? TargetUserId = null,
    string? TargetLabel = null,
    string? SourceIp = null,
    string? Method = null,
    string? Detail = null)
{
    /// <summary>Assigned by the store; 0 until appended.</summary>
    public long Id { get; init; }
}

/// <summary>The event vocabulary. Strings, not an enum, so a later feature can add one without a migration.</summary>
public static class SecurityEventTypes
{
    public const string SignInSucceeded = "sign_in.succeeded";

    public const string SignInFailed = "sign_in.failed";

    /// <summary>A sign-in or first-run request refused by the failed-attempt throttle. Recorded once per throttle window per key, not per refused request.</summary>
    public const string SignInThrottled = "sign_in.throttled";

    public const string SignedOut = "sign_out";

    public const string FirstRunCompleted = "first_run.completed";

    public const string FirstRunFailed = "first_run.failed";

    public const string UserCreated = "user.created";

    public const string UserRoleChanged = "user.role_changed";

    public const string UserDeactivated = "user.deactivated";

    public const string UserReactivated = "user.reactivated";

    /// <summary>An admin set another account's password.</summary>
    public const string PasswordReset = "password.reset";

    /// <summary>A user changed their own password.</summary>
    public const string PasswordChanged = "password.changed";

    /// <summary>A user's attempt to change their own password was refused (wrong current password, too short, throttled).</summary>
    public const string PasswordChangeFailed = "password.change_failed";

    /// <summary>Someone asked, on the sign-in page, for a reset link to be emailed to an address. Recorded the same whether or not an account has that address.</summary>
    public const string PasswordResetRequested = "password_reset.requested";

    /// <summary>An administrator created a reset link for another person. Never holds the link.</summary>
    public const string PasswordResetLinkIssued = "password_reset.link_issued";

    /// <summary>A reset link was handed to the mail server for the account's address.</summary>
    public const string PasswordResetEmailSent = "password_reset.email_sent";

    /// <summary>A reset email could not be sent; the link made for it was ended.</summary>
    public const string PasswordResetEmailFailed = "password_reset.email_failed";

    /// <summary>A reset link was spent.</summary>
    public const string PasswordResetLinkUsed = "password_reset.link_used";

    /// <summary>A reset link was refused. The detail is a reason class (not recognised, expired, already used, ended, account deactivated), never the link.</summary>
    public const string PasswordResetLinkRefused = "password_reset.link_refused";

    /// <summary>A password was changed by spending a reset link.</summary>
    public const string PasswordResetCompleted = "password_reset.completed";

    /// <summary>An administrator sent themselves a test email from the Email page.</summary>
    public const string EmailTestSent = "email.test_sent";

    /// <summary>The test email could not be sent.</summary>
    public const string EmailTestFailed = "email.test_failed";

    /// <summary>An administrator saved the outgoing email settings. The detail names what changed ("mail server, password replaced"), never a value.</summary>
    public const string EmailSettingsChanged = "email.settings_changed";

    /// <summary>An administrator removed the saved outgoing email settings, back to draughtsman.yaml alone.</summary>
    public const string EmailSettingsReset = "email.settings_reset";

    /// <summary>An administrator changed the host (or port) the AI endpoint points at, directly or by switching provider or resetting the AI settings. The detail names the host before and after and whether the stored key was forgotten because of it.</summary>
    public const string AiEndpointHostChanged = "ai.endpoint_host_changed";

    /// <summary>An administrator tried to point the AI endpoint at a host the endpoint policy refuses. The detail names the host that was refused and why in a fixed phrase; nothing was changed.</summary>
    public const string AiEndpointRefused = "ai.endpoint_refused";

    /// <summary>An administrator asked the server to fetch a web page to read its colours into a draft theme (Themes, From a website). The detail names the host and the outcome (ok, or why it was refused); never the path, the query or any of the page.</summary>
    public const string ThemeUrlFetch = "theme.url_fetch";

    public const string TokenCreated = "token.created";

    public const string TokenRevoked = "token.revoked";

    /// <summary>The host-shell <c>reset-admin</c> recovery ran.</summary>
    public const string AdminRecovered = "admin.recovered";

    /// <summary>An administrator downloaded the whole-instance export (every document and asset).</summary>
    public const string DataExported = "data.exported";

    /// <summary>A document moved to the bin (recoverable until the retention period ends).</summary>
    public const string DocumentDeleted = "document.deleted";

    /// <summary>A document taken out of the bin.</summary>
    public const string DocumentRestored = "document.restored";

    /// <summary>A binned document removed permanently: by a person's explicit second step, or by the retention pass.</summary>
    public const string DocumentPurged = "document.purged";
}

/// <summary>How the caller behind an event was authenticated. Descriptive only; never authorises anything.</summary>
public static class SecurityEventMethods
{
    public const string Session = "session";

    /// <summary>A sign-in or first-run request: the credential presented was a password (or setup code).</summary>
    public const string Password = "password";

    public const string Token = "token";

    /// <summary>The host shell (<c>reset-admin</c>): trusted by having the data folder, not by a credential.</summary>
    public const string Host = "host";

    /// <summary>A password-reset link was the credential presented (the reset page, before any session exists).</summary>
    public const string ResetLink = "reset_link";

    /// <summary>Nobody was authenticated (a failed or first sign-in).</summary>
    public const string Anonymous = "anonymous";
}

/// <summary>Filters for <see cref="ISecurityEventStore.List"/>. Every member is optional and they combine with AND.</summary>
public sealed record SecurityEventQuery(
    IReadOnlyList<string>? Types = null,
    string? UserId = null,
    string? SourceIp = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    int Offset = 0,
    int Limit = 50);

public sealed record SecurityEventPage(IReadOnlyList<SecurityEvent> Items, int Total);

/// <summary>
/// Persistence for <see cref="SecurityEvent"/> rows, a sibling to <see cref="IUserStore"/> and exercised unchanged
/// by the same provider contract tests. Append-only: nothing here edits or deletes a row.
/// </summary>
public interface ISecurityEventStore
{
    Task<SecurityEvent> Append(SecurityEvent securityEvent, CancellationToken cancellationToken = default);

    /// <summary>Newest first (time, then id). <see cref="SecurityEventQuery.UserId"/> matches the actor or the target.</summary>
    Task<SecurityEventPage> List(SecurityEventQuery query, CancellationToken cancellationToken = default);
}
