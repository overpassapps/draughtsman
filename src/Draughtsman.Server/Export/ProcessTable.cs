using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Draughtsman.Server.Export;

/// <summary>
/// Who started whom: a snapshot of every running process's id and parent id, on Windows (the toolhelp snapshot), Linux
/// (<c>/proc</c>) and macOS (<c>ps</c>). It exists so the PDF renderer can stop exactly the processes one render started, by
/// id, and nothing else: .NET reports a process's children on no platform, and killing "node" or "msedge" by name would also stop the
/// sidecar, another render or the administrator's own browser (a shared name is never a reason to kill).
/// </summary>
internal static class ProcessTable
{
    /// <summary>Every process id that can be seen, with its parent's id (which may no longer be running).</summary>
    public static IReadOnlyDictionary<int, int> Snapshot()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsSnapshot();
        }

        return Directory.Exists("/proc/self") ? ProcSnapshot() : PsSnapshot();
    }

    /// <summary>
    /// The full path of the executable <paramref name="process"/> is running, from the moment it exists. <see cref="Process.MainModule"/> is not that on
    /// Windows: it reads the module list the process's own loader fills in, so for a process only just created it is null (or throws), and a driver
    /// identified by it a few milliseconds after it appeared was "not positively identified" and left alone, so a start that never returned was not
    /// stopped (seen on a 2-vCPU x64 CI runner; reproduced under emulated x64 on 2 CPUs). The kernel knows the image from the start, so Windows asks it.
    /// Elsewhere this is <see cref="Process.MainModule"/> as before, with its exceptions.
    /// </summary>
    public static string? ImagePath(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return OperatingSystem.IsWindows() ? WindowsImagePath(process.Id) : process.MainModule?.FileName;
    }

    /// <summary>The ids of every process below <paramref name="root"/> in <paramref name="table"/> (children, their children and so on), not including <paramref name="root"/>. A cycle in a damaged table ends the walk instead of looping.</summary>
    public static IReadOnlyList<int> Descendants(IReadOnlyDictionary<int, int> table, int root)
    {
        ArgumentNullException.ThrowIfNull(table);
        ILookup<int, int> children = table.Where(pair => pair.Key != pair.Value).ToLookup(pair => pair.Value, pair => pair.Key);
        var found = new List<int>();
        var seen = new HashSet<int> { root };
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            foreach (int child in children[queue.Dequeue()])
            {
                if (seen.Add(child))
                {
                    found.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        return found;
    }

    private static Dictionary<int, int> ProcSnapshot()
    {
        var table = new Dictionary<int, int>();
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
            {
                continue;
            }

            try
            {
                // "pid (comm) state ppid ...": comm may hold spaces and parentheses, so the fields start after the last ')'.
                string stat = File.ReadAllText(Path.Combine(directory, "stat"));
                int close = stat.LastIndexOf(')');
                string[] fields = stat[(close + 2)..].Split(' ');
                if (close > 0 && fields.Length > 1 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent))
                {
                    table[pid] = parent;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The process exited while it was being read.
            }
        }

        return table;
    }

    private static Dictionary<int, int> PsSnapshot()
    {
        var table = new Dictionary<int, int>();
        var start = new ProcessStartInfo("ps", ["-A", "-o", "pid=,ppid="]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using Process? ps = Process.Start(start);
        if (ps is null)
        {
            return table;
        }

        Task<string> output = ps.StandardOutput.ReadToEndAsync();
        if (!ps.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            ps.Kill();
            return table;
        }

        foreach (string line in output.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length == 2 && int.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent))
            {
                table[pid] = parent;
            }
        }

        return table;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Dictionary<int, int> WindowsSnapshot()
    {
        var table = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            return table;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                table[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return table;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? WindowsImagePath(int pid)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // 1,024 characters nearly always holds it; a path the long-path support allows may need up to the 32,767 the API can return.
            for (int capacity = 1024; capacity <= MaxImagePathCharacters; capacity *= 2)
            {
                var path = new System.Text.StringBuilder(capacity);
                uint length = (uint)capacity;
                if (QueryFullProcessImageName(process, 0, path, ref length))
                {
                    return path.ToString();
                }

                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                {
                    return null;
                }
            }

            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint Th32csSnapProcess = 0x2;

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private const int ErrorInsufficientBuffer = 122;

    private const int MaxImagePathCharacters = 32768;

    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder path, ref uint size);
}
