namespace Draughtsman.Server;

/// <summary>The resolved instance data folder, injectable so services never re-read the environment.</summary>
public sealed record DataFolder(string Path);
