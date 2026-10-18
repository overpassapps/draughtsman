using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Cli;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Diagnostics;
using Draughtsman.Storage;
using Microsoft.EntityFrameworkCore;

namespace Draughtsman.Server.Tests;

/// <summary>
/// <c>draughtsman doctor</c>: each rule in both directions as a plain function of what was found, and the command itself against real data
/// folders, with the exit codes it promises (0 no problems, 1 warnings, 2 failures) shown from a healthy folder, a folder with one mistake in its
/// settings, and a folder whose settings cannot be read. None of it needs a server, a network or a sign-in.
/// </summary>
public class DoctorTests
{
    /// <summary>A fixed instant for the rules that are plain functions of what they are given (nothing here reads a file or a clock).</summary>
    private static readonly DateTimeOffset s_now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The clock for a run of the command over a real folder. It must be the real time: the files such a folder holds carry their real modification
    /// times, and the clock check warns about a file dated more than five minutes after the clock it is given, so a fixed date here turns every run
    /// after that date into a spurious warning (and, once a build is dated later than the fixed day, a failure). Read on each call, never cached.
    /// </summary>
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static DataEntry Entry(string name, long? uid, bool folder = false, string mode = "rw-------")
    {
        return new DataEntry(name, folder, folder ? 0 : 4096, mode, "0600", uid is { } u ? new FileOwner("someone", u, u) : null);
    }

    // ------------------------------------------------------------------------------------------------------ account

    [Fact]
    public void A_server_running_as_root_is_a_warning_in_a_container_and_out_of_one()
    {
        DiagnosticItem container = DoctorChecks.Account(new AccountFacts(10001, "draughtsman", 0, Container: true, Windows: false));
        DiagnosticItem host = DoctorChecks.Account(new AccountFacts(1000, "eric", 0, Container: false, Windows: false));

        Assert.Equal(DiagnosticStatus.Warn, container.Status);
        Assert.Contains("overrode it", container.Summary, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Warn, host.Status);
        Assert.Contains("dedicated, unprivileged account", host.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_doctor_run_as_root_is_noted_and_never_a_warning_by_itself()
    {
        DiagnosticItem withServer = DoctorChecks.Account(new AccountFacts(0, "root", 10001, Container: true, Windows: false));
        DiagnosticItem noServer = DoctorChecks.Account(new AccountFacts(0, "root", null, Container: false, Windows: false));
        DiagnosticItem inContainer = DoctorChecks.Account(new AccountFacts(0, "root", null, Container: true, Windows: false));

        Assert.Equal(DiagnosticStatus.Info, withServer.Status);
        Assert.Contains("compare the owners with uid 10001", withServer.Summary, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Info, noServer.Status);
        Assert.Contains("sudo -u draughtsman draughtsman doctor", noServer.Summary, StringComparison.Ordinal);
        Assert.Contains("docker exec -u 0", inContainer.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_account_passes()
    {
        Assert.Equal(DiagnosticStatus.Pass, DoctorChecks.Account(new AccountFacts(10001, "draughtsman", null, Container: true, Windows: false)).Status);
        Assert.Equal(DiagnosticStatus.Pass, DoctorChecks.Account(new AccountFacts(null, "SERVICE", null, Container: false, Windows: true)).Status);
    }

    [Theory]
    [InlineData(10001L, null, true, false, 10001L)] // the container's own user
    [InlineData(1000L, 995L, false, false, 995L)] // a server found running here wins
    [InlineData(0L, null, true, false, 10001L)] // root in a container: the image's user
    [InlineData(0L, null, false, false, null)] // root on a host with no server to compare: unknown
    [InlineData(0L, 33L, false, false, 33L)]
    [InlineData(null, null, false, true, null)] // Windows has no uids
    public void The_account_the_server_is_expected_to_run_as(long? current, long? server, bool container, bool windows, long? expected)
    {
        Assert.Equal(expected, DoctorChecks.ExpectedServerUid(new AccountFacts(current, "x", server, container, windows)));
    }

    // ------------------------------------------------------------------------------------------------------ ownership

    [Fact]
    public void A_database_owned_by_root_when_the_server_runs_as_the_image_user_fails_and_gives_the_chown()
    {
        DataEntry[] entries = [Entry("data.db", uid: 0), Entry("data.db-wal", uid: 0), Entry("keys", uid: 10001, folder: true)];

        DiagnosticItem item = DoctorChecks.Ownership("/data", new FileOwner("draughtsman", 10001, 10001), "rwx------", "0700", entries, expectedUid: 10001, windows: false);

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Contains("data.db is owned by uid 0", item.Summary, StringComparison.Ordinal);
        Assert.Contains("cannot save anything", item.Summary, StringComparison.Ordinal);
        Assert.Contains("docker exec -u 0 <container> chown -R 10001:10001 /data", item.Fix!.Shell, StringComparison.Ordinal);
        Assert.Null(item.Fix.Yaml);
        Assert.Contains(item.Detail!, line => line.Contains("data.db", StringComparison.Ordinal) && line.Contains("root (uid 0)", StringComparison.Ordinal) || line.Contains("someone (uid 0)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_folder_owned_by_another_account_fails_and_a_stray_untidy_file_only_warns()
    {
        DiagnosticItem folder = DoctorChecks.Ownership("/data", new FileOwner("root", 0, 0), "rwx------", "0700", [Entry("data.db", 10001)], 10001, false);
        DiagnosticItem stray = DoctorChecks.Ownership("/data", new FileOwner("draughtsman", 10001, 10001), "rwx------", "0700", [Entry("data.db", 10001), Entry("notes.txt", 0)], 10001, false);

        Assert.Equal(DiagnosticStatus.Fail, folder.Status);
        Assert.Contains("/data is owned by uid 0", folder.Summary, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Warn, stray.Status);
        Assert.Contains("notes.txt is owned by uid 0", stray.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_open_to_other_accounts_warns_and_a_closed_one_with_the_right_owner_passes()
    {
        DiagnosticItem open = DoctorChecks.Ownership("/data", new FileOwner("d", 10001, 10001), "rwxr-xr-x", "0755", [Entry("data.db", 10001)], 10001, false);
        DiagnosticItem good = DoctorChecks.Ownership("/data", new FileOwner("d", 10001, 10001), "rwx------", "0700", [Entry("data.db", 10001), Entry("keys", 10001, folder: true)], 10001, false);

        Assert.Equal(DiagnosticStatus.Warn, open.Status);
        Assert.Contains("open to other accounts", open.Summary, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Pass, good.Status);
        Assert.Contains("uid 10001", good.Summary, StringComparison.Ordinal);
        Assert.Equal("2", good.Facts!["Entries listed"]);
    }

    [Fact]
    public void Without_an_expected_account_owners_are_listed_but_not_judged_and_windows_has_no_uids()
    {
        DiagnosticItem unknown = DoctorChecks.Ownership("/data", new FileOwner("x", 0, 0), "rwx------", "0700", [Entry("data.db", 0)], expectedUid: null, windows: false);
        DiagnosticItem windows = DoctorChecks.Ownership("C:\\Data", new FileOwner("CORP\\svc", null, null), "-", "-", [new DataEntry("data.db", false, 10, "-", "-", new FileOwner("CORP\\svc", null, null))], null, windows: true);

        Assert.Equal(DiagnosticStatus.Pass, unknown.Status);
        Assert.Equal(DiagnosticStatus.Pass, windows.Status);
        Assert.Contains("ACL", windows.Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------ listening

    private static LoopbackAnswer Nothing(int port = 5180)
    {
        return new LoopbackAnswer(port, false, false, null, null);
    }

    [Fact]
    public void A_loopback_only_server_in_a_container_fails_because_a_published_port_cannot_reach_it()
    {
        DiagnosticItem item = DoctorChecks.Listening(new ListeningFacts(["http://127.0.0.1:5180"], false, AllLoopback: true, Container: true, Nothing()));

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Contains("nothing outside the container can connect", item.Summary, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS=http://0.0.0.0:5180", item.Fix!.Shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_else_about_the_listening_address_is_a_pass_a_warning_or_a_note()
    {
        DiagnosticItem running = DoctorChecks.Listening(new ListeningFacts(["http://0.0.0.0:5180"], true, false, true, new LoopbackAnswer(5180, true, true, "ok", 200)));
        DiagnosticItem someoneElse = DoctorChecks.Listening(new ListeningFacts(["http://127.0.0.1:5180"], false, true, false, new LoopbackAnswer(5180, true, false, null, 404)));
        DiagnosticItem stopped = DoctorChecks.Listening(new ListeningFacts(["http://127.0.0.1:5180"], false, true, false, Nothing()));

        Assert.Equal(DiagnosticStatus.Pass, running.Status);
        Assert.Contains("health: ok", running.Summary, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS", running.Facts!["Set by"], StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Warn, someoneElse.Status);
        Assert.Contains("not a Draughtsman server (HTTP 404)", someoneElse.Summary, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Info, stopped.Status);
        Assert.Contains("which only this machine can reach", stopped.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5180", true, 5180)]
    [InlineData("http://localhost:8080", true, 8080)]
    [InlineData("http://[::1]:5180", true, 5180)]
    [InlineData("http://0.0.0.0:5180", false, 5180)]
    [InlineData("http://+:5180", false, 5180)]
    [InlineData("http://*:9000", false, 9000)]
    [InlineData("http://[::]:5180", false, 5180)]
    [InlineData("http://example.com:80", false, 80)]
    public void Which_bind_addresses_are_loopback_and_which_port_they_name(string url, bool loopback, int port)
    {
        Assert.Equal(loopback, DoctorCommand.IsLoopbackUrl(url));
        Assert.Equal(port, DoctorCommand.PortOf(url));
    }

    // ------------------------------------------------------------------------------------------------------ proxy settings

    [Fact]
    public void Force_https_with_no_known_proxies_warns_and_names_the_shared_throttle()
    {
        var server = new ServerConfig { ForceHttps = true };

        DiagnosticItem item = Assert.Single(DoctorChecks.ProxySettings(server, container: true, observed: null));

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Equal("proxies-missing", item.Id);
        Assert.Contains("twenty failed sign-ins by anyone lock EVERYONE out", item.Summary, StringComparison.Ordinal);
        Assert.Contains("172.17.0.1", item.Summary, StringComparison.Ordinal);
        Assert.Contains("172.17.0.0/16", item.Fix!.Yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_https_with_a_known_proxy_and_a_plain_install_both_pass()
    {
        DiagnosticItem proxied = Assert.Single(DoctorChecks.ProxySettings(new ServerConfig { ForceHttps = true, KnownProxies = ["172.17.0.0/16"] }, true, null));
        DiagnosticItem plain = Assert.Single(DoctorChecks.ProxySettings(new ServerConfig(), false, null));

        Assert.Equal(DiagnosticStatus.Pass, proxied.Status);
        Assert.Contains("1 entry", proxied.Summary, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Pass, plain.Status);
        Assert.Contains("No reverse proxy is configured", plain.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_known_proxy_the_server_cannot_read_is_named()
    {
        IReadOnlyList<DiagnosticItem> items = DoctorChecks.ProxySettings(new ServerConfig { KnownProxies = ["proxy.internal", "172.17.0.1"] }, false, null);

        DiagnosticItem item = Assert.Single(items, i => i.Id == "proxies-unreadable");
        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Contains("proxy.internal", item.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("172.17.0.1 (", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Proxy_headers_the_running_server_saw_from_an_unlisted_peer_are_a_warning_with_the_line_to_add()
    {
        var observed = new ForwardedObservationsReport(s_now.AddDays(-1), s_now, [
            new PeerObservation("172.17.0.1", false, 50, 40, 40, 40, 0, 0, ["https"], s_now.AddHours(-3), s_now),
            new PeerObservation("10.0.0.2", true, 9, 9, 9, 9, 0, 0, ["https"], s_now.AddHours(-3), s_now),
            new PeerObservation("203.0.113.5", false, 3, 0, 0, 0, 0, 0, [], s_now.AddHours(-3), s_now),
        ], 0);

        IReadOnlyList<DiagnosticItem> items = DoctorChecks.ProxySettings(new ServerConfig(), true, observed);

        DiagnosticItem item = Assert.Single(items);
        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Contains("40 of 50 requests from 172.17.0.1", item.Summary, StringComparison.Ordinal);
        Assert.Contains("- \"172.17.0.1\"", item.Fix!.Yaml, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------ clock

    [Fact]
    public void A_clock_before_the_build_is_a_failure_and_one_behind_the_files_is_a_warning_and_a_sane_one_passes()
    {
        DateOnly build = new(2026, 10, 1);

        DiagnosticItem reset = DoctorChecks.Clock(new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), build, null, null);
        DiagnosticItem behind = DoctorChecks.Clock(s_now, build, s_now.AddDays(3), "data.db-wal");
        DiagnosticItem fine = DoctorChecks.Clock(s_now, build, s_now.AddMinutes(-2), "data.db-wal");
        DiagnosticItem justAhead = DoctorChecks.Clock(s_now, build, s_now.AddMinutes(4), "data.db-wal");

        Assert.Equal(DiagnosticStatus.Fail, reset.Status);
        Assert.Contains("before this build was made", reset.Summary, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Warn, behind.Status);
        Assert.Contains("data.db-wal", behind.Summary, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Pass, fine.Status);
        Assert.Equal(DiagnosticStatus.Pass, justAhead.Status); // under five minutes of difference is not a clock gone backwards
    }

    // ------------------------------------------------------------------------------------------------------ exit code

    [Fact]
    public void The_exit_code_is_zero_for_no_problems_one_for_warnings_two_for_failures_and_notes_never_count()
    {
        DiagnosticItem pass = DiagnosticItem.Pass("a", "A", "ok");
        DiagnosticItem info = DiagnosticItem.Info("b", "B", "note");
        DiagnosticItem warn = DiagnosticItem.Warn("c", "C", "hmm");
        DiagnosticItem fail = DiagnosticItem.Fail("d", "D", "no");

        Assert.Equal(0, DoctorChecks.ExitCode([pass, info]));
        Assert.Equal(0, DoctorChecks.ExitCode([]));
        Assert.Equal(1, DoctorChecks.ExitCode([pass, warn, info]));
        Assert.Equal(2, DoctorChecks.ExitCode([pass, warn, fail]));
        Assert.Equal(2, DoctorChecks.ExitCode([fail]));
    }

    // ------------------------------------------------------------------------------------------------------ finding the server

    [Theory]
    [InlineData("dotnet|/app/draughtsman.dll", true)]
    [InlineData("/usr/local/bin/draughtsman", true)]
    [InlineData("dotnet|/app/draughtsman.dll|serve", true)]
    [InlineData("dotnet|/app/draughtsman.dll|--urls=http://0.0.0.0:5180", true)]
    [InlineData("/opt/draughtsman/draughtsman|--DRAUGHTSMAN_DATA=/data", true)]
    [InlineData("dotnet|/app/draughtsman.dll|doctor", false)]
    [InlineData("dotnet|/app/draughtsman.dll|support-bundle|--output|x.zip", false)]
    [InlineData("dotnet|/app/draughtsman.dll|config|check", false)]
    [InlineData("dotnet|/app/draughtsman.dll|--version", false)]
    [InlineData("dotnet|/app/draughtsman.dll|reset-admin|a@b.c", false)]
    [InlineData("/usr/bin/vim|notes.txt", false)]
    [InlineData("dotnet|/app/other.dll", false)]
    public void A_command_line_is_a_server_only_when_it_starts_draughtsman_without_a_command(string commandLine, bool expected)
    {
        Assert.Equal(expected, ServerProcess.IsServerCommandLine(commandLine.Split('|')));
    }

    [Theory]
    [InlineData("Name:\tdraughtsman\nUid:\t10001\t10001\t10001\t10001\nGid:\t10001\n", 10001L)]
    [InlineData("Uid:\t0\t0\t0\t0\n", 0L)]
    [InlineData("Name:\tx\nGid:\t5\n", null)]
    [InlineData("Uid:\tbanana\n", null)]
    public void The_real_user_id_is_read_from_proc_status(string status, long? expected)
    {
        Assert.Equal(expected, ServerProcess.ParseUid(status));
    }

    // ------------------------------------------------------------------------------------------------------ the command, against real folders

    [Fact]
    public async Task A_healthy_folder_exits_zero_and_changes_nothing()
    {
        string folder = HealthyFolder();
        try
        {
            string[] before = Snapshot(folder);

            (int exit, string text) = await Run(folder);

            Assert.Equal(0, exit);
            Assert.Contains("Result: no problems found. Exit code 0.", text, StringComparison.Ordinal);
            Assert.Contains("Draughtsman doctor: version", text, StringComparison.Ordinal);
            Assert.Contains("Data folder and database", text, StringComparison.Ordinal);
            Assert.Contains("Listening address", text, StringComparison.Ordinal);
            Assert.Contains("System clock", text, StringComparison.Ordinal);
            Assert.Contains("Account the server runs as", text, StringComparison.Ordinal);
            Assert.DoesNotContain("WARN", text, StringComparison.Ordinal);
            Assert.DoesNotContain("FAIL", text, StringComparison.Ordinal);
            // One block per section: the database lines (the integrity check, the schema, the migration count) are together, not split by the sections between.
            Assert.Equal(1, text.Split("\nDatabase\n").Length - 1);
            // It read; it wrote nothing that stayed (its probe file is made and removed), and made no write-ahead log.
            Assert.Equal(before, Snapshot(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_warning_alone_exits_one_and_prints_the_fix()
    {
        string folder = HealthyFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, DraughtsmanConfig.FileName), "server:\n  forceHttps: true\n");

            (int exit, string text) = await Run(folder);

            Assert.Equal(1, exit);
            Assert.Contains("WARN", text, StringComparison.Ordinal);
            Assert.DoesNotContain("FAIL", text, StringComparison.Ordinal);
            Assert.Contains("server.forceHttps is on but server.knownProxies is empty", Flat(text), StringComparison.Ordinal);
            Assert.Contains("Fix (draughtsman.yaml):", text, StringComparison.Ordinal);
            Assert.Contains("Result: 1 warning(s), no failures. Exit code 1.", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_settings_file_that_cannot_be_read_exits_two()
    {
        string folder = HealthyFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, DraughtsmanConfig.FileName), "server: [unclosed\n  port: nope\n");

            (int exit, string text) = await Run(folder);

            Assert.Equal(2, exit);
            Assert.Contains("FAIL", text, StringComparison.Ordinal);
            Assert.Contains("draughtsman.yaml cannot be read", Flat(text), StringComparison.Ordinal);
            Assert.Contains("Exit code 2.", text, StringComparison.Ordinal);
            Assert.Contains("describe the defaults, not your file", Flat(text), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [UnixOnlyFact]
    public async Task A_database_the_server_account_does_not_own_exits_two_with_the_chown()
    {
        string folder = HealthyFolder();
        try
        {
            // The server runs as another account than the one that owns these files: the position a root-run restore leaves a container in.
            long me = FileOwners.CurrentUserId()!.Value;
            AccountFacts account = new(me, FileOwners.CurrentUserName(), me + 1, Container: true, Windows: false);

            (int exit, string text) = await Run(folder, OfflineProbes.Make(account: account, now: Now));

            Assert.Equal(2, exit);
            Assert.Contains($"data.db is owned by uid {me}", Flat(text), StringComparison.Ordinal);
            Assert.Contains($"not the server's uid {me + 1}", Flat(text), StringComparison.Ordinal);
            Assert.Contains("chown -R", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_a_warning_not_a_crash()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", "missing-" + Guid.NewGuid().ToString("N"));

        (int exit, string text) = await Run(folder);

        Assert.Equal(1, exit);
        Assert.Contains("does not exist", Flat(text), StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder), "the doctor must not create the folder");
    }

    [Fact]
    public async Task Verbose_prints_the_facts_and_json_prints_the_same_findings_as_data()
    {
        string folder = HealthyFolder();
        try
        {
            (_, string terse) = await Run(folder);
            (_, string verbose) = await Run(folder, extra: "--verbose");
            (int exit, string json) = await Run(folder, extra: "--json");

            Assert.DoesNotContain("        . Data folder:", terse, StringComparison.Ordinal);
            Assert.Contains("        . Data folder: " + folder, verbose, StringComparison.Ordinal);
            Assert.Contains("server.forceHttps: off", verbose, StringComparison.Ordinal);
            JsonNode document = JsonNode.Parse(json)!;
            Assert.Equal(exit, document["exitCode"]!.GetValue<int>());
            Assert.Contains(document["items"]!.AsArray(), item => item!["id"]!.GetValue<string>() == "listening" && item["status"]!.GetValue<string>() == "info");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_verb_runs_through_the_dispatcher_and_a_bad_option_is_the_usage_code_not_a_failure()
    {
        string folder = HealthyFolder();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            // Real probes here would touch the real loopback; the dispatcher path is only checked for what it routes and refuses.
            CliOutcome help = await ServerCli.ExecuteAsync(["doctor", "--help"], stdout, stderr);
            CliOutcome bad = await ServerCli.ExecuteAsync(["doctor", "--frobnicate"], stdout, stderr);

            Assert.False(help.Serve);
            Assert.Equal(0, help.ExitCode);
            Assert.Contains("Usage: draughtsman doctor", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(DoctorCommand.UsageExitCode, bad.ExitCode);
            Assert.Contains("unknown option '--frobnicate'", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private static async Task<(int Exit, string Text)> Run(string folder, DoctorProbes? probes = null, string? extra = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        string[] args = extra is null ? [] : [extra];
        int exit = await DoctorCommand.RunAsync(args, stdout, stderr, folder, probes ?? OfflineProbes.Make(now: Now));
        Assert.Equal(string.Empty, stderr.ToString());
        return (exit, stdout.ToString());
    }

    /// <summary>
    /// Every run of whitespace as one space. The report wraps a finding's sentence at 100 columns and where it breaks depends on how long the temp path in it
    /// is (a short /tmp path and this Mac's long one break it in different places), so a test asking whether a phrase was said reads it through this.
    /// </summary>
    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    /// <summary>A folder as a server leaves it after a clean first start: private, with a migrated database and nothing else.</summary>
    private static string HealthyFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        StorageOptions storage = StorageOptions.FromConfig("sqlite", string.Empty, folder);
        using (DraughtsmanDbContext db = StorageSetup.CreateContext(storage))
        {
            db.Database.Migrate();
        }

        // The pooled connection keeps a write-ahead log alive after the context is gone; a stopped server has none.
        SqlitePools.Release(folder);
        return folder;
    }

    private static string[] Snapshot(string folder)
    {
        return [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p => Path.GetRelativePath(folder, p) + ":" + (File.Exists(p) ? new FileInfo(p).Length.ToString(System.Globalization.CultureInfo.InvariantCulture) : "d") + ":" + File.GetLastWriteTimeUtc(p).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture))];
    }
}
