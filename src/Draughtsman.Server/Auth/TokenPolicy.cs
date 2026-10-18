using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Auth;

/// <summary>
/// The lifetime rules for new API tokens (AU-05), resolved from <c>auth.tokens</c> in draughtsman.yaml. The operator
/// sets a ceiling (<see cref="MaxLifetimeDays"/>) and a default; a caller may ask for any whole number of days up to
/// the ceiling and is refused beyond it. A nonsense configured value (zero, negative, absurd) is clamped rather than
/// honoured, so a typo can neither disable expiry nor mint a token that is already dead.
/// </summary>
public sealed class TokenPolicy(TokenPolicyConfig config)
{
    private const int AbsoluteMaxDays = 3650;

    public int MaxLifetimeDays { get; } = Math.Clamp(config.MaxLifetimeDays, 1, AbsoluteMaxDays);

    public int DefaultLifetimeDays { get; } = Math.Clamp(config.DefaultLifetimeDays, 1, Math.Clamp(config.MaxLifetimeDays, 1, AbsoluteMaxDays));

    /// <summary>Null <paramref name="requestedDays"/> means the default. False, with a message for the caller, when it is under 1 or over the ceiling.</summary>
    public bool TryResolveExpiry(int? requestedDays, DateTimeOffset now, out DateTimeOffset expiresAt, out string? problem)
    {
        int days = requestedDays ?? DefaultLifetimeDays;
        if (days < 1 || days > MaxLifetimeDays)
        {
            expiresAt = default;
            problem = $"A token must last between 1 and {MaxLifetimeDays} days.";
            return false;
        }

        expiresAt = now.AddDays(days);
        problem = null;
        return true;
    }
}
