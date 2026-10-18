using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Draughtsman.Server.Auth;

/// <summary>
/// Argon2id password hashing (build-spec-v0.1.md section 7). Parameters are the OWASP Password Storage Cheat Sheet's
/// Argon2id baseline: 19 MiB memory, 2 iterations, 1 degree of parallelism, a 16-byte salt and a 32-byte derived key.
/// The encoded form is self-describing (<c>argon2id$m=&lt;kib&gt;,t=&lt;iterations&gt;,p=&lt;parallelism&gt;$&lt;salt&gt;$&lt;hash&gt;</c>) so a future
/// parameter change can still verify a hash written under the old ones. Comparison is constant-time.
/// </summary>
public static class PasswordHasher
{
    /// <summary>The minimum a caller must enforce before calling <see cref="Hash"/> (build-spec-v0.1.md section 7).</summary>
    public const int MinimumLength = 12;

    private const string Prefix = "argon2id";
    private const int MemoryKiB = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltLength = 16;
    private const int HashLength = 32;

    /// <summary>Internal only so a test can prove Verify actually runs against this rather than being short-circuited away.</summary>
    internal const string DummyPassword = "dummy-password-for-constant-time-verify";

    /// <summary>
    /// Verified against when the real user is missing or inactive (security review F6), so Argon2id's expensive
    /// derivation always runs and a caller cannot distinguish "no such account" from "wrong password" by timing
    /// alone. Computed once, lazily, with the same cost parameters as a real hash - a fixed password and salt, since
    /// nothing is ever meant to match this on purpose.
    /// </summary>
    public static readonly Lazy<string> DummyHash = new(() => Hash(DummyPassword));

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] hash = Derive(password, salt, MemoryKiB, Iterations, Parallelism, HashLength);
        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}$m={MemoryKiB},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>False for a wrong password, a malformed encoded form, or an empty argument - never throws on bad input.</summary>
    public static bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        string[] parts = encoded.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryParseParams(parts[1], out int memoryKiB, out int iterations, out int parallelism))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = Derive(password, salt, memoryKiB, iterations, parallelism, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int hashLength)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            Iterations = iterations,
            MemorySize = memoryKiB,
        };
        return argon2.GetBytes(hashLength);
    }

    /// <summary>Parses "m=19456,t=2,p=1"; false for anything else, including out-of-range or missing fields.</summary>
    private static bool TryParseParams(string text, out int memoryKiB, out int iterations, out int parallelism)
    {
        memoryKiB = 0;
        iterations = 0;
        parallelism = 0;
        string[] fields = text.Split(',');
        if (fields.Length != 3)
        {
            return false;
        }

        return TryParseField(fields[0], "m=", out memoryKiB)
            && TryParseField(fields[1], "t=", out iterations)
            && TryParseField(fields[2], "p=", out parallelism)
            && memoryKiB > 0 && iterations > 0 && parallelism > 0;
    }

    private static bool TryParseField(string field, string prefix, out int value)
    {
        value = 0;
        return field.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(field.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
