using System.Globalization;

namespace Draughtsman.Server.Diagnostics;

/// <summary>
/// Finds a Draughtsman server running on this machine and the account it runs as, so the doctor can compare a data folder's owners with
/// the account that has to write there rather than with whoever happens to be running the doctor (root, under <c>docker exec -u 0</c>).
/// Linux only, from <c>/proc</c>: it reads each process's command line and real user id, and nothing else. A process whose command line is a
/// command (<c>doctor</c>, <c>backup</c>, <c>config check</c>) is not the server.
/// </summary>
public static class ServerProcess
{
    /// <summary>The user id of a running server other than this process, or null when none is found or <c>/proc</c> is not there.</summary>
    public static long? FindServerUid(int selfPid)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return null;
        }

        try
        {
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
            {
                string name = Path.GetFileName(directory);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid == selfPid)
                {
                    continue;
                }

                try
                {
                    string[] args = File.ReadAllText(Path.Combine(directory, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (IsServerCommandLine(args) && ParseUid(File.ReadAllText(Path.Combine(directory, "status"))) is { } uid)
                    {
                        return uid;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A process that exited, or one this account may not look at.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// True when the command line starts Draughtsman as a server: the executable or <c>draughtsman.dll</c> (anywhere in the line, since
    /// <c>dotnet</c> comes first) followed by nothing, <c>serve</c> or only options, and not by a command.
    /// </summary>
    public static bool IsServerCommandLine(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (int i = 0; i < args.Count; i++)
        {
            string file = Path.GetFileName(args[i]);
            bool isProgram = string.Equals(file, "draughtsman", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, "draughtsman.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, "draughtsman.dll", StringComparison.OrdinalIgnoreCase);
            if (!isProgram)
            {
                continue;
            }

            if (i + 1 >= args.Count)
            {
                return true;
            }

            string next = args[i + 1];
            return next == "serve" || (next.StartsWith('-') && next is not ("--version" or "-v" or "--help" or "-h" or "-?"));
        }

        return false;
    }

    /// <summary>The real user id from the text of <c>/proc/&lt;pid&gt;/status</c> (<c>Uid:\t10001\t10001\t10001\t10001</c>).</summary>
    public static long? ParseUid(string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        foreach (string line in status.Split('\n'))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                string[] fields = line["Uid:".Length..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > 0 && long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out long uid) ? uid : null;
            }
        }

        return null;
    }
}
