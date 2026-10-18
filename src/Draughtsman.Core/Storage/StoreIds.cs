using System.Globalization;
using System.Security.Cryptography;

namespace Draughtsman.Core.Storage;

/// <summary>
/// Generates the ids the store hands out when a caller has none. Lower-case hex after a type prefix keeps them inside
/// the document schema's id pattern and readable in a URL or a DSL line.
/// Version ids start with the creation instant in ticks, strictly increasing within a process, so ordering by id
/// is chronological and settles ties between versions stored in the same millisecond.
/// </summary>
public static class StoreIds
{
    public const string DocumentPrefix = "doc_";

    public const string VersionPrefix = "v_";

    public const string AssetPrefix = "asset_";

    public const string UserPrefix = "usr_";

    public const string ApiTokenPrefix = "tok_";

    public const string SessionPrefix = "sess_";

    public const string PasswordResetPrefix = "prl_";

    private static long s_lastVersionTicks;

    public static string NewDocumentId()
    {
        return DocumentPrefix + RandomHex(12);
    }

    public static string NewUserId()
    {
        return UserPrefix + RandomHex(12);
    }

    public static string NewApiTokenId()
    {
        return ApiTokenPrefix + RandomHex(12);
    }

    public static string NewSessionId()
    {
        return SessionPrefix + RandomHex(16);
    }

    public static string NewPasswordResetId()
    {
        return PasswordResetPrefix + RandomHex(16);
    }

    public static string NewVersionId()
    {
        return VersionPrefix + NextVersionTicks().ToString("x15", CultureInfo.InvariantCulture) + RandomHex(6);
    }

    public static string NewAssetId()
    {
        return AssetPrefix + RandomHex(12);
    }

    /// <summary>UTC ticks now, bumped past the last value handed out so two calls never share a prefix.</summary>
    private static long NextVersionTicks()
    {
        while (true)
        {
            long last = Volatile.Read(ref s_lastVersionTicks);
            long next = Math.Max(DateTime.UtcNow.Ticks, last + 1);
            if (Interlocked.CompareExchange(ref s_lastVersionTicks, next, last) == last)
            {
                return next;
            }
        }
    }

    private static string RandomHex(int length)
    {
        return RandomNumberGenerator.GetHexString(length, lowercase: true);
    }
}
