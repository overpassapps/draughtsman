namespace Draughtsman.Server.Documents;

/// <summary>
/// The wire form is <c>user:&lt;name&gt;</c> or <c>agent:&lt;name&gt;/&lt;model&gt;</c> (<see cref="Draughtsman.Core.Storage.Actor"/>).
/// Resolving who acted from this header - and the standing rule that it must never drive authorization - is
/// <see cref="Draughtsman.Server.Auth.AuthenticatedActor"/>; this constant is what both that class and MCP's own,
/// separate per-call actor argument (<c>Draughtsman.Mcp.Tools.DraughtsmanTools</c>) name in their own doc comments.
/// </summary>
public static class ActorHeader
{
    public const string Name = "X-Draughtsman-Actor";
}
