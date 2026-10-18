using System.Security.Cryptography;
using System.Text;

namespace Draughtsman.Server.Auth;

/// <summary>
/// The one-time setup code first-run requires (security review F1.2, maintainer's decision 2026-09-29): printed to the
/// server log at startup so an operator with log access can complete first-run, rather than trusting "this request
/// came from the local machine" - a check Docker and reverse-proxy deployments would let any caller sail through.
/// Lives only in memory: a restart before first-run completes prints a fresh code and invalidates the old one; once
/// any admin exists this gate is moot forever anyway, since a second first-run is impossible regardless
/// (<see cref="Draughtsman.Core.Auth.IUserStore.TryCreateFirstUser"/>).
/// </summary>
public sealed class SetupCodeGate(SetupCodeFile? file = null)
{
    // 31 symbols (no 0/O/1/I/L). 256 is not a multiple of 31, so `byte % 31` favours the first eight symbols very slightly (9/256 against 8/256):
    // about 49.5 bits of entropy rather than 50, which five guesses per address per window make irrelevant.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int Length = 10;

    private string? _code;

    /// <summary>Generates and stores a fresh code, replacing any previous one, and returns it for logging.</summary>
    public string Issue()
    {
        string code = Generate();
        _code = code;
        return code;
    }

    /// <summary>
    /// Constant-time comparison against the current code; false once <see cref="Consume"/> has run or none was ever issued. Surrounding whitespace
    /// and letter case are ignored: the code is copied out of a terminal (a double-click often takes a trailing space) and the page enables its
    /// button on the trimmed value, so the server must agree with the page about what a right code looks like.
    /// </summary>
    public bool Verify(string? candidate)
    {
        string? current = _code;
        string typed = (candidate ?? string.Empty).Trim().ToUpperInvariant();
        if (current is null || typed.Length == 0)
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(current);
        byte[] actual = Encoding.UTF8.GetBytes(typed);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Single-use: called once first-run actually succeeds, so a captured code cannot be replayed. Also removes the Windows service's setup-code file (<see cref="SetupCodeFile"/>), so no copy of the code outlives the first administrator.</summary>
    public void Consume()
    {
        _code = null;
        file?.Delete();
    }

    private static string Generate()
    {
        Span<byte> bytes = stackalloc byte[Length];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[Length];
        for (int i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }
}
