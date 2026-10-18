using System.Security.Claims;
using System.Text;
using Draughtsman.Core.Auth;

namespace Draughtsman.Server.Auth;

/// <summary>
/// Records security events (AU-04): a row in <c>security_events</c> for the admin read API and a structured log line
/// under <c>Draughtsman.Server.Security</c> for whoever ships the log. Never given a secret to record, and it cleans
/// the free text it is given (control characters out, length capped) so a hostile token name or attempted email
/// cannot forge a log line. A failure to write the row is logged and swallowed: losing an audit row is bad, but
/// refusing a sign-in or a password change because the log table is unwritable would be worse.
/// </summary>
public sealed class SecurityEventLog(ISecurityEventStore store, ILoggerFactory loggerFactory, TimeProvider clock)
{
    private const int MaxDetail = 512;
    private const int MaxLabel = 254;

    private readonly ILogger _logger = loggerFactory.CreateLogger("Draughtsman.Server.Security");

    /// <summary>
    /// Records an event for the current request. The actor defaults to whoever <paramref name="context"/> is
    /// authenticated as (and <see cref="SecurityEvent.Method"/> to how), <paramref name="actor"/> overrides it for the
    /// sign-in paths where the context has not been updated yet. The source IP is the connection's, after forwarded
    /// headers from <c>server.knownProxies</c> have been applied.
    /// </summary>
    public Task Record(HttpContext context, string type, User? target = null, string? detail = null, User? actor = null, string? method = null, string? targetLabel = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? actorId;
        string? actorLabel;
        if (actor is not null)
        {
            actorId = actor.Id;
            actorLabel = actor.Email;
        }
        else
        {
            actorId = context.User.UserId();
            actorLabel = context.User.FindFirstValue(ClaimTypes.Email);
        }

        method ??= actorId is null ? SecurityEventMethods.Anonymous : MethodOf(context.User);
        string? ip = context.Connection.RemoteIpAddress?.ToString();
        return Append(new SecurityEvent(type, clock.GetUtcNow(), actorId, Clean(actorLabel, MaxLabel), target?.Id, Clean(target?.Email ?? targetLabel, MaxLabel), ip, method, Clean(detail, MaxDetail)));
    }

    /// <summary>For callers with no HTTP request (the host-shell recovery).</summary>
    public Task Record(SecurityEvent securityEvent)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        return Append(securityEvent with
        {
            ActorLabel = Clean(securityEvent.ActorLabel, MaxLabel),
            TargetLabel = Clean(securityEvent.TargetLabel, MaxLabel),
            Detail = Clean(securityEvent.Detail, MaxDetail),
        });
    }

    /// <summary>Control characters become spaces and the text is capped, so it cannot break a log line or a table cell.</summary>
    public static string? Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, max));
        foreach (char c in value)
        {
            if (builder.Length >= max)
            {
                break;
            }

            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        string cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string MethodOf(ClaimsPrincipal user)
    {
        return AuthPolicies.IsCookieAuthenticated(user) ? SecurityEventMethods.Session : SecurityEventMethods.Token;
    }

    private async Task Append(SecurityEvent securityEvent)
    {
        LogLevel level = securityEvent.Type is SecurityEventTypes.SignInFailed or SecurityEventTypes.SignInThrottled or SecurityEventTypes.FirstRunFailed or SecurityEventTypes.PasswordChangeFailed or SecurityEventTypes.AiEndpointRefused
            ? LogLevel.Warning
            : LogLevel.Information;
        _logger.Log(
            level,
            "Security event {Type}: actor {ActorUserId} target {TargetUserId} from {SourceIp} via {Method}. {Detail}",
            securityEvent.Type,
            securityEvent.ActorUserId ?? "-",
            securityEvent.TargetUserId ?? securityEvent.TargetLabel ?? "-",
            securityEvent.SourceIp ?? "-",
            securityEvent.Method ?? "-",
            securityEvent.Detail ?? string.Empty);
        try
        {
            await store.Append(securityEvent);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogError(error, "Could not record security event {Type}", securityEvent.Type);
        }
    }
}
