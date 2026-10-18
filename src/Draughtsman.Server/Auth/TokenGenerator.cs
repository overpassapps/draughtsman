using System.Security.Cryptography;
using System.Text;

namespace Draughtsman.Server.Auth;

/// <summary>
/// Opaque secrets for session cookies and API tokens. Only the SHA-256 hash of a secret is ever persisted
/// (<see cref="Hash"/>); the raw value lives in the cookie or is shown to the caller once, at creation, and nowhere
/// else - never logged, never in a response after the moment it is issued.
/// </summary>
public static class TokenGenerator
{
    private const int SecretBytes = 32;

    /// <summary>A URL-safe random secret with no padding, suitable for a cookie value or a bearer token.</summary>
    public static string NewSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>An API token's raw form: a recognisable prefix over the opaque secret, so a leaked value is greppable.</summary>
    public static string NewApiTokenSecret()
    {
        return "dst_" + NewSecret();
    }

    /// <summary>Lower-case hex SHA-256, the form stored at rest and looked up on every authenticated request.</summary>
    public static string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
