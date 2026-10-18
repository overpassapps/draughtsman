using Draughtsman.Server.Hardening;

namespace Draughtsman.Server.Tests;

/// <summary>
/// D2 of the Windows install proof: the data folder must not be open to other local users, and the server says so when it is.
/// The checks are pure over <see cref="AclEntry"/> so they run here; the real DACL read, the protected creation and the
/// <c>icacls</c> result are proved on the Windows VM.
/// </summary>
public class WindowsDataFolderAclTests
{
    private const int ReadAndExecute = 0x1200A9; // what C:\ProgramData gives BUILTIN\Users: read data, EA, execute, attributes, permissions, synchronise
    private const int Full = 0x1F01FF;
    private const int ReadAttributesPermissionsSynchronise = 0x80 | 0x20000 | 0x100000;

    private const string Folder = "C:\\ProgramData\\Draughtsman";

    private static IReadOnlyList<string> Notes(params AclEntry[] entries) => WindowsDataFolderAcl.Describe(Folder, entries, "NT AUTHORITY\\LOCAL SERVICE");

    [Fact]
    public void The_ProgramData_default_that_the_proof_found_is_reported_naming_Users_and_the_fix()
    {
        IReadOnlyList<string> notes = Notes(
            new AclEntry("S-1-5-18", true, Full), new AclEntry("S-1-5-32-544", true, Full), new AclEntry("S-1-5-19", true, Full),
            new AclEntry("S-1-5-32-545", true, ReadAndExecute), new AclEntry("S-1-5-32-545", true, 0x2 | 0x4 | 0x10 | 0x100 | 0x100000));

        string note = Assert.Single(notes);
        Assert.Contains("BUILTIN\\Users", note);
        Assert.Contains(Folder, note);
        Assert.Contains("/inheritance:r", note);
        Assert.Contains("service install", note);
    }

    [Theory]
    [InlineData("S-1-1-0", "Everyone")]
    [InlineData("S-1-5-11", "Authenticated Users")]
    [InlineData("S-1-5-32-546", "Guests")]
    public void Every_broad_account_with_any_access_is_named(string sid, string name)
    {
        Assert.Contains(name, Assert.Single(Notes(new AclEntry(sid, true, ReadAndExecute))));
    }

    [Fact]
    public void A_private_folder_says_nothing()
    {
        Assert.Empty(Notes(
            new AclEntry("S-1-5-18", true, Full), new AclEntry("S-1-5-32-544", true, Full),
            new AclEntry("S-1-5-19", true, Full), new AclEntry("S-1-5-21-111-222-333-1001", true, Full)));
    }

    [Fact]
    public void A_deny_entry_and_an_entry_that_only_reads_attributes_do_not_count_as_access()
    {
        Assert.Empty(Notes(new AclEntry("S-1-5-32-545", false, Full), new AclEntry("S-1-1-0", true, ReadAttributesPermissionsSynchronise)));
    }

    [Fact]
    public void A_new_folder_gets_a_protected_ACL_for_SYSTEM_the_Administrators_and_its_creator_only()
    {
        string sddl = WindowsDataFolderAcl.NewFolderSddl("S-1-5-21-1-2-3-1001");

        Assert.StartsWith("D:P", sddl, StringComparison.Ordinal);
        Assert.Contains("(A;OICI;FA;;;SY)", sddl, StringComparison.Ordinal);
        Assert.Contains("(A;OICI;FA;;;BA)", sddl, StringComparison.Ordinal);
        Assert.Contains("(A;OICI;FA;;;S-1-5-21-1-2-3-1001)", sddl, StringComparison.Ordinal);
        Assert.Equal(3, sddl.Count(c => c == '('));
        foreach (string broad in new[] { ";;;BU)", ";;;WD)", ";;;AU)", ";;;IU)" })
        {
            Assert.DoesNotContain(broad, sddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Preparing_a_data_folder_on_this_host_returns_what_it_found_and_does_not_throw_for_a_new_one()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<string> notes = DataFolderPermissions.Prepare(folder, echo: false);

            Assert.Empty(notes);
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
