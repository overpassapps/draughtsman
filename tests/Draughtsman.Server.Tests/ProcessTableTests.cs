using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Draughtsman.Server.Export;
using Draughtsman.Sidecar;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The process-id plumbing behind stopping a hung PDF render: who is whose child, and killing exactly an owned tree. Real processes on
/// every host. The processes are Node, the same program the PDF driver is, started directly (no shell, no wrapper), so the parent each
/// one has is exactly the process that started it on Windows, Linux and macOS alike. A helper script makes each of them write its own
/// pid and the parent pid the operating system told it about, so a test asserts the relationships the production code promises
/// (every descendant found, a sibling or namesake tree spared) against what the processes themselves reported, never against an
/// incidental pid.
/// </summary>
public sealed class ProcessTableTests : IDisposable
{
    /// <summary>
    /// Starts the given number of generations of copies of itself (two children each), records "its own pid, its parent's pid" as the
    /// name and content of a file in the folder it is given, then blocks. A root that never exits by itself is the point: a shell
    /// running <c>sleep &amp; sleep &amp; wait</c> ends when its children are killed, which made "the root was stopped" a race.
    /// </summary>
    private const string TreeScript = """
        const { spawn } = require('node:child_process');
        const fs = require('node:fs');
        const path = require('node:path');
        const depth = Number(process.argv[2]);
        const folder = process.argv[3];
        const file = path.join(folder, String(process.pid));
        fs.writeFileSync(file + '.tmp', String(process.ppid));
        fs.renameSync(file + '.tmp', file);
        if (depth > 0) {
          for (let i = 0; i < 2; i++) {
            spawn(process.execPath, [__filename, String(depth - 1), folder], { stdio: 'ignore', windowsHide: true });
          }
        }
        setInterval(() => {}, 1000000);
        """;

    private const string BlockScript = "setInterval(() => {}, 1000000)";

    private readonly List<Process> _spawned = [];
    private readonly string _scratch = Directory.CreateTempSubdirectory("draughtsman-process-table-").FullName;
    private readonly string _node = ResolveNode();
    private readonly string _treeScript;

    public ProcessTableTests()
    {
        _treeScript = Path.Combine(_scratch, "tree.js");
        File.WriteAllText(_treeScript, TreeScript);
    }

    public void Dispose()
    {
        foreach (Process process in _spawned)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }

        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A just-killed process can still hold its folder for a moment; the temp folder is cleaned by the host in the end.
        }
    }

    private Process Start(params string[] arguments)
    {
        var info = new ProcessStartInfo(_node) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process = Process.Start(info)!;
        _spawned.Add(process);
        return process;
    }

    /// <summary>
    /// Starts a tree of <paramref name="generations"/> generations below its root (two children each, so 2^(g+1) - 1 processes in all) and
    /// returns its root with the folder its members report into.
    /// </summary>
    private (Process Root, string Folder, int Size) StartTree(int generations)
    {
        string folder = Directory.CreateDirectory(Path.Combine(_scratch, Guid.NewGuid().ToString("N"))).FullName;
        Process root = Start(_treeScript, generations.ToString(System.Globalization.CultureInfo.InvariantCulture), folder);
        return (root, folder, (1 << (generations + 1)) - 1);
    }

    /// <summary>Every member of a tree that has reported in so far: its pid and the parent pid it was told by the operating system.</summary>
    private static Dictionary<int, int> Reported(string folder)
    {
        var members = new Dictionary<int, int>();
        foreach (string file in Directory.GetFiles(folder))
        {
            if (Path.GetExtension(file).Length == 0 && int.TryParse(Path.GetFileName(file), out int pid) && int.TryParse(ReadShared(file), out int parent))
            {
                members[pid] = parent;
            }
        }

        return members;
    }

    /// <summary>
    /// A report file's text, or null when it cannot be read this instant. On Windows a plain <c>File.ReadAllText</c> refuses a file that anyone else still has open
    /// for writing or deleting (the child that just renamed it into place, an antivirus scan of it), which is a sharing violation, not a missing report: the waiting loop
    /// reads again, and one that is never readable still ends in its "never showed up" failure.
    /// </summary>
    private static string? ReadShared(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }

    [Fact]
    public void A_report_file_another_handle_still_has_open_for_writing_is_read_not_refused()
    {
        // Windows refuses a plain File.ReadAllText of a file anyone else holds open for writing (a sharing violation); Linux and macOS do not lock.
        string file = Path.Combine(_scratch, "held-open");
        using var writer = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write("1234"u8);
        writer.Flush();

        Assert.Equal("1234", ReadShared(file));
        Assert.Null(ReadShared(Path.Combine(_scratch, "not-there")));
    }

    /// <summary>Waits until a whole tree has reported in and every member is in the process table.</summary>
    private static async Task<Dictionary<int, int>> WaitForTree(string folder, int size)
    {
        Dictionary<int, int> members = [];
        await WaitUntil(() =>
        {
            members = Reported(folder);
            IReadOnlyDictionary<int, int> table = ProcessTable.Snapshot();
            return members.Count == size && members.Keys.All(table.ContainsKey);
        });
        return members;
    }

    [Fact]
    public async Task The_table_names_a_childs_parent_and_walks_grandchildren_but_not_siblings()
    {
        (Process parent, string folder, int size) = StartTree(generations: 2);
        (Process sibling, string siblingFolder, int siblingSize) = StartTree(generations: 1);
        Dictionary<int, int> members = await WaitForTree(folder, size);
        Dictionary<int, int> siblingMembers = await WaitForTree(siblingFolder, siblingSize);

        IReadOnlyDictionary<int, int> table = ProcessTable.Snapshot();

        // The table says what each process was itself told: the test started the root directly, the root started the rest.
        Assert.Equal(Environment.ProcessId, table[parent.Id]);
        Assert.Equal(Environment.ProcessId, members[parent.Id]);
        foreach ((int pid, int reportedParent) in members)
        {
            Assert.Equal(reportedParent, table[pid]);
        }

        // Children and grandchildren alike are below the root; nothing of the sibling tree is.
        IReadOnlyList<int> below = ProcessTable.Descendants(table, parent.Id);
        Assert.Subset(below.ToHashSet(), members.Keys.Where(id => id != parent.Id).ToHashSet());
        Assert.DoesNotContain(parent.Id, below);
        Assert.Empty(below.Intersect(siblingMembers.Keys));
        Assert.DoesNotContain(sibling.Id, below);

        // Walked from the test process itself, every member of both trees is below it.
        HashSet<int> belowUs = [.. ProcessTable.Descendants(table, Environment.ProcessId)];
        Assert.Subset(belowUs, members.Keys.ToHashSet());
        Assert.Subset(belowUs, siblingMembers.Keys.ToHashSet());
    }

    [Fact]
    public void A_damaged_table_with_a_cycle_ends_the_walk()
    {
        var table = new Dictionary<int, int> { [2] = 1, [3] = 2, [1] = 3 };

        IReadOnlyList<int> below = ProcessTable.Descendants(table, 1);

        Assert.Equal([2, 3], below.Order().ToArray());
    }

    [Fact]
    public async Task Killing_an_owned_tree_stops_the_root_and_everything_below_it_and_spares_a_namesake_that_is_not_below_it()
    {
        (Process root, string folder, int size) = StartTree(generations: 2);
        (Process namesake, string namesakeFolder, int namesakeSize) = StartTree(generations: 1);
        Dictionary<int, int> members = await WaitForTree(folder, size);
        Dictionary<int, int> namesakeMembers = await WaitForTree(namesakeFolder, namesakeSize);
        var tree = new OwnedProcessTree(_node);
        Assert.True(tree.Adopt(root.Id));
        int[] below = [.. members.Keys.Where(id => id != root.Id)];
        Assert.Equal(size - 1, below.Length);

        IReadOnlyList<int> killed = tree.KillAll();

        Assert.Contains(root.Id, killed);
        foreach (int id in below)
        {
            Assert.Contains(id, killed);
        }

        Assert.True(await WaitForExit(root));

        // A kill is a request to the operating system: the process is gone a moment later, not when the call returns.
        foreach (int id in below)
        {
            Assert.True(await WaitUntilGone(id), $"descendant {id} survived");
        }

        // The same program, started by the same test, but not below the owned root: still running, every member of it.
        Assert.False(namesake.HasExited, "a process of the same name that was not below the root was killed");
        foreach (int id in namesakeMembers.Keys)
        {
            Assert.True(IsRunning(id), $"namesake {id} was killed");
            Assert.DoesNotContain(id, killed);
        }
    }

    [Fact]
    public async Task A_process_that_started_before_the_root_is_left_alone_however_the_table_links_it_to_the_root()
    {
        // Windows keeps a process's creator id after the creator exits and later gives that number to a new process, so the table can show a long-lived
        // orphan (or the test host, or what started the test host) below a root that started long after it. The orphan here is a Node process this test
        // owns and the stale link is written into the table by hand, so a kill that follows the link only ever stops this test's own processes.
        Process orphan = Start("-e", BlockScript);
        await WaitUntil(() => ProcessTable.Snapshot().ContainsKey(orphan.Id));
        await Task.Delay(100);
        Process root = Start("-e", BlockScript);
        await WaitUntil(() => ProcessTable.Snapshot().ContainsKey(root.Id));
        Assert.True(orphan.StartTime < root.StartTime, "the orphan must have started before the root for this test to mean anything");
        var table = new Dictionary<int, int>(ProcessTable.Snapshot()) { [orphan.Id] = root.Id };
        var tree = new OwnedProcessTree(_node, () => table);
        Assert.True(tree.Adopt(root.Id));

        IReadOnlyList<int> killed = tree.KillAll();

        Assert.Contains(root.Id, killed);
        Assert.True(await WaitForExit(root));
        Assert.DoesNotContain(orphan.Id, killed);
        Assert.False(orphan.HasExited, "a process that started before the root was killed because a stale parent id placed it below the root");
    }

    [Fact]
    public async Task A_process_that_is_not_the_expected_executable_is_never_adopted_so_never_killed()
    {
        Process other = Start("-e", BlockScript);
        await WaitUntil(() => ProcessTable.Snapshot().ContainsKey(other.Id));
        string notThisNode = Path.Combine(_scratch, "definitely-not-this-node");
        var tree = new OwnedProcessTree(notThisNode);

        Assert.False(tree.Adopt(other.Id, requiredExecutable: notThisNode));
        Assert.Empty(tree.KillAll());
        Assert.False(other.HasExited);
        Assert.False(tree.Adopt(int.MaxValue - 1), "an id that is not running was adopted");
    }

    [Fact]
    public async Task A_driver_whose_start_never_returns_is_still_found_and_stopped()
    {
        var tree = new OwnedProcessTree(_node);
        Process? started = null;
        var neverReturns = new TaskCompletionSource<int>();
        using var abandon = new CancellationTokenSource();

        Task<int> start = tree.StartAsync(
            async () =>
            {
                started = Start("-e", BlockScript);
                return await neverReturns.Task;
            },
            abandon.Token);
        try
        {
            await WaitUntil(() => started is not null && ProcessTable.Snapshot().ContainsKey(started.Id));
            IReadOnlyList<int> killed = tree.KillAll();

            Assert.NotNull(started);
            Assert.Contains(started.Id, killed);
            Assert.True(await WaitForExit(started));
        }
        finally
        {
            // StartAsync holds a gate that every driver start in this test process shares until its start returns, so a failed assertion above must not skip this:
            // it left the gate held for the rest of the run, and the real driver start in PdfHealthTests then waited on it until its two time limits (a 504 after 93 seconds).
            neverReturns.SetResult(0);
            await start;
        }
    }

    [Fact]
    public void A_process_that_has_only_just_been_created_is_identified_by_its_executable_and_stopped()
    {
        if (OperatingSystem.IsWindows())
        {
            AssertSuspendedProcessIsAdoptedAndKilled();
        }
    }

    /// <summary>
    /// A process created suspended has not run an instruction, so its loader has not filled in the module list <see cref="Process.MainModule"/> reads: the state a
    /// just-started driver is in for its first milliseconds (found on a 2-CPU x64 runner, where a driver adopted that soon was "not positively identified" and left
    /// running), held for as long as the test likes instead of won or lost by timing. The kernel still knows the image.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void AssertSuspendedProcessIsAdoptedAndKilled()
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new StringBuilder($"\"{_node}\" -e \"setInterval(() => {{}}, 1000000)\"");
        Assert.True(
            CreateProcess(_node, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateSuspended | CreateNoWindow, IntPtr.Zero, null, ref startup, out ProcessInformation created),
            $"could not create a suspended process (error {Marshal.GetLastWin32Error()})");
        try
        {
            var tree = new OwnedProcessTree(_node);
            using Process process = Process.GetProcessById((int)created.ProcessId);

            Assert.True(tree.Adopt(process.Id, requiredExecutable: _node), "a process only just created was not identified as the executable it was started from");
            Assert.Contains(process.Id, tree.KillAll());
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)));
        }
        finally
        {
            TerminateProcess(created.Process, 1);
            CloseHandle(created.Process);
            CloseHandle(created.Thread);
        }
    }

    private const uint CreateSuspended = 0x4;

    private const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedBytes;
        public IntPtr ReservedPointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Waits for a condition about processes the test just started: a process takes however long the host lets it to start, so a fixed
    /// delay was a race that a loaded Mac lost. A ceiling (a minute), not a sleep: it returns as soon as the condition holds, and a condition that
    /// never holds still fails the test.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the processes the test started never showed up in the process table");
            await Task.Delay(25);
        }
    }

    /// <summary>The Node the build already needs (the sidecar's own lookup), with links followed so it is the file a process running it reports.</summary>
    private static string ResolveNode()
    {
        string found = SidecarLocator.ResolveNode(string.Empty, AppContext.BaseDirectory);
        return Path.GetFullPath(new FileInfo(found).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? found);
    }

    private static async Task<bool> WaitForExit(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitUntilGone(int id)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (IsRunning(id))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(25);
        }

        return true;
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
