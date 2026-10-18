using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth.PasswordReset;

namespace Draughtsman.Server.Auth.Endpoints;

/// <summary>
/// The admin Users page (build-spec-v0.1.md section 7; design/atlas/login-admin.md's Users row, scoped down for this
/// task to list, add, change role, reset password and deactivate). Every route requires
/// <see cref="AuthPolicies.CookieAdminOnly"/>: a cookie-authenticated admin, never an API token regardless of its
/// owner's role (security review F3) - account management is cookie-session-only.
/// </summary>
public static class UserEndpoints
{
    /// <summary>The columns are 256 characters (DraughtsmanDbContext); a longer value is a 500 on Postgres, so it is a 400 here (AU-18).</summary>
    internal const int MaxEmailLength = 254;

    internal const int MaxDisplayNameLength = 200;

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/users").RequireAuthorization(AuthPolicies.CookieAdminOnly);
        group.MapGet("", ListUsers).WithName("ListUsers");
        group.MapPost("", CreateUser).WithName("CreateUser");
        group.MapPatch("/{id}", UpdateUser).WithName("UpdateUser");
        group.MapPost("/{id}/password-reset-link", PasswordResetEndpoints.CreateAdminLink).WithName("CreatePasswordResetLink");
        return app;
    }

    private static async Task<IResult> ListUsers(IUserStore users, CancellationToken cancellationToken)
    {
        IReadOnlyList<User> all = await users.ListUsers(cancellationToken);
        return Results.Ok(all.Select(UserResponse.From));
    }

    private static async Task<IResult> CreateUser(CreateUserRequest? body, HttpContext context, IUserStore users, SecurityEventLog events, CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return MalformedBody();
        }

        if (string.IsNullOrWhiteSpace(body.Email) || !body.Email.Contains('@', StringComparison.Ordinal))
        {
            return ValidationProblem("A valid email address is required.");
        }

        if (body.Email.Length > MaxEmailLength)
        {
            return ValidationProblem($"An email address may be at most {MaxEmailLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(body.DisplayName))
        {
            return ValidationProblem("A display name is required.");
        }

        if (body.DisplayName.Length > MaxDisplayNameLength)
        {
            return ValidationProblem($"A display name may be at most {MaxDisplayNameLength} characters.");
        }

        if (!TryParseRole(body.Role, out UserRole role))
        {
            return ValidationProblem("Role must be \"Admin\" or \"Editor\".");
        }

        if (string.IsNullOrEmpty(body.Password) || body.Password.Length < PasswordHasher.MinimumLength)
        {
            return ValidationProblem($"Password must be at least {PasswordHasher.MinimumLength} characters.");
        }

        try
        {
            User created = await users.CreateUser(body.Email, body.DisplayName, role, PasswordHasher.Hash(body.Password), DateTimeOffset.UtcNow, cancellationToken);
            await events.Record(context, SecurityEventTypes.UserCreated, target: created, detail: $"role {role}");
            return Results.Created($"/api/users/{Uri.EscapeDataString(created.Id)}", UserResponse.From(created));
        }
        catch (InvalidOperationException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "User already exists", detail: $"A user with email '{body.Email}' already exists.");
        }
    }

    /// <summary>
    /// Applies whichever of role, password and active are present. An admin may not deactivate or demote themselves
    /// here - a self-service way to lock the only signed-in admin out is a production incident waiting to happen,
    /// not a feature.
    /// </summary>
    private static async Task<IResult> UpdateUser(string id, UpdateUserRequest? body, HttpContext context, IUserStore users, IPasswordResetStore resets, TimeProvider clock, SecurityEventLog events, CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return MalformedBody();
        }

        User? target = await users.GetUserById(id, cancellationToken);
        if (target is null)
        {
            return UserNotFound(id);
        }

        bool isSelf = string.Equals(context.User.UserId(), id, StringComparison.Ordinal);

        if (body.Role is not null)
        {
            if (!TryParseRole(body.Role, out UserRole role))
            {
                return ValidationProblem("Role must be \"Admin\" or \"Editor\".");
            }

            if (isSelf && role != UserRole.Admin)
            {
                return ValidationProblem("You cannot remove your own Admin role.");
            }

            if (!isSelf && role != UserRole.Admin && target.Role == UserRole.Admin && target.Active)
            {
                if (!await users.TryDemoteAdmin(id, role, cancellationToken))
                {
                    return ValidationProblem(LastAdminDetail);
                }
            }
            else
            {
                await users.UpdateUserRole(id, role, cancellationToken);
            }

            if (role != target.Role)
            {
                await events.Record(context, SecurityEventTypes.UserRoleChanged, target: target, detail: $"{target.Role} to {role}");
            }
        }

        if (body.Password is not null)
        {
            if (body.Password.Length < PasswordHasher.MinimumLength)
            {
                return ValidationProblem($"Password must be at least {PasswordHasher.MinimumLength} characters.");
            }

            await users.UpdateUserPassword(id, PasswordHasher.Hash(body.Password), cancellationToken);
            // A changed password invalidates every existing session for the account, including one an attacker
            // holds, and every API token (security review F8) - a leaked token survives a password reset otherwise.
            await users.RevokeSessionsForUser(id, cancellationToken);
            await users.RevokeApiTokensForUser(id, cancellationToken);
            // A link made before the password was set must not be able to undo it.
            await resets.InvalidateLive(id, clock.GetUtcNow(), cancellationToken);
            await events.Record(context, SecurityEventTypes.PasswordReset, target: target, detail: "password set by an administrator; sessions and tokens revoked");
        }

        if (body.Active is bool active)
        {
            if (isSelf && !active)
            {
                return ValidationProblem("You cannot deactivate your own account.");
            }

            if (!active && !isSelf && target.Role == UserRole.Admin && target.Active)
            {
                if (!await users.TryDeactivateAdmin(id, cancellationToken))
                {
                    return ValidationProblem(LastAdminDetail);
                }
            }
            else
            {
                await users.SetUserActive(id, active, cancellationToken);
            }

            if (!active)
            {
                await users.RevokeSessionsForUser(id, cancellationToken);
                // A deactivated account cannot reset (the link is also refused at use), and stays that way if it is reactivated later.
                await resets.InvalidateLive(id, clock.GetUtcNow(), cancellationToken);
            }

            if (active != target.Active)
            {
                await events.Record(context, active ? SecurityEventTypes.UserReactivated : SecurityEventTypes.UserDeactivated, target: target);
            }
        }

        User updated = await users.GetUserById(id, cancellationToken) ?? target;
        return Results.Ok(UserResponse.From(updated));
    }

    private static bool TryParseRole(string? role, out UserRole parsed)
    {
        return Enum.TryParse(role, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }

    private const string LastAdminDetail = "This is the last active administrator; promote another user to Admin first.";

    private static IResult MalformedBody()
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be a JSON object.");
    }

    private static IResult ValidationProblem(string detail)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: detail);
    }

    private static IResult UserNotFound(string id)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "User not found", detail: $"No user with id '{id}'.");
    }
}
