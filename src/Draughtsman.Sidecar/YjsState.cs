namespace Draughtsman.Sidecar;

/// <summary>An encoded Yjs document (a full update) and its state vector, as raw bytes.</summary>
public sealed record YjsState(byte[] State, byte[] StateVector);
