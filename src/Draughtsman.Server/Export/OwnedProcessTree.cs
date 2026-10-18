using System.Diagnostics;

namespace Draughtsman.Server.Export;

/// <summary>
/// The processes one PDF render started, known by id so a render that never finishes can be stopped without touching anything
/// else. A render starts Playwright's driver (a Node process the server launches) and the driver starts the browser and its
/// helpers; when the browser hangs, the render hangs with it and the driver, the browser and every helper stay alive (found on a
/// Windows service: Edge's helpers outlived their request, one Node driver per attempt). The driver is identified at the moment
/// it appears (a child of this process, new since just before it was started, whose executable is the driver's own Node file, which
/// the sidecar's Node is not), remembered with its start time so a reused id is never mistaken for it, and on <see cref="KillAll"/>
/// the driver and everything below it are killed by id. A process that cannot be positively identified is left alone, and so is one that
/// started before the root it is said to be below: a parent id is only a claim (Windows keeps the creator's id after the creator exits and
/// later gives the number to a new process), and .NET's own <c>Kill(entireProcessTree: true)</c> applies the same rule.
/// </summary>
internal sealed class OwnedProcessTree
{
    // Starting a driver is serialised so "the new child since the snapshot" can only be this render's, never a concurrent one's.
    private static readonly SemaphoreSlim s_startGate = new(1, 1);

    private readonly string _driverExecutable;
    private readonly Func<IReadOnlyDictionary<int, int>> _snapshot;
    private readonly List<(int Pid, DateTime Started)> _roots = [];
    private IReadOnlyDictionary<int, int>? _beforeStart;

    /// <param name="driverExecutable">The full path of the driver's Node file; only a process running exactly that file is ever taken as the driver.</param>
    public OwnedProcessTree(string driverExecutable)
        : this(driverExecutable, ProcessTable.Snapshot)
    {
    }

    /// <param name="snapshot">Where the table of who started whom comes from; a test hands in one that names a stale parent.</param>
    internal OwnedProcessTree(string driverExecutable, Func<IReadOnlyDictionary<int, int>> snapshot)
    {
        _driverExecutable = driverExecutable;
        _snapshot = snapshot;
    }

    /// <summary>The ids of the processes currently owned (the driver roots, not their descendants), for logs and tests.</summary>
    public IReadOnlyList<int> RootIds
    {
        get
        {
            lock (_roots)
            {
                return [.. _roots.Select(root => root.Pid)];
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="start"/> (which starts the driver) and records the driver process it created. The driver is also picked up
    /// by <see cref="KillAll"/> if <paramref name="start"/> never returns, since a hung start is the case that most needs the kill.
    /// </summary>
    /// <param name="abandon">Cancelled when the render was given up on while it still waited its turn to start a driver, so it never starts one nobody wants.</param>
    public async Task<T> StartAsync<T>(Func<Task<T>> start, CancellationToken abandon = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        await s_startGate.WaitAsync(abandon).ConfigureAwait(false);
        try
        {
            _beforeStart = _snapshot();
            T started = await start().ConfigureAwait(false);
            AdoptNewDriverChildren(_snapshot());
            return started;
        }
        finally
        {
            s_startGate.Release();
        }
    }

    private void AdoptNewDriverChildren(IReadOnlyDictionary<int, int> table)
    {
        IReadOnlyDictionary<int, int>? before = _beforeStart;
        if (before is null)
        {
            return;
        }

        foreach (KeyValuePair<int, int> pair in table)
        {
            if (pair.Value == Environment.ProcessId && !before.ContainsKey(pair.Key) && !RootIds.Contains(pair.Key))
            {
                Adopt(pair.Key, requiredExecutable: _driverExecutable);
            }
        }
    }

    /// <summary>
    /// Takes <paramref name="pid"/> as an owned root if it is running and, when <paramref name="requiredExecutable"/> is given, its
    /// executable is exactly that file. Returns false for anything else (gone, unreadable, a different program).
    /// </summary>
    public bool Adopt(int pid, string? requiredExecutable = null)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (requiredExecutable is not null && !SamePath(ProcessTable.ImagePath(process), requiredExecutable))
            {
                return false;
            }

            lock (_roots)
            {
                _roots.Add((pid, process.StartTime));
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Kills every owned root and everything below it, children before parents, by id; returns the ids that were stopped. Safe to call with nothing owned.</summary>
    public IReadOnlyList<int> KillAll()
    {
        (int Pid, DateTime Started)[] roots;
        lock (_roots)
        {
            roots = [.. _roots];
        }

        IReadOnlyDictionary<int, int> table = _snapshot();
        // A driver whose start never returned has not been recorded yet.
        AdoptNewDriverChildren(table);
        lock (_roots)
        {
            roots = [.. _roots];
        }

        var killed = new List<int>();
        foreach ((int pid, DateTime started) in roots)
        {
            // Everything below the root is read before anything is killed: a killed parent's children stay in the table, pointing at it.
            List<int> victims = [.. ProcessTable.Descendants(table, pid)];
            victims.Reverse();
            foreach (int victim in victims)
            {
                if (Kill(victim, expectedStart: null, notBefore: started))
                {
                    killed.Add(victim);
                }
            }

            if (Kill(pid, started))
            {
                killed.Add(pid);
            }
        }

        return killed;
    }

    private static bool Kill(int pid, DateTime? expectedStart, DateTime? notBefore = null)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (expectedStart is { } started && process.StartTime != started)
            {
                return false; // the id now belongs to a different process
            }

            // Something below the root started after it did. The table's parent ids are not that proof, and what predates the root (a long-lived orphan,
            // or the very process that is running this code and the ones that started it) is not below it. The handle the check opened is the one Kill uses.
            if (notBefore is { } floor && process.StartTime < floor)
            {
                return false;
            }

            process.Kill();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SamePath(string? actual, string expected)
    {
        return actual is not null && string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
