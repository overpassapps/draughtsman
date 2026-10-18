namespace Draughtsman.Server.Auth;

/// <summary>The two authentication schemes and the policy scheme that forwards to whichever fits the request.</summary>
public static class AuthSchemes
{
    public const string Cookie = "Cookie";

    public const string Token = "Token";

    public const string Combined = "Combined";
}
