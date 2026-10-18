using System.Text.RegularExpressions;

namespace Draughtsman.Server.Cli;

/// <summary>
/// Checks made before <c>service install</c> changes anything. On Windows the service starts as LocalService, which cannot
/// read a user's profile folder (<c>C:\Users\&lt;name&gt;</c> is closed to every other account), so an executable unpacked
/// there installs fine and then fails to start with the service manager's bare "Access is denied" (D6 of the Windows install
/// proof), leaving a created-but-stopped service behind. Say so first, in plain words, and say what to do instead.
/// </summary>
public static partial class ServicePreflight
{
    /// <summary>
    /// A message when <paramref name="executable"/> sits in a user profile folder that <paramref name="account"/> cannot read,
    /// otherwise null. Only the built-in service accounts are judged: a named account is the operator's decision (it may be the
    /// profile's own owner), and the file system, not a guess made here, will say if it cannot start.
    /// </summary>
    /// <param name="profilesRoot">The folder that holds the user profiles; null means the parent of this user's profile, else <c>C:\Users</c>.</param>
    public static string? ExecutableUnreadableByService(string executable, string account, string? profilesRoot = null)
    {
        if (!IsBuiltInServiceAccount(account))
        {
            return null;
        }

        string path = executable.Replace('\\', '/');
        string root = (profilesRoot ?? ProfilesRoot()).Replace('\\', '/').TrimEnd('/');
        if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // C:\Users\Public (and Default) are readable by every account; anything else is one person's profile.
        string[] parts = path[(root.Length + 1)..].Split('/');
        if (parts.Length < 2 || parts[0].Equals("Public", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{executable} is inside the user profile folder {root.Replace('/', '\\')}\\{parts[0]}, which the service account ({account}) cannot read, so the service would fail to start with \"Access is denied\". " +
            $"Unpack the archive to a folder outside {root.Replace('/', '\\')}, for example C:\\Program Files\\Draughtsman, and run 'draughtsman service install' from there (or pass --exe with that path). Nothing was changed.";
    }

    private static string ProfilesRoot()
    {
        string? profile = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("USERPROFILE") : null;
        string? parent = string.IsNullOrWhiteSpace(profile) ? null : Path.GetDirectoryName(profile.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(parent) ? "C:\\Users" : parent;
    }

    private static bool IsBuiltInServiceAccount(string account)
    {
        return BuiltInServiceAccount().IsMatch(account.Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^(NT AUTHORITY\\|NTAUTHORITY\\)?(LocalService|NetworkService|LocalSystem|System)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BuiltInServiceAccount();
}
