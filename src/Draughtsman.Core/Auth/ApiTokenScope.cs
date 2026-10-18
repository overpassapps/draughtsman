namespace Draughtsman.Core.Auth;

/// <summary>
/// What an API token may do. <see cref="Full"/> is the owner's whole role (how every token behaved before scopes
/// existed, and so the default); <see cref="Read"/> may only read: the server refuses every non-GET request made with
/// it, and <c>/mcp</c> refuses the two tools that write. Never a bigger right than the owner's own role.
/// </summary>
public enum ApiTokenScope
{
    Full,
    Read,
}
