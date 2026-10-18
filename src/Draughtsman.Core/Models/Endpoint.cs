namespace Draughtsman.Core.Models;

/// <summary>One end of an edge: a node id and, optionally, one of that node's port ids.</summary>
public sealed record Endpoint(string Node)
{
    public string? Port { get; init; }
}
