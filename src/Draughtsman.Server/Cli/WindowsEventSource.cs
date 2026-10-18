using System.Diagnostics;
using System.Runtime.Versioning;

namespace Draughtsman.Server.Cli;

/// <summary>
/// The Windows Event Log source the service writes under. The logger can only write to a source that already exists, and
/// registering one needs administrator rights the service account (LocalService) does not have, so without this step every
/// message a service writes is dropped and its first event is Event ID 0, "The source draughtsman was not found". <c>service
/// install</c> runs elevated and registers it (<see cref="Register"/>); the server names the same source when it logs
/// (<c>ServerCli.CreateServerBuilder</c>), whatever the service itself is called, since a process cannot tell which name the
/// service manager gave it. Registering again, or for a second service, is harmless.
/// </summary>
public static class WindowsEventSource
{
    public const string Name = "draughtsman";

    public const string LogName = "Application";

    /// <summary>Creates the source in the Application log when it does not exist yet; returns true when this call created it.</summary>
    [SupportedOSPlatform("windows")]
    public static bool Register(string source = Name, string log = LogName)
    {
        if (EventLog.SourceExists(source))
        {
            return false;
        }

        EventLog.CreateEventSource(new EventSourceCreationData(source, log));
        return true;
    }

    /// <summary>
    /// Writes <paramref name="message"/> to the Application log as an error under the service's source, so a server that stops at start-up
    /// (before it has a logger) still leaves its reason where an administrator looks for a service that failed to start. Does nothing when
    /// the source is not registered (it never registers one itself) or the log cannot be written; a failure here must not hide the real one.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void TryWriteError(string message)
    {
        try
        {
            if (EventLog.SourceExists(Name))
            {
                EventLog.WriteEntry(Name, message.Length > 30000 ? message[..30000] : message, EventLogEntryType.Error);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }
}
