namespace Draughtsman.Core.Auth;

/// <summary>
/// A local account (build-spec-v0.1.md section 7). <see cref="PasswordHash"/> is the Argon2id encoded form
/// (see <c>Draughtsman.Server.Auth.PasswordHasher</c>) and is never logged. A deactivated user (<see cref="Active"/>
/// false) keeps their row, audit history and tokens but can no longer sign in or authenticate a token.
/// </summary>
public sealed record User(
    string Id,
    string Email,
    string DisplayName,
    UserRole Role,
    string PasswordHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSignInAt,
    bool Active);
