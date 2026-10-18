namespace Draughtsman.Core.Auth;

/// <summary>
/// The authenticated identity behind the current call, if any. MCP (<c>src/Draughtsman.Mcp</c>) is transport-agnostic
/// and must not depend on ASP.NET Core, so this is how it learns who is actually calling without referencing
/// <c>HttpContext</c> directly. Null over stdio: build-spec-v0.1.md section 9 keeps <c>draughtsman mcp --stdio</c>
/// unauthenticated and same-machine, so there is no identity to resolve there, by design.
/// </summary>
public interface IActorContext
{
    AuthenticatedIdentity? Current { get; }
}

/// <summary>
/// The authenticated caller: a real user row's id and display name, whether from a session or a token's owner.
/// <paramref name="ReadOnly"/> is true for a read-scoped API token (<see cref="ApiTokenScope.Read"/>): the MCP tools
/// that write refuse such a caller.
/// </summary>
public sealed record AuthenticatedIdentity(string UserId, string DisplayName, bool ReadOnly = false);

/// <summary>Always unauthenticated. The stdio host's registration: same-machine trust, no identity to check anything against.</summary>
public sealed class NullActorContext : IActorContext
{
    public AuthenticatedIdentity? Current => null;
}
