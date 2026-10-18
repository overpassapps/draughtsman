using System.Net;

namespace Draughtsman.Ai;

/// <summary>
/// The messages a provider puts in a <see cref="ModelProviderException"/>. They can reach an HTTP response body, so
/// none may carry an API key: a provider that rejects a key often echoes it back masked ("Incorrect API key provided:
/// sk-...abcd"), which is still part of the key, so an authentication failure reports the status only and drops the
/// provider's body; any other body has the exact key redacted before it is kept.
/// </summary>
internal static class ProviderErrors
{
    public static string HttpFailure(string providerName, HttpStatusCode status, string? reason, string body, string? apiKey)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return $"{providerName} returned {(int)status} {reason}: the API key or credentials were rejected.";
        }

        return $"{providerName} returned {(int)status} {reason}: {Redact(body, apiKey)}";
    }

    public static string MissingKey(string providerName, string environmentVariable)
    {
        return $"No {providerName} API key is available: set the environment variable '{environmentVariable}' or save a key in the admin AI settings.";
    }

    public static string Redact(string text, string? secret)
    {
        return string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "[redacted]", StringComparison.Ordinal);
    }
}
