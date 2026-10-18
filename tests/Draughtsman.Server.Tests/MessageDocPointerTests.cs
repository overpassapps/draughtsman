using System.Text.RegularExpressions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The server's log lines, errors and <c>--help</c> text name documentation pages by path (<c>docs/upgrade-backup.md</c>).
/// A page that is renamed or merged leaves those pointers dangling in shipped binaries, so every <c>docs/*.md</c> path a
/// message in <c>src/</c> names must exist, and the backup guide is named by its current name only.
/// </summary>
public class MessageDocPointerTests
{
    private static readonly Regex s_docPath = new(@"docs/[A-Za-z0-9_\-/]+\.md", RegexOptions.Compiled);

    [Fact]
    public void Every_docs_page_a_server_message_names_exists()
    {
        string root = PdfAvailabilityTests.RepoRoot();
        List<(string Page, string File)> named = NamedDocPages(root);

        Assert.NotEmpty(named);
        string[] missing = named.Where(pair => !System.IO.File.Exists(Path.Combine(root, pair.Page))).Select(pair => $"{pair.Page} (in {pair.File})").ToArray();
        Assert.True(missing.Length == 0, "Messages name docs pages that do not exist: " + string.Join(", ", missing));
    }

    [Fact]
    public void Backup_messages_name_upgrade_backup_not_the_old_pointer_page()
    {
        List<(string Page, string File)> named = NamedDocPages(PdfAvailabilityTests.RepoRoot());

        Assert.DoesNotContain(named, pair => pair.Page == "docs/backup-restore.md");
        // The messages that used to name the old page still name a backup guide: the right one.
        foreach (string source in new[] { "StorageMigrator.cs", "ServerCli.cs", "BackupCommands.cs" })
        {
            Assert.Contains(named, pair => pair.Page == "docs/upgrade-backup.md" && pair.File.EndsWith(source, StringComparison.Ordinal));
        }
    }

    /// <summary>Every <c>docs/*.md</c> path in a hand-written source file under <c>src/</c>, with the file that names it.</summary>
    private static List<(string Page, string File)> NamedDocPages(string root)
    {
        var named = new List<(string Page, string File)>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin") || parts.Contains("Migrations"))
            {
                continue;
            }

            foreach (Match match in s_docPath.Matches(System.IO.File.ReadAllText(file)))
            {
                named.Add((match.Value, relative));
            }
        }

        return named;
    }
}
