using System.Security.Cryptography;
using System.Text;

namespace Draughtsman.Server.KeyRing;

/// <summary>
/// A key-encryption key (KEK) could not be used. The message says what is wrong with it (missing, too short, not
/// base64) and never contains the key or any part of it.
/// </summary>
internal sealed class KeyEncryptionKeyException(string message) : Exception(message);

/// <summary>
/// The operating system store the key ring is meant to be protected by (the macOS Keychain) cannot be used, so the
/// server must not start: carrying on would quietly leave a ring that should be protected as plain text. Unlike every
/// other protection failure this is not turned into <see cref="NoKeyRingProtection"/>; it stops startup, and the
/// message names the two safe ways forward. Never contains the key.
/// </summary>
internal sealed class KeyRingProtectionRequiredException(string message) : Exception(message);

/// <summary>
/// The KEK wrapped around each key in the Data Protection ring: parsing of the operator-supplied text, and the
/// authenticated encryption the ring's secrets go through. The KEK is 32 or more random bytes held outside the data
/// folder (an environment variable, a root-only file, or the macOS Keychain); a working AES-256-GCM key is derived
/// from it with HKDF so the raw KEK is never used for encryption directly, and a longer KEK is simply accepted.
/// </summary>
internal static class KeyEncryptionKey
{
    /// <summary>The shortest KEK accepted: 256 bits of entropy, so a pasted passphrase cannot stand in for a random key.</summary>
    public const int MinimumBytes = 32;

    private const string Purpose = "Draughtsman.KeyRing.KeyEncryptionKey.v1";

    private const int NonceSize = 12;

    private const int TagSize = 16;

    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes(Purpose);

    /// <summary>Generates a new random KEK, as base64 text ready to store.</summary>
    public static string Generate()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(MinimumBytes));
    }

    /// <summary>Reads a KEK from its text form (base64 or base64url of at least <see cref="MinimumBytes"/> random bytes; surrounding whitespace is ignored).</summary>
    public static byte[] Parse(string text)
    {
        string trimmed = text.Trim().Replace('-', '+').Replace('_', '/');
        if (trimmed.Length == 0)
        {
            throw new KeyEncryptionKeyException("The key-encryption key is empty.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(trimmed.PadRight(trimmed.Length + ((4 - (trimmed.Length % 4)) % 4), '='));
        }
        catch (FormatException)
        {
            throw new KeyEncryptionKeyException("The key-encryption key must be base64 text; generate one with: openssl rand -base64 32");
        }

        if (bytes.Length < MinimumBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new KeyEncryptionKeyException($"The key-encryption key is too short: it must decode to at least {MinimumBytes} random bytes; generate one with: openssl rand -base64 32");
        }

        return bytes;
    }

    /// <summary>Encrypts and authenticates <paramref name="plaintext"/>; the result is nonce, tag then ciphertext.</summary>
    public static byte[] Seal(byte[] kek, byte[] plaintext)
    {
        RequireGcm();
        byte[] key = Derive(kek);
        try
        {
            byte[] result = new byte[NonceSize + TagSize + plaintext.Length];
            Span<byte> nonce = result.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, result.AsSpan(NonceSize + TagSize), result.AsSpan(NonceSize, TagSize), AssociatedData);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Reverses <see cref="Seal"/>. Throws <see cref="CryptographicException"/> for a wrong key or altered data, never saying which.</summary>
    public static byte[] Open(byte[] kek, byte[] sealedData)
    {
        RequireGcm();
        if (sealedData.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The protected key is truncated.");
        }

        byte[] key = Derive(kek);
        try
        {
            byte[] plaintext = new byte[sealedData.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                sealedData.AsSpan(0, NonceSize),
                sealedData.AsSpan(NonceSize + TagSize),
                sealedData.AsSpan(NonceSize, TagSize),
                plaintext,
                AssociatedData);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Derive(byte[] kek)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, kek, 32, salt: null, info: Encoding.UTF8.GetBytes(Purpose));
    }

    private static void RequireGcm()
    {
        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("AES-GCM is not available on this platform, so a key-encryption key cannot be used.");
        }
    }
}
