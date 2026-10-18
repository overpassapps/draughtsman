using Draughtsman.Server.Export;

namespace Draughtsman.Server.Tests;

/// <summary>
/// QA B1: the Playwright driver in a release archive must be found, complete and executable. These pin the server's own search
/// (<see cref="PlaywrightDriver"/>) over temporary folders laid out like an unpacked archive; scripts/archive-layout.test.sh pins
/// the archive side, and the real proof is a PDF from an unpacked archive.
/// </summary>
public sealed class PlaywrightDriverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "draughtsman-driver-" + Guid.NewGuid().ToString("N"));

    public PlaywrightDriverTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private string Folder(string name, bool node = true, bool package = true, bool executable = true)
    {
        string folder = Path.Combine(_root, name);
        string playwright = Path.Combine(folder, ".playwright");
        Directory.CreateDirectory(playwright);
        if (node)
        {
            string nodeFile = Path.Combine(playwright, "node", PlaywrightDriver.PlatformFolder(), PlaywrightDriver.NodeFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(nodeFile)!);
            File.WriteAllText(nodeFile, "#!/bin/sh\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(nodeFile, executable ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        if (package)
        {
            Directory.CreateDirectory(Path.Combine(playwright, "package"));
            File.WriteAllText(Path.Combine(playwright, "package", "cli.js"), "// cli");
        }

        return folder;
    }

    [Fact]
    public void A_complete_folder_is_ready()
    {
        DriverCheck check = PlaywrightDriver.Inspect(Folder("complete"));

        Assert.Equal(DriverState.Ready, check.State);
        Assert.Null(check.Detail);
    }

    [Fact]
    public void Each_missing_half_is_named_not_guessed()
    {
        // QA B1 step 3: node was extracted somewhere and package/ stayed behind; neither folder was complete.
        DriverCheck noNode = PlaywrightDriver.Inspect(Folder("no-node", node: false));
        Assert.Equal(DriverState.NodeMissing, noNode.State);
        Assert.Contains(PlaywrightDriver.NodeFileName, noNode.Detail, StringComparison.Ordinal);

        DriverCheck noPackage = PlaywrightDriver.Inspect(Folder("no-package", package: false));
        Assert.Equal(DriverState.PackageMissing, noPackage.State);
        Assert.Contains("cli.js", noPackage.Detail, StringComparison.Ordinal);

        Assert.Equal(DriverState.NotFound, PlaywrightDriver.Inspect(Path.Combine(_root, "nothing-here")).State);
    }

    [Fact]
    public void A_node_without_its_execute_bit_is_reported_and_then_repaired_by_prepare()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // QA B1 step 2: the extracted file was mode 644, "Permission denied".
        string folder = Folder("not-executable", executable: false);
        Assert.Equal(DriverState.NotExecutable, PlaywrightDriver.Inspect(folder).State);

        DriverCheck prepared = PlaywrightDriver.Prepare([folder], setEnvironment: false);

        Assert.Equal(DriverState.Ready, prepared.State);
        string node = Path.Combine(folder, ".playwright", "node", PlaywrightDriver.PlatformFolder(), PlaywrightDriver.NodeFileName);
        Assert.NotEqual(UnixFileMode.None, File.GetUnixFileMode(node) & UnixFileMode.UserExecute);
    }

    [Fact]
    public void Prepare_takes_the_first_complete_folder_and_otherwise_reports_the_most_informative_failure()
    {
        string broken = Folder("broken", package: false);
        string good = Folder("good");

        Assert.Equal(good, PlaywrightDriver.Prepare([Path.Combine(_root, "nowhere"), broken, good], setEnvironment: false).Root);

        // Nothing complete: a folder that has part of it beats one that has none, so the hint names the real file.
        DriverCheck failure = PlaywrightDriver.Prepare([Path.Combine(_root, "nowhere"), broken], setEnvironment: false);
        Assert.Equal(DriverState.PackageMissing, failure.State);
        Assert.Equal(broken, failure.Root);
    }

    [Fact]
    public void The_test_output_folder_has_a_complete_driver_the_server_can_find_on_its_own()
    {
        // The dev tree's own copy (Microsoft.Playwright's build targets): the search must accept what Playwright itself accepts.
        DriverCheck check = PlaywrightDriver.Prepare(PlaywrightDriver.CandidateRoots(), setEnvironment: false);

        Assert.True(check.Ready, check.Detail);
    }
}
