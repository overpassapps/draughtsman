using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Auth.Recovery;
using Draughtsman.Server.Cli;
using Draughtsman.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-03: the host-shell lost-admin recovery. Run against a real data folder and a real SQLite database, the same as
/// the verb will be; the trust model is on <see cref="AdminRecoveryService"/>.
/// </summary>
public sealed class ResetAdminTests : IDisposable
{
    private const string NewPassword = "recovered-password-42";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public ResetAdminTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        SqlitePools.ReleaseUnder(_folder);
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Resetting_an_admin_sets_the_password_revokes_sessions_and_tokens_and_records_who_ran_it()
    {
        (string adminId, string sessionHash, string tokenHash) = await Seed();

        (int code, string output, string error) = await Run(["lost@example.com"]);

        Assert.Equal(0, code);
        Assert.Contains("Password reset for lost@example.com", output, StringComparison.Ordinal);
        // The password is never echoed unless it was generated for the operator.
        Assert.DoesNotContain(NewPassword, output + error, StringComparison.Ordinal);
        await using ServiceProvider services = Open();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IUserStore users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        User user = (await users.GetUserById(adminId))!;
        Assert.True(PasswordHasher.Verify(NewPassword, user.PasswordHash));
        Assert.False(PasswordHasher.Verify("the-old-forgotten-password", user.PasswordHash));
        Assert.True((await users.GetSessionByHash(sessionHash))!.Revoked);
        Assert.True((await users.GetApiTokenByHash(tokenHash))!.Revoked);

        SecurityEvent recorded = Assert.Single((await scope.ServiceProvider.GetRequiredService<ISecurityEventStore>().List(new SecurityEventQuery(Types: [SecurityEventTypes.AdminRecovered]))).Items);
        Assert.Equal(adminId, recorded.TargetUserId);
        Assert.Equal(SecurityEventMethods.Host, recorded.Method);
        Assert.Contains("host user " + Environment.UserName, recorded.ActorLabel, StringComparison.Ordinal);
        Assert.DoesNotContain(NewPassword, recorded.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_running_server_honours_the_reset_at_once_and_the_old_password_and_sessions_stop_working()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        (_, string tokenSecret) = await admin.CreateToken("agent");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/documents")).StatusCode);

        (int code, _, string error) = await Run(["local@example.com"], folder: factory.DataFolder);

        Assert.True(code == 0, error);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Bearer(tokenSecret).GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword })).StatusCode);
        HttpClient back = await factory.SignIn(AuthTestHelpers.DefaultEmail, NewPassword);
        Assert.Equal(HttpStatusCode.OK, (await back.GetAsync("/api/documents")).StatusCode);
        // The recovery shows up in the admin's own log.
        Assert.Contains(await back.SecurityEvents("?type=admin.recovered"), e => e!["method"]!.GetValue<string>() == "host");
    }

    [Fact]
    public async Task A_deactivated_admin_is_brought_back()
    {
        (string adminId, _, _) = await Seed(deactivate: true);

        (int code, string output, _) = await Run(["lost@example.com"]);

        Assert.Equal(0, code);
        Assert.Contains("reactivated", output, StringComparison.Ordinal);
        await using ServiceProvider services = Open();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IUserStore>().GetUserById(adminId))!.Active);
    }

    [Fact]
    public async Task An_editor_is_refused_without_promote_and_nothing_changes_then_promoted_with_it()
    {
        await Seed();
        await using (ServiceProvider services = Open())
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserStore>().CreateUser("editor@example.com", "Editor", UserRole.Editor, PasswordHasher.Hash("editors-own-password"), DateTimeOffset.UtcNow);
        }

        (int refusedCode, _, string refusedError) = await Run(["editor@example.com"]);
        Assert.Equal(1, refusedCode);
        Assert.Contains("--promote", refusedError, StringComparison.Ordinal);
        await using (ServiceProvider services = Open())
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            User editor = (await scope.ServiceProvider.GetRequiredService<IUserStore>().GetUserByEmail("editor@example.com"))!;
            Assert.Equal(UserRole.Editor, editor.Role);
            Assert.True(PasswordHasher.Verify("editors-own-password", editor.PasswordHash));
            Assert.Empty((await scope.ServiceProvider.GetRequiredService<ISecurityEventStore>().List(new SecurityEventQuery(Types: [SecurityEventTypes.AdminRecovered]))).Items);
        }

        (int promotedCode, string promotedOutput, _) = await Run(["editor@example.com", "--promote"]);
        Assert.Equal(0, promotedCode);
        Assert.Contains("promoted to Admin", promotedOutput, StringComparison.Ordinal);
        await using ServiceProvider after = Open();
        await using AsyncServiceScope afterScope = after.CreateAsyncScope();
        User promoted = (await afterScope.ServiceProvider.GetRequiredService<IUserStore>().GetUserByEmail("editor@example.com"))!;
        Assert.Equal(UserRole.Admin, promoted.Role);
        Assert.True(PasswordHasher.Verify(NewPassword, promoted.PasswordHash));
    }

    /// <summary>The verb as an operator runs it: through the dispatcher, data folder by option, password on standard input.</summary>
    [Fact]
    public async Task The_reset_admin_verb_runs_through_the_cli_dispatcher()
    {
        (string adminId, string sessionHash, _) = await Seed();
        var output = new StringWriter();
        var error = new StringWriter();

        CliOutcome outcome = await ServerCli.ExecuteAsync(["reset-admin", "lost@example.com", "--DRAUGHTSMAN_DATA=" + _folder], output, error, stdin: new StringReader(NewPassword + "\n"));

        Assert.False(outcome.Serve);
        Assert.True(outcome.ExitCode == 0, error.ToString());
        Assert.Contains("Password reset for lost@example.com", output.ToString(), StringComparison.Ordinal);
        await using ServiceProvider services = Open();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IUserStore users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        Assert.True(PasswordHasher.Verify(NewPassword, (await users.GetUserById(adminId))!.PasswordHash));
        Assert.True((await users.GetSessionByHash(sessionHash))!.Revoked);
    }

    [Fact]
    public async Task The_reset_admin_verb_refuses_an_editor_without_promote_through_the_dispatcher()
    {
        await Seed();
        await using (ServiceProvider services = Open())
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserStore>().CreateUser("editor@example.com", "Editor", UserRole.Editor, PasswordHasher.Hash("editors-own-password"), DateTimeOffset.UtcNow);
        }

        var error = new StringWriter();
        CliOutcome refused = await ServerCli.ExecuteAsync(["reset-admin", "editor@example.com", "--DRAUGHTSMAN_DATA=" + _folder], new StringWriter(), error, stdin: new StringReader(NewPassword + "\n"));

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("--promote", error.ToString(), StringComparison.Ordinal);
        await using (ServiceProvider services = Open())
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            User editor = (await scope.ServiceProvider.GetRequiredService<IUserStore>().GetUserByEmail("editor@example.com"))!;
            Assert.Equal(UserRole.Editor, editor.Role);
            Assert.True(PasswordHasher.Verify("editors-own-password", editor.PasswordHash));
        }

        // The same command with --promote goes through, so the refusal above was the missing flag and nothing else.
        CliOutcome promoted = await ServerCli.ExecuteAsync(["reset-admin", "editor@example.com", "--promote", "--DRAUGHTSMAN_DATA=" + _folder], new StringWriter(), new StringWriter(), stdin: new StringReader(NewPassword + "\n"));
        Assert.Equal(0, promoted.ExitCode);
    }

    [Fact]
    public async Task Generate_prints_a_working_password_once()
    {
        (string adminId, _, _) = await Seed();

        (int code, string output, _) = await Run(["lost@example.com", "--generate"], promptPassword: _ => throw new InvalidOperationException("must not prompt when generating"));

        Assert.Equal(0, code);
        string generated = output.Split('\n').Single(l => l.StartsWith("New password (shown once): ", StringComparison.Ordinal))["New password (shown once): ".Length..].Trim();
        Assert.Equal(24, generated.Length);
        await using ServiceProvider services = Open();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        Assert.True(PasswordHasher.Verify(generated, (await scope.ServiceProvider.GetRequiredService<IUserStore>().GetUserById(adminId))!.PasswordHash));
    }

    [Fact]
    public async Task Refusals_leave_everything_untouched_and_exit_non_zero()
    {
        (string adminId, string sessionHash, _) = await Seed();

        // Unknown account, password too short, and (separately) no users at all.
        (int unknown, _, string unknownError) = await Run(["nobody@example.com"]);
        (int tooShort, _, string shortError) = await Run(["lost@example.com"], promptPassword: _ => "short");
        Assert.Equal(1, unknown);
        Assert.Contains("No account", unknownError, StringComparison.Ordinal);
        Assert.Equal(1, tooShort);
        Assert.Contains("at least 12", shortError, StringComparison.Ordinal);

        await using ServiceProvider services = Open();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IUserStore users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        Assert.True(PasswordHasher.Verify("the-old-forgotten-password", (await users.GetUserById(adminId))!.PasswordHash));
        Assert.False((await users.GetSessionByHash(sessionHash))!.Revoked);
        Assert.Empty((await scope.ServiceProvider.GetRequiredService<ISecurityEventStore>().List(new SecurityEventQuery(Types: [SecurityEventTypes.AdminRecovered]))).Items);

        string emptyFolder = Path.Combine(_folder, "empty");
        Directory.CreateDirectory(emptyFolder);
        (int none, _, string noneError) = await Run(["anyone@example.com"], folder: emptyFolder);
        Assert.Equal(1, none);
        Assert.Contains("no users yet", noneError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_password_can_not_be_given_as_an_argument_and_usage_errors_exit_2()
    {
        await Seed();

        // A second positional argument (someone putting the password on the command line) is refused, not used.
        (int withPasswordArgument, _, string error) = await Run(["lost@example.com", NewPassword]);
        Assert.Equal(2, withPasswordArgument);
        Assert.DoesNotContain(NewPassword, error.Replace("Unexpected argument '" + NewPassword + "'.", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(2, (await Run([])).Code);
        Assert.Equal(2, (await Run(["lost@example.com", "--nonsense"])).Code);
        Assert.Equal(2, (await Run(["lost@example.com"], promptPassword: _ => null)).Code);
        Assert.Equal(0, (await Run(["--help"])).Code);

        await using ServiceProvider services = Open();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        User admin = (await scope.ServiceProvider.GetRequiredService<IUserStore>().GetUserByEmail("lost@example.com"))!;
        Assert.True(PasswordHasher.Verify("the-old-forgotten-password", admin.PasswordHash));
    }

    [Fact]
    public async Task The_password_may_come_from_the_environment_variable()
    {
        (string adminId, _, _) = await Seed();

        (int code, _, _) = await Run(["lost@example.com"], environment: name => name == ResetAdminCommand.PasswordEnvironmentVariable ? "from-the-environment-1" : null, promptPassword: _ => throw new InvalidOperationException("must not prompt"));

        Assert.Equal(0, code);
        await using ServiceProvider services = Open();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        Assert.True(PasswordHasher.Verify("from-the-environment-1", (await scope.ServiceProvider.GetRequiredService<IUserStore>().GetUserById(adminId))!.PasswordHash));
    }

    [Fact]
    public void Generated_passwords_differ_each_time_and_use_only_unambiguous_characters()
    {
        string[] many = Enumerable.Range(0, 50).Select(_ => AdminRecoveryService.GeneratePassword()).ToArray();

        Assert.Equal(50, many.Distinct().Count());
        Assert.All(many, p =>
        {
            Assert.Equal(24, p.Length);
            Assert.DoesNotContain(p, c => "0O1lI".Contains(c, StringComparison.Ordinal));
            Assert.True(p.All(char.IsLetterOrDigit));
        });
        Assert.All(many, p => Assert.True(p.Length >= PasswordHasher.MinimumLength));
    }

    private ServiceProvider Open()
    {
        return ResetAdminCommand.BuildServices(_folder, out _);
    }

    /// <summary>An admin (lost@example.com) with a live session and a live token, made directly through the stores.</summary>
    private async Task<(string AdminId, string SessionHash, string TokenHash)> Seed(bool deactivate = false)
    {
        await using ServiceProvider services = ResetAdminCommand.BuildServices(_folder, out StorageOptions storage);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await StorageMigrator.Migrate(scope.ServiceProvider.GetRequiredService<DraughtsmanDbContext>(), storage, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        IUserStore users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        User admin = await users.CreateUser("lost@example.com", "Lost", UserRole.Admin, PasswordHasher.Hash("the-old-forgotten-password"), DateTimeOffset.UtcNow);
        await users.CreateSession(admin.Id, "seed-session-hash", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
        await users.CreateApiToken(admin.Id, "seed token", "seed-token-hash", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
        if (deactivate)
        {
            await users.SetUserActive(admin.Id, false);
        }

        return (admin.Id, "seed-session-hash", "seed-token-hash");
    }

    private async Task<(int Code, string Output, string Error)> Run(string[] args, string? folder = null, Func<string, string?>? promptPassword = null, Func<string, string?>? environment = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await ResetAdminCommand.RunAsync(args, TextReader.Null, output, error, folder ?? _folder, promptPassword ?? (_ => NewPassword), environment ?? (_ => null));
        return (code, output.ToString(), error.ToString());
    }
}
