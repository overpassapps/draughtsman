using Draughtsman.Server.Hardening;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-12: the data folder's secrets are owner-only. Unix only: modes do not exist on Windows, where these are skipped.
/// Both directions: what must be tightened is, and what must be left alone (the data folder's own mode, files nobody
/// listed) is.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class DataFolderPermissionsTests
{
    private const UnixFileMode GroupAndOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [UnixFact]
    public async Task A_running_server_creates_a_private_folder_database_and_side_files()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();
        // Some writes, so the WAL and SHM files exist beside the database.
        await client.PostAsync("/api/documents", new StringContent("{\"title\":\"p\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(factory.DataFolder) & GroupAndOther);
        string[] sqliteFiles = Directory.GetFiles(factory.DataFolder, "data.db*");
        Assert.Contains(sqliteFiles, f => Path.GetFileName(f) == "data.db");
        foreach (string file in sqliteFiles)
        {
            Assert.True((File.GetUnixFileMode(file) & GroupAndOther) == 0, $"{Path.GetFileName(file)} is {Convert.ToString((int)File.GetUnixFileMode(file), 8)}");
        }

        foreach (string folder in Directory.GetDirectories(factory.DataFolder))
        {
            Assert.True((File.GetUnixFileMode(folder) & GroupAndOther) == 0, $"{Path.GetFileName(folder)} folder is open");
        }
    }

    [UnixFact]
    public void Earlier_runs_leftovers_are_tightened_and_a_folder_the_operator_shared_is_reported_not_changed()
    {
        string folder = NewFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "keys"));
            Directory.CreateDirectory(Path.Combine(folder, "ai"));
            Directory.CreateDirectory(Path.Combine(folder, "assets"));
            string[] files = [Path.Combine(folder, "data.db"), Path.Combine(folder, "data.db-wal"), Path.Combine(folder, "data.db-shm"), Path.Combine(folder, "draughtsman.yaml"),
                Path.Combine(folder, "keys", "key-1.xml"), Path.Combine(folder, "ai", "settings.json")];
            foreach (string file in files)
            {
                File.WriteAllText(file, "x");
                File.SetUnixFileMode(file, (UnixFileMode)Convert.ToInt32("644", 8));
            }

            string unrelated = Path.Combine(folder, "licence.key");
            File.WriteAllText(unrelated, "licence");
            File.SetUnixFileMode(unrelated, (UnixFileMode)Convert.ToInt32("644", 8));
            foreach (string sub in new[] { "keys", "ai", "assets" })
            {
                File.SetUnixFileMode(Path.Combine(folder, sub), (UnixFileMode)Convert.ToInt32("755", 8));
            }

            File.SetUnixFileMode(folder, (UnixFileMode)Convert.ToInt32("777", 8));

            IReadOnlyList<string> notes = DataFolderPermissions.Tighten(folder);

            foreach (string file in files)
            {
                Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(file) & GroupAndOther);
            }

            foreach (string sub in new[] { "keys", "ai", "assets" })
            {
                Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(Path.Combine(folder, sub)) & GroupAndOther);
            }

            // Not ours to tighten: the licence file (not a secret) and the data folder's own mode (maybe shared on purpose).
            Assert.Equal((UnixFileMode)Convert.ToInt32("644", 8), File.GetUnixFileMode(unrelated));
            Assert.Equal((UnixFileMode)Convert.ToInt32("777", 8), File.GetUnixFileMode(folder));
            string note = Assert.Single(notes);
            Assert.Contains("777", note, StringComparison.Ordinal);
            Assert.Contains("10001", note, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(folder, (UnixFileMode)Convert.ToInt32("700", 8));
            Directory.Delete(folder, recursive: true);
        }
    }

    [UnixFact]
    public void A_folder_that_is_already_private_needs_no_notes_and_nothing_changes()
    {
        string folder = NewFolder();
        try
        {
            File.SetUnixFileMode(folder, (UnixFileMode)Convert.ToInt32("700", 8));
            string db = Path.Combine(folder, "data.db");
            File.WriteAllText(db, "x");
            File.SetUnixFileMode(db, (UnixFileMode)Convert.ToInt32("600", 8));

            Assert.Empty(DataFolderPermissions.Tighten(folder));
            Assert.Equal((UnixFileMode)Convert.ToInt32("600", 8), File.GetUnixFileMode(db));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Tightening_a_folder_that_does_not_exist_is_a_quiet_no_op()
    {
        Assert.Empty(DataFolderPermissions.Tighten(Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"))));
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows())
            {
                Skip = "File modes do not exist on Windows.";
            }
        }
    }
}
