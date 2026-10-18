using System.Net;
using System.Net.Http.Json;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Auth.Recovery;
using Draughtsman.Server.Hosting;
using Draughtsman.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// D1 of the Windows install proof: a Windows service has no console and its only log, the Application event log, is
/// readable by every local user, so the first-run setup code goes to a restricted file and the log says where. The rules:
/// the code is in that file and in no log line, the file is owner/Administrator-only from its first byte, it is gone the
/// moment the first administrator exists, and <c>draughtsman first-run-code</c> reads it for an elevated shell.
/// </summary>
public sealed class SetupCodeFileTests : IDisposable
{
    private const string Code = "ABCDEFGHJK";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public SetupCodeFileTests()
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
    public void The_file_holds_the_code_and_a_new_code_replaces_the_old_one()
    {
        var file = new SetupCodeFile(_folder);

        file.Write(Code);
        Assert.Equal(Code, file.Read());
        file.Write("ZZZZZZZZZZ");

        Assert.Equal("ZZZZZZZZZZ", file.Read());
        Assert.Equal(Path.Combine(_folder, "setup-code.txt"), file.Path);
    }

    [Fact]
    public void A_missing_file_reads_as_null_and_deleting_it_twice_is_harmless()
    {
        var file = new SetupCodeFile(_folder);

        Assert.Null(file.Read());
        Assert.False(file.Delete());
        file.Write(Code);
        Assert.True(file.Delete());
        Assert.False(file.Delete());
        Assert.Null(file.Read());
    }

    [Fact]
    public void On_Unix_the_file_is_readable_by_its_owner_only_from_the_moment_it_exists()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows protection is the ACL below, applied at creation by the same call.
        }

        var file = new SetupCodeFile(_folder);
        file.Write(Code);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file.Path));
    }

    [Fact]
    public void The_Windows_ACL_grants_SYSTEM_and_Administrators_only_and_inherits_nothing()
    {
        string sddl = SetupCodeFile.WindowsAclSddl;

        Assert.StartsWith("D:P", sddl, StringComparison.Ordinal); // protected: nothing flows down from the data folder
        Assert.Contains("(A;;FA;;;SY)", sddl, StringComparison.Ordinal);
        Assert.Contains("(A;;FA;;;BA)", sddl, StringComparison.Ordinal);
        Assert.Equal(2, sddl.Count(c => c == '('));
        foreach (string broad in new[] { ";;;BU)", ";;;WD)", ";;;AU)", ";;;IU)", ";;;LS)", ";;;NS)", ";;;OW)", ";;;CO)" })
        {
            Assert.DoesNotContain(broad, sddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_Windows_service_logs_where_the_code_is_never_the_code()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([provider]);
        ILogger logger = factory.CreateLogger("test");

        SetupCodeAnnouncer.Announce(logger, HostKind.WindowsService, _folder, Code);

        Assert.Equal(Code, new SetupCodeFile(_folder).Read());
        string logged = string.Join('\n', provider.Lines.Select(l => l.Message));
        Assert.DoesNotContain(Code, logged, StringComparison.Ordinal);
        Assert.Contains($"The setup code is stored at {Path.Combine(_folder, "setup-code.txt")}; read it as an Administrator", logged, StringComparison.Ordinal);
        Assert.Contains("first-run-code", logged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HostKind.Console)]
    [InlineData(HostKind.Container)]
    [InlineData(HostKind.Systemd)]
    [InlineData(HostKind.Launchd)]
    public void Every_other_host_still_prints_the_code_to_its_log_and_writes_no_file(HostKind host)
    {
        var provider = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([provider]);

        SetupCodeAnnouncer.Announce(factory.CreateLogger("test"), host, _folder, Code);

        Assert.Contains(provider.Lines, line => line.Message.Contains($"First-run setup code: {Code}", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_folder, "setup-code.txt")));
    }

    [Fact]
    public void A_file_that_cannot_be_written_is_reported_without_falling_back_to_logging_the_code()
    {
        // The data folder path is a regular file, so the code file cannot be created under it.
        string blocker = Path.Combine(_folder, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var provider = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([provider]);

        SetupCodeAnnouncer.Announce(factory.CreateLogger("test"), HostKind.WindowsService, blocker, Code);

        Assert.Contains(provider.Lines, line => line.Level == LogLevel.Error && line.Message.Contains("could not be written", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Lines, line => line.Message.Contains(Code, StringComparison.Ordinal));
    }

    [Fact]
    public void Consuming_the_code_deletes_the_file_and_a_gate_without_a_file_still_works()
    {
        var file = new SetupCodeFile(_folder);
        var gate = new SetupCodeGate(file);
        file.Write(gate.Issue());
        Assert.NotNull(file.Read());

        gate.Consume();

        Assert.Null(file.Read());
        var bare = new SetupCodeGate();
        Assert.True(bare.Verify(bare.Issue()));
        bare.Consume();
        Assert.False(bare.Verify("anything"));
    }

    [Fact]
    public async Task Completing_first_run_over_HTTP_deletes_the_setup_code_file()
    {
        using var factory = new DraughtsmanServerFactory();
        string dataFolder = factory.Services.GetRequiredService<DataFolder>().Path;
        string setupCode = factory.Services.GetRequiredService<SetupCodeGate>().Issue();
        var file = new SetupCodeFile(dataFolder);
        file.Write(setupCode);
        HttpClient client = factory.CreateClient();

        // A wrong code changes nothing: the file stays until an administrator really exists.
        HttpResponseMessage wrong = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "a@example.com", displayName = "A", password = "test-password-1234", setupCode = "WRONGWRONG" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(setupCode, file.Read());

        HttpResponseMessage right = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "a@example.com", displayName = "A", password = "test-password-1234", setupCode });
        Assert.Equal(HttpStatusCode.Created, right.StatusCode);

        Assert.Null(file.Read());
    }

    // --- draughtsman first-run-code ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_verb_prints_the_code_from_the_file_while_there_is_no_administrator()
    {
        new SetupCodeFile(_folder).Write(Code);

        (int exit, string output, string error) = await RunVerb();

        Assert.Equal(0, exit);
        Assert.Contains($"First-run setup code: {Code}", output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public async Task The_verb_refuses_once_an_administrator_exists_even_if_a_stale_file_is_still_there()
    {
        await SeedAdmin();
        new SetupCodeFile(_folder).Write(Code);

        (int exit, string output, string error) = await RunVerb();

        Assert.Equal(1, exit);
        Assert.DoesNotContain(Code, output + error, StringComparison.Ordinal);
        Assert.Contains("already has an administrator", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_says_so_when_there_is_no_file_and_when_the_folder_is_missing()
    {
        (int exit, _, string error) = await RunVerb();
        Assert.Equal(1, exit);
        Assert.Contains("There is no setup code at", error, StringComparison.Ordinal);

        (int missingExit, _, string missingError) = await RunVerb(Path.Combine(_folder, "nowhere"));
        Assert.Equal(1, missingExit);
        Assert.Contains("does not exist", missingError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_explains_a_file_this_account_cannot_read_and_does_not_print_it()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // an ACL-denied read is proved on the Windows VM; root reads anything
        }

        var file = new SetupCodeFile(_folder);
        file.Write(Code);
        File.SetUnixFileMode(file.Path, UnixFileMode.None);

        (int exit, string output, string error) = await RunVerb();

        Assert.Equal(1, exit);
        Assert.DoesNotContain(Code, output + error, StringComparison.Ordinal);
        Assert.Contains("cannot be read by this account", error, StringComparison.Ordinal);
        Assert.Contains("elevated (Administrator) prompt", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_falls_back_to_the_service_default_folder_only_when_none_was_named_and_the_usual_one_has_no_file()
    {
        string serviceFolder = Path.Combine(_folder, "service-data");
        Directory.CreateDirectory(serviceFolder);
        new SetupCodeFile(serviceFolder).Write(Code);
        var usual = new StringWriter();

        // No folder named anywhere: the service default is tried (the usual folder, beside the test binary, has no file).
        var output = new StringWriter();
        int exit = await FirstRunCodeCommand.RunAsync([], output, usual, serviceDefaultFolder: serviceFolder);
        Assert.Equal(0, exit);
        Assert.Contains(Code, output.ToString(), StringComparison.Ordinal);

        // A folder named on the command line is never second-guessed: it has no file, so that is the answer.
        string named = Path.Combine(_folder, "named");
        Directory.CreateDirectory(named);
        var namedError = new StringWriter();
        int namedExit = await FirstRunCodeCommand.RunAsync([$"--DRAUGHTSMAN_DATA={named}"], new StringWriter(), namedError, serviceDefaultFolder: serviceFolder);
        Assert.Equal(1, namedExit);
        Assert.Contains("There is no setup code at", namedError.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("space")]
    [InlineData("equals")]
    public async Task The_verb_takes_the_data_folder_in_the_services_own_spelling_and_in_the_equals_spelling(string form)
    {
        new SetupCodeFile(_folder).Write(Code);
        string[] args = form == "space" ? ["--DRAUGHTSMAN_DATA", _folder] : [$"--DRAUGHTSMAN_DATA={_folder}"];
        var output = new StringWriter();
        var error = new StringWriter();

        int exit = await FirstRunCodeCommand.RunAsync(args, output, error);

        Assert.Equal(0, exit);
        Assert.Contains(Code, output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task The_verb_refuses_an_option_it_does_not_know_and_a_data_option_with_no_value()
    {
        var error = new StringWriter();
        Assert.Equal(2, await FirstRunCodeCommand.RunAsync(["--nonsense"], new StringWriter(), error));
        Assert.Contains("Unexpected argument", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await FirstRunCodeCommand.RunAsync(["--DRAUGHTSMAN_DATA"], new StringWriter(), new StringWriter()));
    }

    [Fact]
    public async Task With_no_folder_named_and_no_file_anywhere_the_service_default_folder_that_exists_gives_the_better_answer()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DRAUGHTSMAN_DATA")))
        {
            return; // a named folder is never second-guessed, which is the point of the test above
        }

        // The usual folder (data/ beside the test binary) does not exist; the service default does, holds an administrator and no file.
        string serviceFolder = Path.Combine(_folder, "service-data");
        Directory.CreateDirectory(serviceFolder);
        await SeedAdmin(serviceFolder);
        var error = new StringWriter();

        int exit = await FirstRunCodeCommand.RunAsync([], new StringWriter(), error, serviceDefaultFolder: serviceFolder);

        Assert.Equal(1, exit);
        Assert.Contains("already has an administrator", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_is_registered_in_the_dispatcher_and_the_help_text()
    {
        new SetupCodeFile(_folder).Write(Code);
        var output = new StringWriter();

        Cli.CliOutcome outcome = await Cli.ServerCli.ExecuteAsync([$"first-run-code", $"--DRAUGHTSMAN_DATA={_folder}"], output, new StringWriter());

        Assert.False(outcome.Serve);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains(Code, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("draughtsman first-run-code", Cli.ServerCli.HelpText, StringComparison.Ordinal);
        Assert.Equal(Cli.CliVerb.FirstRunCode, Cli.CliCommand.Parse(["first-run-code"]).Verb);
    }

    private async Task<(int Code, string Output, string Error)> RunVerb(string? folder = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await FirstRunCodeCommand.RunAsync([], output, error, folder ?? _folder);
        return (code, output.ToString(), error.ToString());
    }

    private async Task SeedAdmin(string? folder = null)
    {
        await using ServiceProvider services = ResetAdminCommand.BuildServices(folder ?? _folder, out StorageOptions storage);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await StorageMigrator.Migrate(scope.ServiceProvider.GetRequiredService<DraughtsmanDbContext>(), storage, NullLogger.Instance);
        await scope.ServiceProvider.GetRequiredService<IUserStore>().CreateUser("admin@example.com", "Admin", UserRole.Admin, PasswordHasher.Hash("a-long-enough-password"), DateTimeOffset.UtcNow);
    }
}
