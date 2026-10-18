namespace Draughtsman.Sidecar;

/// <summary>
/// How the server finds and supervises the Node sidecar. The server fills this from the sidecar section of
/// draughtsman.yaml; every member has a default that works for a published tree and for a repository checkout.
/// </summary>
public sealed class SidecarOptions
{
    /// <summary>
    /// Node executable: a full path, or a bare name looked up on PATH. Empty means the bundled runtime under
    /// sidecar/ next to the server binary if present, otherwise node on PATH.
    /// </summary>
    public string NodePath { get; init; } = string.Empty;

    /// <summary>
    /// The bundled sidecar script. Empty means sidecar/sidecar.js next to the server binary, where dotnet build
    /// and dotnet publish put it.
    /// </summary>
    public string ScriptPath { get; init; } = string.Empty;

    /// <summary>Folder the bundled lookups start from. Defaults to the folder holding the running binary.</summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    public TimeSpan LayoutTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan YjsTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a freshly spawned process has to answer its first ping.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Wait before the first restart after a crash; doubles per consecutive failure up to <see cref="MaxRestartDelay"/>.</summary>
    public TimeSpan InitialRestartDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxRestartDelay { get; init; } = TimeSpan.FromSeconds(30);
}
