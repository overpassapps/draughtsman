using System.Globalization;

namespace Draughtsman.Server.Diagnostics;

/// <summary>
/// Whether this command may open the SQLite database without disturbing the files beside it. SQLite, when the account running it is root, gives a
/// write-ahead log (<c>data.db-wal</c>) and its shared-memory file (<c>data.db-shm</c>) the owner of <c>data.db</c> itself as it opens them. Normally that is
/// no change. But after a restore or copy done as root, <c>data.db</c> is root's while the two files the running server uses are still its own, and a
/// root-run command that merely READS the database then hands them to root too: the fault gets worse, and a check that promised to change nothing has
/// changed ownership. (Found by running <c>doctor</c> and <c>config check</c> as root in a container against a root-owned database.) In that one position the
/// database is not opened; the ownership finding already says what is wrong and how to put it right.
/// </summary>
public static class DatabaseGuard
{
    /// <summary>True when opening the database is safe. <paramref name="reason"/> says why not, in a sentence an operator can act on.</summary>
    public static bool SafeToOpen(string? databaseFile, long? currentUid, out string? reason)
    {
        return SafeToOpen(databaseFile, currentUid, File.Exists, path => FileOwners.Of(path)?.Uid, out reason);
    }

    /// <summary>The rule over what the files are and who owns them, so a test can state the position (a root-owned database beside the server's own log) without being root.</summary>
    internal static bool SafeToOpen(string? databaseFile, long? currentUid, Func<string, bool> exists, Func<string, long?> ownerOf, out string? reason)
    {
        reason = null;
        if (databaseFile is null || currentUid != 0 || OperatingSystem.IsWindows() || !exists(databaseFile))
        {
            return true;
        }

        long? databaseOwner = ownerOf(databaseFile);
        if (databaseOwner is null)
        {
            return true;
        }

        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            string sibling = databaseFile + suffix;
            if (exists(sibling) && ownerOf(sibling) is { } owner && owner != databaseOwner)
            {
                reason = $"The database was not opened: this command is running as root, {Path.GetFileName(databaseFile)} is owned by uid {databaseOwner.Value.ToString(CultureInfo.InvariantCulture)} but {Path.GetFileName(sibling)} by uid {owner.ToString(CultureInfo.InvariantCulture)}, and opening a database as root gives its -wal and -shm files to the database's owner, which would take them from the server. Put the ownership right first (see the ownership finding), or run this as the server's own account.";
                return false;
            }
        }

        return true;
    }
}
