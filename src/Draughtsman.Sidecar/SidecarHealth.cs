namespace Draughtsman.Sidecar;

/// <summary>Result of the sidecar's health (ping) method.</summary>
public sealed record SidecarHealth(bool Ok, string Version, string Node, int Pid);
