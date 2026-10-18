namespace Draughtsman.Sidecar;

public enum SidecarState
{
    /// <summary>Not started yet, or a restart is pending.</summary>
    Starting,

    /// <summary>Spawned and answered its first ping.</summary>
    Running,

    /// <summary>Could not be started, or exited and has not been restarted yet. Requests fail fast until it is back.</summary>
    Unavailable,

    /// <summary>Shut down deliberately with the server.</summary>
    Stopped,
}

/// <summary>A snapshot of the supervisor's view of the process, for /api/health and logs.</summary>
public sealed record SidecarStatus(SidecarState State)
{
    /// <summary>Sidecar package version, once it has answered a ping.</summary>
    public string? Version { get; init; }

    /// <summary>Node version string as reported by the process, e.g. v22.19.0.</summary>
    public string? NodeVersion { get; init; }

    public string? Error { get; init; }

    /// <summary>Number of times the process has been started after its first start.</summary>
    public int Restarts { get; init; }

    public int? ProcessId { get; init; }

    public string? NodePath { get; init; }

    public string? ScriptPath { get; init; }
}
