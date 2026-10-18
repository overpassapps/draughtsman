using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Draughtsman.Server.KeyRing;

internal enum SecretReading
{
    /// <summary>Opened under the current application name: how every secret written by this build is stored.</summary>
    Current,

    /// <summary>Opened only under an install path an earlier build bound it to: readable, and due to be re-protected.</summary>
    Legacy,

    /// <summary>Not opened by the current name or by any legacy candidate.</summary>
    Unreadable,
}

/// <summary>Why a payload could not be opened, as far as can be told from the payload and the ring; free of any secret.</summary>
internal enum UnreadableReason
{
    /// <summary>The text is not a Data Protection payload at all (cut short, or edited).</summary>
    Malformed,

    /// <summary>The key that protected it is not in the ring, or the ring's protection cannot open it: a restored or replaced ring, a lost key-encryption key.</summary>
    KeyUnavailable,

    /// <summary>The ring has the key, but no path this build knows opens it: it was written under an install path nobody told this build about (or it was damaged).</summary>
    PathNotKnown,

    /// <summary>The ring has the key and no legacy reader was available to try other paths.</summary>
    Unknown,
}

/// <param name="Reading">How the payload opened.</param>
/// <param name="Plaintext">The secret; held only as long as the caller needs it. Null when unreadable.</param>
/// <param name="LegacySource">For <see cref="SecretReading.Legacy"/>: which candidate opened it.</param>
/// <param name="Why">For <see cref="SecretReading.Unreadable"/>: the diagnosis.</param>
internal sealed record SecretOutcome(SecretReading Reading, string? Plaintext, LegacyCandidate? LegacySource, UnreadableReason? Why)
{
    public override string ToString()
    {
        return $"{Reading} (secret redacted)";
    }
}

/// <summary>
/// One purpose's protect and unprotect over the host's Data Protection provider (which is bound to
/// <see cref="DataProtectionIdentity.ApplicationName"/>), with a read-only fallback to the install paths an earlier build bound its
/// secrets to. <see cref="Protect"/> only ever writes under the current name.
/// </summary>
internal sealed class SecretProtector(IDataProtectionProvider provider, string purpose, LegacySecretReader? legacy)
{
    private readonly IDataProtector _protector = provider.CreateProtector(purpose);

    public LegacySecretReader? Legacy => legacy;

    public string Protect(string plaintext)
    {
        return _protector.Protect(plaintext);
    }

    /// <param name="payload">The stored text.</param>
    /// <param name="allowLegacy">False asks "is this stored the way this build stores it", which is how re-protection checks its own write.</param>
    public SecretOutcome Unprotect(string payload, bool allowLegacy = true)
    {
        try
        {
            return new SecretOutcome(SecretReading.Current, _protector.Unprotect(payload), null, null);
        }
        catch (CryptographicException)
        {
            // Not stored under the current name; fall through to the install paths an earlier build used.
        }

        if (allowLegacy && legacy is not null && legacy.TryUnprotect(purpose, payload) is { } reading)
        {
            return new SecretOutcome(SecretReading.Legacy, reading.Plaintext, reading.Candidate, null);
        }

        return new SecretOutcome(SecretReading.Unreadable, null, null, allowLegacy ? Diagnose(payload) : UnreadableReason.Unknown);
    }

    private UnreadableReason Diagnose(string payload)
    {
        if (!TryKeyId(payload, out Guid keyId))
        {
            return UnreadableReason.Malformed;
        }

        if (legacy is null)
        {
            return UnreadableReason.Unknown;
        }

        return legacy.ReadableKeys.Contains(keyId) ? UnreadableReason.PathNotKnown : UnreadableReason.KeyUnavailable;
    }

    /// <summary>Data Protection's payload header: a four-byte magic (09 F0 C9 F0) then the id of the key that protected it.</summary>
    private static bool TryKeyId(string payload, out Guid keyId)
    {
        keyId = Guid.Empty;
        try
        {
            byte[] bytes = WebEncoders.Base64UrlDecode(payload);
            if (bytes.Length < 20 || bytes[0] != 0x09 || bytes[1] != 0xF0 || bytes[2] != 0xC9 || bytes[3] != 0xF0)
            {
                return false;
            }

            keyId = new Guid(bytes.AsSpan(4, 16));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
