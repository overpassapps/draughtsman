using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Draughtsman.Server.Hardening;

/// <summary>One allow or deny entry of a folder's DACL, reduced to what the privacy check needs: who (a SID string), allow or deny, and the access mask.</summary>
public sealed record AclEntry(string Sid, bool Allow, int Rights);

/// <summary>
/// Windows's half of keeping the data folder private (D2 of the Windows install proof; the Unix half is the umask and modes in
/// <see cref="DataFolderPermissions"/>). The data folder holds every document, the password and token hashes, the key ring and
/// the stored AI key. Under <c>%ProgramData%</c> a new folder inherits read and create rights for <c>BUILTIN\Users</c>, so any
/// local account could read the database and plant a <c>draughtsman.yaml</c>. <c>service install</c> fixes the folder it creates
/// (<c>ServicePlanner.WindowsDataFolderAclArguments</c>); this class does the same for a folder the server creates itself
/// (<see cref="CreateProtected"/>, only when it is new, as on Unix) and tells the operator when an existing one is open
/// (<see cref="Describe"/>), without changing it: a shared folder may be deliberate. The checks are pure over
/// <see cref="AclEntry"/> so they run on any host; only reading and creating touch Windows.
/// </summary>
public static class WindowsDataFolderAcl
{
    public const string SidSystem = "S-1-5-18";
    public const string SidAdministrators = "S-1-5-32-544";

    /// <summary>Accounts that mean "other people on this machine": Everyone, Users, Authenticated Users, Guests, Network and Interactive logons.</summary>
    public static readonly IReadOnlyDictionary<string, string> BroadAccounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["S-1-1-0"] = "Everyone",
        ["S-1-5-32-545"] = "BUILTIN\\Users",
        ["S-1-5-11"] = "Authenticated Users",
        ["S-1-5-32-546"] = "BUILTIN\\Guests",
        ["S-1-5-2"] = "NETWORK",
        ["S-1-5-4"] = "INTERACTIVE",
    };

    // Reading data or the folder's listing, writing or creating, extended attributes, execute/traverse, delete and changing permissions or owner:
    // anything but the three rights an entry may keep without exposing content (read attributes, read permissions, synchronise).
    private const int ContentOrChangeRights = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x20000000 | 0x40000000 | unchecked((int)0x80000000);

    /// <summary>
    /// The DACL for a data folder the server creates: protected (<c>P</c>, nothing inherited from the parent), full control
    /// for SYSTEM, the Administrators and the account creating it, inherited by everything inside.
    /// </summary>
    public static string NewFolderSddl(string creatorSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(creatorSid);
        return $"D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{creatorSid})";
    }

    /// <summary>A note per account in <paramref name="entries"/> that is a broad one with an allow entry giving it access to the folder's content; empty when only SYSTEM, Administrators and named accounts have any.</summary>
    public static IReadOnlyList<string> Describe(string folder, IEnumerable<AclEntry> entries, string account)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string[] open = [.. entries
            .Where(e => e.Allow && (e.Rights & ContentOrChangeRights) != 0 && BroadAccounts.ContainsKey(e.Sid))
            .Select(e => BroadAccounts[e.Sid])
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (open.Length == 0)
        {
            return [];
        }

        return
        [
            $"The data folder {folder} is open to other users on this machine ({string.Join(", ", open)} can read or change it). It holds the database with every password hash, the key ring and draughtsman.yaml, " +
            $"so any local account could read it or plant a settings file. Make it private to SYSTEM, the Administrators and the service account: " +
            $"icacls \"{folder}\" /inheritance:r /remove:g *S-1-5-32-545 *S-1-1-0 *S-1-5-11 /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F \"{account}:(OI)(CI)F\" (draughtsman service install does exactly this).",
        ];
    }

    /// <summary>Creates <paramref name="folder"/> with <see cref="NewFolderSddl"/>; its parents are created normally first.</summary>
    [SupportedOSPlatform("windows")]
    public static void CreateProtected(string folder)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('\\', '/'));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string sid = WindowsIdentity.GetCurrent().User?.Value ?? SidAdministrators;
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(NewFolderSddl(sid));
        new DirectoryInfo(folder).Create(security);
    }

    /// <summary>The folder's DACL as <see cref="AclEntry"/> values (explicit and inherited entries alike).</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<AclEntry> Read(string folder)
    {
        DirectorySecurity security = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access);
        var entries = new List<AclEntry>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            entries.Add(new AclEntry(rule.IdentityReference.Value, rule.AccessControlType == AccessControlType.Allow, (int)rule.FileSystemRights));
        }

        return entries;
    }

    /// <summary>The account a note should name in its fix command: the current user.</summary>
    public static string CurrentAccount() => $"{Environment.UserDomainName}\\{Environment.UserName}";
}
