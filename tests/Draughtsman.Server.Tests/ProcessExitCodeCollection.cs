namespace Draughtsman.Server.Tests;

/// <summary>
/// Tests that read or set <c>Environment.ExitCode</c>, which is process-wide, run one at a time (the refusals of <c>StorageStartup</c> set it, and a
/// parallel test restoring an earlier value would otherwise race them).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessExitCodeCollection
{
    public const string Name = "process exit code";
}
