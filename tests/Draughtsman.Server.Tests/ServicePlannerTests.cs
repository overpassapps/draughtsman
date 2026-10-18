using System.Xml.Linq;
using Draughtsman.Server.Cli;

namespace Draughtsman.Server.Tests;

public class ServicePlannerTests : IDisposable
{
    private const string Exe = "/opt/draughtsman/draughtsman";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public ServicePlannerTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private static ServiceOptions Install(ServiceTarget platform, string? data = null, string? user = null, string name = "draughtsman", string exe = Exe)
    {
        return new ServiceOptions { Action = ServiceAction.Install, Platform = platform, DryRun = true, DataFolder = data, User = user, Name = name, Executable = exe };
    }

    [Fact]
    public void The_systemd_unit_runs_serve_with_an_explicit_data_folder_and_a_dynamic_user()
    {
        string unit = ServicePlanner.RenderSystemdUnit("draughtsman", Exe, "/var/lib/draughtsman", user: null);

        Assert.Contains("ExecStart=\"/opt/draughtsman/draughtsman\" serve\n", unit);
        Assert.Contains("Environment=\"DRAUGHTSMAN_DATA=/var/lib/draughtsman\"\n", unit);
        Assert.Contains("Environment=\"DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/cache/draughtsman\"\n", unit);
        Assert.Contains("WorkingDirectory=/opt/draughtsman\n", unit);
        Assert.Contains("DynamicUser=yes\n", unit);
        Assert.Contains("StateDirectory=draughtsman\n", unit);
        Assert.Contains("CacheDirectory=draughtsman\n", unit);
        Assert.Contains("Restart=on-failure\n", unit);
        Assert.Contains("WantedBy=multi-user.target\n", unit);
        Assert.DoesNotContain("User=", unit.Replace("DynamicUser=", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("ReadWritePaths", unit);
    }

    [Fact]
    public void A_named_account_replaces_the_dynamic_user()
    {
        string unit = ServicePlanner.RenderSystemdUnit("draughtsman", Exe, "/var/lib/draughtsman", user: "svc-draughtsman");

        Assert.Contains("User=svc-draughtsman\n", unit);
        Assert.DoesNotContain("DynamicUser", unit);
    }

    [Fact]
    public void A_data_folder_of_its_own_needs_an_account_and_is_made_writable_for_exactly_that_folder()
    {
        ServicePlanException error = Assert.Throws<ServicePlanException>(() => ServicePlanner.RenderSystemdUnit("draughtsman", Exe, "/srv/draughtsman", user: null));
        Assert.Contains("--user", error.Message);

        string unit = ServicePlanner.RenderSystemdUnit("draughtsman", Exe, "/srv/draughtsman", user: "svc");
        Assert.Contains("ReadWritePaths=/srv/draughtsman\n", unit);
        Assert.DoesNotContain("StateDirectory", unit);
    }

    [Fact]
    public void Systemd_specials_in_a_path_are_escaped_so_they_are_not_expanded()
    {
        string unit = ServicePlanner.RenderSystemdUnit("draughtsman", "/opt/my \"app\"/100%/dra$ughtsman", "/var/lib/draughtsman", user: null);

        Assert.Contains("ExecStart=\"/opt/my \\\"app\\\"/100%%/dra$$ughtsman\" serve\n", unit);
        Assert.Contains("WorkingDirectory=/opt/my \"app\"/100%%\n", unit);
    }

    [Fact]
    public void The_launchd_plist_round_trips_awkward_paths_through_real_xml()
    {
        string plist = ServicePlanner.RenderLaunchdPlist("draughtsman", "/Applications/A&B <Draughtsman>/draughtsman", "/Library/Application Support/Draughtsman", user: null);

        XElement dict = XDocument.Parse(plist).Root!.Element("dict")!;
        Dictionary<string, XElement> entries = Pairs(dict);
        Assert.Equal("uk.co.overpass.draughtsman", entries["Label"].Value);
        Assert.Equal(["/Applications/A&B <Draughtsman>/draughtsman", "serve"], entries["ProgramArguments"].Elements("string").Select(e => e.Value));
        Assert.Equal("/Applications/A&B <Draughtsman>", entries["WorkingDirectory"].Value);
        Dictionary<string, XElement> environment = Pairs(entries["EnvironmentVariables"]);
        Assert.Equal("/Library/Application Support/Draughtsman", environment["DRAUGHTSMAN_DATA"].Value);
        Assert.Equal("/Library/Application Support/Draughtsman/.bundle", environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"].Value);
        Assert.Equal("true", entries["RunAtLoad"].Name.LocalName);
        Assert.Equal("true", entries["KeepAlive"].Name.LocalName);
        Assert.False(entries.ContainsKey("UserName"));
    }

    [Fact]
    public void The_launchd_plist_names_the_account_when_one_is_given()
    {
        string plist = ServicePlanner.RenderLaunchdPlist("draughtsman", "/opt/draughtsman/draughtsman", "/var/draughtsman", user: "_draughtsman");

        Assert.Equal("_draughtsman", Pairs(XDocument.Parse(plist).Root!.Element("dict")!)["UserName"].Value);
    }

    [Fact]
    public void The_windows_service_command_quotes_the_executable_and_passes_the_data_folder_as_host_configuration()
    {
        IReadOnlyList<string> arguments = ServicePlanner.WindowsCreateArguments("draughtsman", "C:\\Program Files\\Draughtsman\\draughtsman.exe", "C:\\ProgramData\\Draughtsman\\", "NT AUTHORITY\\LocalService");

        Assert.Equal(
            [
                "create", "draughtsman",
                "binPath=", "\"C:\\Program Files\\Draughtsman\\draughtsman.exe\" serve --DRAUGHTSMAN_DATA \"C:\\ProgramData\\Draughtsman\"",
                "start=", "auto",
                "obj=", "NT AUTHORITY\\LocalService",
                "DisplayName=", "Draughtsman diagram server",
            ],
            arguments);
    }

    [Fact]
    public void Install_orders_the_steps_write_then_reload_then_enable()
    {
        ServicePlan plan = ServicePlanner.Plan(Install(ServiceTarget.Systemd), Exe);

        Assert.Collection(
            plan.Steps,
            step => Assert.Equal("/etc/systemd/system/draughtsman.service", Assert.IsType<ServiceStep.WriteFile>(step).Path),
            step => Assert.Equal(["daemon-reload"], Assert.IsType<ServiceStep.Run>(step).Arguments),
            step => Assert.Equal(["enable", "--now", "draughtsman.service"], Assert.IsType<ServiceStep.Run>(step).Arguments));
    }

    [Fact]
    public void The_default_data_folder_is_the_platforms_own_not_the_working_directory()
    {
        Assert.Equal("/var/lib/draughtsman", ServicePlanner.DefaultDataFolder(ServiceTarget.Systemd, "draughtsman"));
        Assert.Equal("/Library/Application Support/Draughtsman", ServicePlanner.DefaultDataFolder(ServiceTarget.Launchd, "draughtsman"));
        Assert.EndsWith("\\Draughtsman", ServicePlanner.DefaultDataFolder(ServiceTarget.WindowsService, "draughtsman"));
        Assert.Equal("/var/lib/second", ServicePlanner.DefaultDataFolder(ServiceTarget.Systemd, "second"));
    }

    [Theory]
    [InlineData(ServiceTarget.Systemd)]
    [InlineData(ServiceTarget.Launchd)]
    [InlineData(ServiceTarget.WindowsService)]
    public void Uninstall_removes_the_service_definition_and_never_the_data_folder(ServiceTarget target)
    {
        ServicePlan plan = ServicePlanner.Plan(new ServiceOptions { Action = ServiceAction.Uninstall, Platform = target, DryRun = true }, Exe);

        Assert.DoesNotContain(plan.Steps, step => step is ServiceStep.EnsureDirectory or ServiceStep.WriteFile);
        Assert.All(plan.Steps.OfType<ServiceStep.DeleteFile>(), file => Assert.DoesNotContain("data", file.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Steps, step => step is ServiceStep.Run { IgnoreFailure: true });
        Assert.Contains(plan.Notes, note => note.Contains("data folder is left", StringComparison.Ordinal));
    }

    [Fact]
    public void Uninstall_stops_the_service_before_deleting_its_unit()
    {
        ServicePlan plan = ServicePlanner.Plan(new ServiceOptions { Action = ServiceAction.Uninstall, Platform = ServiceTarget.Systemd, DryRun = true }, Exe);

        int stop = plan.Steps.ToList().FindIndex(step => step is ServiceStep.Run { Arguments: ["disable", "--now", ..] });
        int delete = plan.Steps.ToList().FindIndex(step => step is ServiceStep.DeleteFile);
        Assert.InRange(stop, 0, delete - 1);
    }

    [Fact]
    public void Options_parse_every_flag()
    {
        (ServiceOptions? options, string? error) = ServiceOptions.Parse(["install", "--name", "dra2", "--data", "/srv/d", "--user", "svc", "--exe", "/x/draughtsman", "--dry-run", "--platform", "macos"]);

        Assert.Null(error);
        Assert.Equal(new ServiceOptions { Action = ServiceAction.Install, Name = "dra2", DataFolder = "/srv/d", User = "svc", Executable = "/x/draughtsman", DryRun = true, Platform = ServiceTarget.Launchd }, options);
    }

    [Theory]
    [InlineData("install", "--platform", "systemd")]
    [InlineData("install", "--name", "has space")]
    [InlineData("install", "--name")]
    [InlineData("install", "--data", "--dry-run")]
    [InlineData("install", "--bogus")]
    [InlineData("reinstall")]
    [InlineData("install", "--dry-run", "--platform", "amiga")]
    public void Bad_service_arguments_are_reported_not_acted_on(params string[] args)
    {
        (ServiceOptions? options, string? error) = ServiceOptions.Parse(args);

        Assert.Null(options);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_service_command_needs_an_action()
    {
        Assert.NotNull(ServiceOptions.Parse([]).Error);
    }

    [Fact]
    public async Task A_dry_run_writes_nothing_and_a_real_run_writes_the_file()
    {
        string target = Path.Combine(_folder, "unit.service");
        var plan = new ServicePlan(ServiceTarget.Systemd, ServiceAction.Install, "t", [new ServiceStep.WriteFile(target, "[Unit]\n")], []);
        var stdout = new StringWriter();

        int dry = await ServiceExecutor.RunAsync(plan, dryRun: true, stdout, new StringWriter());

        Assert.Equal(0, dry);
        Assert.False(File.Exists(target));
        Assert.Contains("[Unit]", stdout.ToString());

        int real = await ServiceExecutor.RunAsync(plan, dryRun: false, new StringWriter(), new StringWriter());

        Assert.Equal(0, real);
        Assert.Equal("[Unit]\n", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task A_failing_step_stops_the_plan_and_fails_the_command()
    {
        string marker = Path.Combine(_folder, "after.txt");
        string failing = OperatingSystem.IsWindows() ? "cmd.exe" : "false";
        IReadOnlyList<string> failingArguments = OperatingSystem.IsWindows() ? ["/c", "exit", "1"] : [];
        var plan = new ServicePlan(
            ServiceTarget.Systemd,
            ServiceAction.Install,
            "t",
            [new ServiceStep.Run(failing, failingArguments), new ServiceStep.WriteFile(marker, "x")],
            []);
        var stderr = new StringWriter();

        int exit = await ServiceExecutor.RunAsync(plan, dryRun: false, new StringWriter(), stderr);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(marker));
        Assert.Contains("failed", stderr.ToString());
    }

    [Fact]
    public async Task The_service_command_refuses_to_run_under_the_dotnet_host_and_exits_2()
    {
        var stderr = new StringWriter();

        CliOutcome outcome = await ServerCli.ExecuteAsync(["service", "install", "--dry-run", "--exe", "/usr/local/bin/dotnet"], new StringWriter(), stderr);

        Assert.False(outcome.Serve);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains("published", stderr.ToString());
    }

    [Fact]
    public async Task A_dry_run_through_the_dispatcher_prints_the_plist_for_the_launchd_target()
    {
        var stdout = new StringWriter();

        CliOutcome outcome = await ServerCli.ExecuteAsync(["service", "install", "--dry-run", "--platform", "launchd", "--exe", "/opt/draughtsman/draughtsman"], stdout, new StringWriter());

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("<string>uk.co.overpass.draughtsman</string>", stdout.ToString());
        Assert.Contains("launchctl bootstrap system /Library/LaunchDaemons/uk.co.overpass.draughtsman.plist", stdout.ToString());
    }

    // --- Windows install fixes (D1, D2, D6, D7, D8 of the Windows install proof) ----------------------------------------

    private const string WindowsExe = "C:\\Program Files\\Draughtsman\\draughtsman.exe";

    [Fact]
    public void The_windows_data_folder_is_made_private_to_SYSTEM_the_Administrators_and_the_service_in_one_icacls_call()
    {
        IReadOnlyList<string> acl = ServicePlanner.WindowsDataFolderAclArguments("C:\\ProgramData\\Draughtsman\\", "NT AUTHORITY\\LocalService");

        Assert.Equal(
            [
                "C:\\ProgramData\\Draughtsman", "/inheritance:r",
                "/remove:g", "*S-1-5-32-545", "*S-1-1-0", "*S-1-5-11",
                "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-19:(OI)(CI)F",
            ],
            acl);
        // Nothing that would leave a broad account in place: no plain /grant on top of inherited rights, no Users/Everyone grant.
        Assert.DoesNotContain("/grant", acl);
        Assert.DoesNotContain(acl, a => a.StartsWith("*S-1-5-32-545:", StringComparison.Ordinal) || a.StartsWith("*S-1-1-0:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("NT AUTHORITY\\NetworkService", "*S-1-5-20:(OI)(CI)F")]
    [InlineData("LocalSystem", null)]
    [InlineData("DOMAIN\\svc-draughtsman", "DOMAIN\\svc-draughtsman:(OI)(CI)F")]
    public void The_service_account_is_given_by_SID_when_it_is_built_in_and_never_listed_twice(string account, string? expectedGrant)
    {
        IReadOnlyList<string> acl = ServicePlanner.WindowsDataFolderAclArguments("D:\\data", account);

        string[] grants = [.. acl.SkipWhile(a => a != "/grant:r").Skip(1)];
        Assert.Contains("*S-1-5-18:(OI)(CI)F", grants);
        Assert.Contains("*S-1-5-32-544:(OI)(CI)F", grants);
        Assert.Equal(expectedGrant is null ? 2 : 3, grants.Length);
        if (expectedGrant is not null)
        {
            Assert.Contains(expectedGrant, grants);
        }
    }

    [Fact]
    public void A_windows_install_sets_the_ACL_and_registers_the_event_source_before_the_service_exists()
    {
        ServicePlan plan = ServicePlanner.Plan(Install(ServiceTarget.WindowsService, exe: WindowsExe), WindowsExe);

        int directory = plan.Steps.ToList().FindIndex(step => step is ServiceStep.EnsureDirectory);
        int acl = plan.Steps.ToList().FindIndex(step => step is ServiceStep.Run { FileName: "icacls" });
        int source = plan.Steps.ToList().FindIndex(step => step is ServiceStep.RegisterEventSource { Source: "draughtsman" });
        int create = plan.Steps.ToList().FindIndex(step => step is ServiceStep.Run { Arguments: ["create", ..] });
        int start = plan.Steps.ToList().FindIndex(step => step is ServiceStep.Run { Arguments: ["start", ..] });
        Assert.InRange(directory, 0, acl - 1);
        Assert.InRange(acl, 0, create - 1);
        Assert.InRange(source, 0, create - 1);
        Assert.InRange(start, create + 1, plan.Steps.Count);
        string notes = string.Join('\n', plan.Notes);
        Assert.Contains("first-run-code", notes);
        Assert.DoesNotContain("look for it there", notes);
    }

    [Fact]
    public void The_display_name_differs_from_the_service_name_by_more_than_case()
    {
        // The service manager shows the name, not the display name, when they are equal ignoring case (found on real Windows).
        Assert.False(string.Equals("draughtsman", ServicePlanner.WindowsDisplayName("draughtsman"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Draughtsman diagram server (second)", ServicePlanner.WindowsDisplayName("second"));
        Assert.False(string.Equals("second", ServicePlanner.WindowsDisplayName("second"), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("C:\\Users\\Sam\\draughtsman-verify\\draughtsman.exe", "NT AUTHORITY\\LocalService", true)]
    [InlineData("c:/users/sam/Downloads/draughtsman-0.1.0-win-x64/draughtsman.exe", "LocalService", true)]
    [InlineData("C:\\Users\\Sam\\x\\draughtsman.exe", "NT AUTHORITY\\NetworkService", true)]
    [InlineData("C:\\Program Files\\Draughtsman\\draughtsman.exe", "NT AUTHORITY\\LocalService", false)]
    [InlineData("D:\\Apps\\Draughtsman\\draughtsman.exe", "NT AUTHORITY\\LocalService", false)]
    [InlineData("C:\\Users\\Public\\Draughtsman\\draughtsman.exe", "NT AUTHORITY\\LocalService", false)]
    [InlineData("C:\\Users\\Sam\\x\\draughtsman.exe", "DOMAIN\\Sam", false)]
    public void An_executable_in_a_user_profile_is_refused_for_a_built_in_service_account_before_anything_changes(string executable, string account, bool refused)
    {
        string? problem = ServicePreflight.ExecutableUnreadableByService(executable, account, profilesRoot: "C:\\Users");

        Assert.Equal(refused, problem is not null);
        if (refused)
        {
            Assert.Contains("user profile folder", problem);
            Assert.Contains("C:\\Program Files\\Draughtsman", problem);
            Assert.Contains("Nothing was changed", problem);
        }
    }

    [Fact]
    public void The_windows_plan_throws_the_preflight_message_for_a_profile_path_and_plans_normally_elsewhere()
    {
        ServicePlanException refused = Assert.Throws<ServicePlanException>(() => ServicePlanner.Plan(Install(ServiceTarget.WindowsService, exe: "C:\\Users\\Sam\\dv\\draughtsman.exe"), "C:\\Users\\Sam\\dv\\draughtsman.exe"));
        Assert.Contains("Access is denied", refused.Message);

        Assert.NotEmpty(ServicePlanner.Plan(Install(ServiceTarget.WindowsService, exe: WindowsExe), WindowsExe).Steps);
    }

    [Fact]
    public void Uninstalling_a_stopped_windows_service_does_not_report_the_stop_that_was_not_needed()
    {
        ServicePlan plan = ServicePlanner.Plan(new ServiceOptions { Action = ServiceAction.Uninstall, Platform = ServiceTarget.WindowsService, DryRun = true }, WindowsExe);

        var stop = Assert.IsType<ServiceStep.Run>(plan.Steps[0]);
        Assert.Equal(["stop", "draughtsman"], stop.Arguments);
        Assert.True(stop.IgnoreFailure);
        Assert.Contains(1062, stop.QuietExitCodes!);
        Assert.False(Assert.IsType<ServiceStep.Run>(plan.Steps[1]).IgnoreFailure);
    }

    [Fact]
    public async Task A_quiet_exit_code_swallows_the_programs_output_and_any_other_failure_is_still_shown_and_still_fails()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // sh is the stand-in program; the real sc.exe case is proved on the Windows VM
        }

        IReadOnlyList<string> Script(int exit) => ["-c", $"echo '[SC] ControlService FAILED 1062: The service has not been started.' >&2; exit {exit}"];
        ServicePlan Plan(ServiceStep step) => new(ServiceTarget.Systemd, ServiceAction.Uninstall, "t", [step], []);

        var quietOut = new StringWriter();
        var quietErr = new StringWriter();
        int quiet = await ServiceExecutor.RunAsync(Plan(new ServiceStep.Run("sh", Script(5), IgnoreFailure: true, QuietExitCodes: [5])), dryRun: false, quietOut, quietErr);
        Assert.Equal(0, quiet);
        Assert.Equal(string.Empty, quietOut.ToString() + quietErr.ToString());

        var otherErr = new StringWriter();
        int other = await ServiceExecutor.RunAsync(Plan(new ServiceStep.Run("sh", Script(7), IgnoreFailure: true, QuietExitCodes: [5])), dryRun: false, new StringWriter(), otherErr);
        Assert.Equal(0, other); // best-effort: it still does not fail the command
        Assert.Contains("FAILED 1062", otherErr.ToString());

        var failedErr = new StringWriter();
        int failed = await ServiceExecutor.RunAsync(Plan(new ServiceStep.Run("sh", Script(7), QuietExitCodes: [5])), dryRun: false, new StringWriter(), failedErr);
        Assert.Equal(1, failed);
    }

    private static Dictionary<string, XElement> Pairs(XElement dict)
    {
        var result = new Dictionary<string, XElement>();
        XElement[] children = dict.Elements().ToArray();
        for (int i = 0; i + 1 < children.Length; i += 2)
        {
            result[children[i].Value] = children[i + 1];
        }

        return result;
    }
}
