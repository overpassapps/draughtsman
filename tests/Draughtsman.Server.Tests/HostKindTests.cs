using Draughtsman.Server.Hosting;

namespace Draughtsman.Server.Tests;

/// <summary>Which "where is the setup code" hint the first-run page opens (PR-02) follows from the environment the process was started in.</summary>
public class HostKindTests
{
    private static HostKind Detect(Dictionary<string, string> environment, bool dockerEnv = false, bool windowsService = false, bool macOs = false)
    {
        return HostKinds.Detect(name => environment.GetValueOrDefault(name), path => dockerEnv && path == "/.dockerenv", windowsService, macOs);
    }

    [Fact]
    public void A_bare_environment_is_a_console_run()
    {
        Assert.Equal(HostKind.Console, Detect([]));
    }

    [Fact]
    public void The_dotnet_image_variable_or_a_dockerenv_file_means_a_container()
    {
        Assert.Equal(HostKind.Container, Detect(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" }));
        Assert.Equal(HostKind.Container, Detect([], dockerEnv: true));
        Assert.Equal(HostKind.Console, Detect(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "false" }));
    }

    [Fact]
    public void Systemd_is_recognised_by_its_invocation_id()
    {
        Assert.Equal(HostKind.Systemd, Detect(new() { ["INVOCATION_ID"] = "0123456789abcdef" }));
    }

    [Fact]
    public void Launchd_needs_a_job_label_and_is_not_fooled_by_a_shell_or_a_gui_app()
    {
        Assert.Equal(HostKind.Launchd, Detect(new() { ["XPC_SERVICE_NAME"] = "uk.co.overpass.draughtsman" }, macOs: true));
        Assert.Equal(HostKind.Console, Detect(new() { ["XPC_SERVICE_NAME"] = "0" }, macOs: true));
        Assert.Equal(HostKind.Console, Detect(new() { ["XPC_SERVICE_NAME"] = "application.com.jetbrains.rider.1234" }, macOs: true));
        Assert.Equal(HostKind.Console, Detect(new() { ["XPC_SERVICE_NAME"] = "uk.co.overpass.draughtsman" }, macOs: false));
    }

    [Fact]
    public void A_windows_service_wins_over_everything_else()
    {
        Assert.Equal(HostKind.WindowsService, Detect(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" }, windowsService: true));
    }

    [Fact]
    public void Every_kind_has_a_wire_name_and_a_log_hint_naming_where_to_look()
    {
        Assert.Equal(["console", "container", "launchd", "systemd", "windowsService"], Enum.GetValues<HostKind>().Select(HostKinds.Name).Order().ToArray());
        Assert.Contains("docker logs", HostKinds.LogHint(HostKind.Container, "/data"));
        Assert.Contains("journalctl -u", HostKinds.LogHint(HostKind.Systemd, "/data"));
        Assert.Contains("/data/logs/draughtsman.log", HostKinds.LogHint(HostKind.Launchd, "/data"));
        string windows = HostKinds.LogHint(HostKind.WindowsService, "C:/data");
        Assert.Contains("Application event log", windows);
        Assert.Contains("data\\setup-code.txt", windows);
        Assert.Contains("first-run-code", windows);
        Assert.Contains("terminal", HostKinds.LogHint(HostKind.Console, "/data"));
    }
}
