namespace Draughtsman.Server.Auth.PasswordReset;

// Explicit `= null` defaults for the reason AuthDtos.cs gives: RespectRequiredConstructorParameters.

/// <summary>Body of POST /api/auth/password-reset/request.</summary>
public sealed record PasswordResetRequestBody(string? Email = null);

/// <summary>The one answer to a "Forgot password?" request, whoever asked and whatever the address: it says nothing about whether an account has it.</summary>
public sealed record PasswordResetRequestedResponse(string Message);

/// <summary>Body of POST /api/auth/password-reset/complete: the secret from the link and the password the person chose.</summary>
public sealed record PasswordResetCompleteBody(string? Token = null, string? NewPassword = null);

/// <summary>
/// Body of POST /api/users/{id}/password-reset-link. <c>Token</c> is the only time the secret exists outside the link; <c>Link</c> is
/// the finished address when <c>server.publicUrl</c> is set, else null and the page builds it from its own origin.
/// </summary>
public sealed record PasswordResetLinkResponse(string Token, string? Link, DateTimeOffset ExpiresAt, int ExpiresInHours, string UserEmail);
