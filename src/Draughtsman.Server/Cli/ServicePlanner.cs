using System.Globalization;
using System.Text;
using System.Xml;

namespace Draughtsman.Server.Cli;

/// <summary>A service option the target cannot honour (for example a data folder systemd cannot give a dynamic user).</summary>
public sealed class ServicePlanException(string message) : Exception(message);

/// <summary>
/// Turns <see cref="ServiceOptions"/> into the unit file, launchd plist or <c>sc.exe</c> calls that install the server
/// as a background service, without touching the machine. Every service starts the published executable with
/// <c>serve</c> and names its data folder explicitly (DRAUGHTSMAN_DATA): a service's working directory is <c>/</c> or
/// <c>System32</c>, so nothing may depend on it. The bundle-extraction folder is set for the same reason: the
/// single-file executable unpacks its native libraries (SQLite) under $HOME by default, and a dynamic or system account
/// has none.
/// </summary>
public static class ServicePlanner
{
    public const string LaunchdLabelPrefix = "uk.co.overpass.";

    private const string WindowsDefaultAccount = "NT AUTHORITY\\LocalService";

    // Well-known SIDs, so the ACL steps work on a Windows in any display language ("Users" is "Benutzer" in German).
    private const string SidSystem = "*S-1-5-18";
    private const string SidAdministrators = "*S-1-5-32-544";
    private const string SidUsers = "*S-1-5-32-545";
    private const string SidEveryone = "*S-1-1-0";
    private const string SidAuthenticatedUsers = "*S-1-5-11";
    private const string SidLocalService = "*S-1-5-19";
    private const string SidNetworkService = "*S-1-5-20";

    public static ServiceTarget CurrentTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return ServiceTarget.WindowsService;
        }

        return OperatingSystem.IsMacOS() ? ServiceTarget.Launchd : ServiceTarget.Systemd;
    }

    public static string LaunchdLabel(string name) => LaunchdLabelPrefix + name;

    public static string DefaultDataFolder(ServiceTarget target, string name)
    {
        string display = name == ServiceOptions.DefaultName ? "Draughtsman" : name;
        return target switch
        {
            ServiceTarget.Systemd => $"/var/lib/{name}",
            ServiceTarget.Launchd => $"/Library/Application Support/{display}",
            _ => $"{WindowsProgramData()}\\{display}",
        };
    }

    public static ServicePlan Plan(ServiceOptions options, string currentExecutable)
    {
        ServiceTarget target = options.Platform ?? CurrentTarget();
        string executable = options.Executable ?? currentExecutable;
        return options.Action switch
        {
            ServiceAction.Install => PlanInstall(options, target, executable),
            ServiceAction.Uninstall => PlanUninstall(options, target),
            _ => PlanStatus(options, target),
        };
    }

    // --- systemd ---------------------------------------------------------------------------------------------------

    public static string UnitPath(string name) => $"/etc/systemd/system/{name}.service";

    /// <summary>
    /// The unit text. With no <c>--user</c> the service is a systemd DynamicUser that owns <c>/var/lib/&lt;name&gt;</c> (its
    /// StateDirectory) and <c>/var/cache/&lt;name&gt;</c>, so the install creates no account; with <c>--user</c> it runs as
    /// that account and, for a data folder of its own, gets write access to exactly that folder.
    /// </summary>
    public static string RenderSystemdUnit(string name, string executable, string dataFolder, string? user)
    {
        string defaultData = DefaultDataFolder(ServiceTarget.Systemd, name);
        bool managedState = string.Equals(dataFolder, defaultData, StringComparison.Ordinal);
        if (!managedState && user is null)
        {
            throw new ServicePlanException($"a data folder other than {defaultData} needs --user: a systemd dynamic user can only own /var/lib/{name}. Create the account, give it the folder, and pass --user <account>.");
        }

        string cache = $"/var/cache/{name}";
        var unit = new StringBuilder();
        unit.Append("# Written by `draughtsman service install`; run it again to regenerate this file.\n");
        unit.Append("# Settings belong in draughtsman.yaml in the data folder, not here.\n");
        unit.Append("[Unit]\n");
        unit.Append("Description=Draughtsman diagram server\n");
        unit.Append("After=network-online.target\n");
        unit.Append("Wants=network-online.target\n\n");
        unit.Append("[Service]\n");
        unit.Append("Type=simple\n");
        unit.Append(CultureInfo.InvariantCulture, $"ExecStart={SystemdQuote(executable)} serve\n");
        unit.Append(CultureInfo.InvariantCulture, $"WorkingDirectory={SystemdEscapeSpecifiers(ParentOf(executable, '/'))}\n");
        unit.Append(CultureInfo.InvariantCulture, $"Environment={SystemdQuote($"DRAUGHTSMAN_DATA={dataFolder}")}\n");
        unit.Append(CultureInfo.InvariantCulture, $"Environment={SystemdQuote($"DOTNET_BUNDLE_EXTRACT_BASE_DIR={cache}")}\n");
        unit.Append("Restart=on-failure\n");
        unit.Append("RestartSec=5\n");
        if (user is null)
        {
            unit.Append("DynamicUser=yes\n");
        }
        else
        {
            unit.Append(CultureInfo.InvariantCulture, $"User={user}\n");
        }

        if (managedState)
        {
            unit.Append(CultureInfo.InvariantCulture, $"StateDirectory={name}\n");
        }
        else
        {
            unit.Append(CultureInfo.InvariantCulture, $"ReadWritePaths={SystemdEscapeSpecifiers(dataFolder)}\n");
        }

        unit.Append(CultureInfo.InvariantCulture, $"CacheDirectory={name}\n");
        unit.Append("NoNewPrivileges=yes\n");
        unit.Append("ProtectSystem=strict\n");
        unit.Append("ProtectHome=read-only\n");
        unit.Append("PrivateTmp=yes\n\n");
        unit.Append("[Install]\n");
        unit.Append("WantedBy=multi-user.target\n");
        return unit.ToString();
    }

    /// <summary>A systemd word in double quotes: backslash and quote escaped, and % and $ doubled so neither is expanded.</summary>
    public static string SystemdQuote(string value)
    {
        string escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + SystemdEscapeSpecifiers(escaped).Replace("$", "$$", StringComparison.Ordinal) + "\"";
    }

    private static string SystemdEscapeSpecifiers(string value) => value.Replace("%", "%%", StringComparison.Ordinal);

    // --- launchd ---------------------------------------------------------------------------------------------------

    public static string PlistPath(string name) => $"/Library/LaunchDaemons/{LaunchdLabel(name)}.plist";

    /// <summary>
    /// A system LaunchDaemon: starts at boot, is restarted when it exits, logs under <c>&lt;data&gt;/logs</c>. It runs as
    /// root unless <c>--user</c> names an account (create and own the data folder for it first).
    /// </summary>
    public static string RenderLaunchdPlist(string name, string executable, string dataFolder, string? user)
    {
        string logs = dataFolder + "/logs";
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false, NewLineChars = "\n" };
        using var buffer = new MemoryStream();
        using (XmlWriter xml = XmlWriter.Create(buffer, settings))
        {
            xml.WriteStartDocument();
            xml.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
            xml.WriteStartElement("plist");
            xml.WriteAttributeString("version", "1.0");
            xml.WriteStartElement("dict");
            PlistString(xml, "Label", LaunchdLabel(name));
            xml.WriteElementString("key", "ProgramArguments");
            xml.WriteStartElement("array");
            xml.WriteElementString("string", executable);
            xml.WriteElementString("string", "serve");
            xml.WriteEndElement();
            PlistString(xml, "WorkingDirectory", ParentOf(executable, '/'));
            xml.WriteElementString("key", "EnvironmentVariables");
            xml.WriteStartElement("dict");
            PlistString(xml, "DRAUGHTSMAN_DATA", dataFolder);
            PlistString(xml, "DOTNET_BUNDLE_EXTRACT_BASE_DIR", dataFolder + "/.bundle");
            xml.WriteEndElement();
            if (user is not null)
            {
                PlistString(xml, "UserName", user);
            }

            xml.WriteElementString("key", "RunAtLoad");
            PlistTrue(xml);
            xml.WriteElementString("key", "KeepAlive");
            PlistTrue(xml);
            xml.WriteElementString("key", "ThrottleInterval");
            xml.WriteElementString("integer", "10");
            PlistString(xml, "StandardOutPath", logs + "/" + name + ".log");
            PlistString(xml, "StandardErrorPath", logs + "/" + name + ".log");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
    }

    private static void PlistTrue(XmlWriter xml)
    {
        xml.WriteStartElement("true");
        xml.WriteEndElement();
    }

    private static void PlistString(XmlWriter xml, string key, string value)
    {
        xml.WriteElementString("key", key);
        xml.WriteElementString("string", value);
    }

    // --- Windows ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>sc.exe create</c> arguments. The data folder travels on the command line (<c>--DRAUGHTSMAN_DATA</c>, which the
    /// host reads as configuration) because the alternative, a per-service Environment value, lives in the registry and
    /// ADR 0001 keeps the product off it. Each <c>option=</c> and its value are separate arguments, as sc.exe requires.
    /// </summary>
    public static IReadOnlyList<string> WindowsCreateArguments(string name, string executable, string dataFolder, string account)
    {
        string binPath = $"\"{executable}\" serve --DRAUGHTSMAN_DATA \"{dataFolder.TrimEnd('\\', '/')}\"";
        return ["create", name, "binPath=", binPath, "start=", "auto", "obj=", account, "DisplayName=", WindowsDisplayName(name)];
    }

    /// <summary>
    /// The name Services shows. It must differ from the service name by more than letter case: the service manager treats a
    /// display name that equals the name case-insensitively as none and shows the name, so "Draughtsman" for a service called
    /// "draughtsman" appeared as plain "draughtsman" (the registry held it, sc qc and services.msc did not show it).
    /// </summary>
    public static string WindowsDisplayName(string name)
    {
        return name == ServiceOptions.DefaultName ? "Draughtsman diagram server" : $"Draughtsman diagram server ({name})";
    }

    /// <summary>
    /// The one <c>icacls</c> call that makes the data folder private to the machine's administrators and the service
    /// (D2 of the Windows install proof). The folder is created under <c>%ProgramData%</c>, whose ACL gives every local user
    /// read and create rights, and a grant on top of that left them in place: so inheritance is cut (<c>/inheritance:r</c>),
    /// <c>Users</c>, <c>Everyone</c> and <c>Authenticated Users</c> are removed if the folder had them of its own, and exactly
    /// SYSTEM, Administrators and the service account get full control, inherited by everything inside. Well-known accounts
    /// are given by SID so a localised Windows resolves them.
    /// </summary>
    public static IReadOnlyList<string> WindowsDataFolderAclArguments(string dataFolder, string account)
    {
        string full = $"{WindowsAclPrincipal(account)}:(OI)(CI)F";
        string[] grants = [$"{SidSystem}:(OI)(CI)F", $"{SidAdministrators}:(OI)(CI)F"];
        // The service account is usually LocalService; if the operator chose SYSTEM or an administrator, it is already in the list.
        string[] withService = [.. grants.Concat(grants.Contains(full, StringComparer.OrdinalIgnoreCase) ? [] : new[] { full })];
        return [dataFolder.TrimEnd('\\', '/'), "/inheritance:r", "/remove:g", SidUsers, SidEveryone, SidAuthenticatedUsers, "/grant:r", .. withService];
    }

    /// <summary>The account as <c>icacls</c> should be given it: a SID for the built-in service accounts, whatever the operator typed for any other.</summary>
    private static string WindowsAclPrincipal(string account)
    {
        string normalised = account.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalised switch
        {
            "ntauthority\\localservice" or "localservice" => SidLocalService,
            "ntauthority\\networkservice" or "networkservice" => SidNetworkService,
            "ntauthority\\system" or "localsystem" or "system" => SidSystem,
            _ => account,
        };
    }

    // --- plans -----------------------------------------------------------------------------------------------------

    private static ServicePlan PlanInstall(ServiceOptions options, ServiceTarget target, string executable)
    {
        string name = options.Name;
        string dataFolder = options.DataFolder ?? DefaultDataFolder(target, name);
        var steps = new List<ServiceStep>();
        var notes = new List<string>();
        switch (target)
        {
            case ServiceTarget.Systemd:
                steps.Add(new ServiceStep.WriteFile(UnitPath(name), RenderSystemdUnit(name, executable, dataFolder, options.User)));
                if (options.DataFolder is not null && options.DataFolder != DefaultDataFolder(target, name))
                {
                    steps.Add(new ServiceStep.EnsureDirectory(dataFolder, options.User));
                }

                steps.Add(new ServiceStep.Run("systemctl", ["daemon-reload"]));
                steps.Add(new ServiceStep.Run("systemctl", ["enable", "--now", name + ".service"]));
                notes.Add($"Logs: journalctl -u {name}. First-run setup code: look for it in that log.");
                break;
            case ServiceTarget.Launchd:
                steps.Add(new ServiceStep.EnsureDirectory(dataFolder, options.User));
                steps.Add(new ServiceStep.EnsureDirectory(dataFolder + "/logs", options.User));
                steps.Add(new ServiceStep.EnsureDirectory(dataFolder + "/.bundle", options.User));
                steps.Add(new ServiceStep.WriteFile(PlistPath(name), RenderLaunchdPlist(name, executable, dataFolder, options.User)));
                steps.Add(new ServiceStep.Run("launchctl", ["bootstrap", "system", PlistPath(name)]));
                steps.Add(new ServiceStep.Run("launchctl", ["enable", "system/" + LaunchdLabel(name)]));
                notes.Add($"Logs: {dataFolder}/logs/{name}.log. First-run setup code: look for it there.");
                if (options.User is null)
                {
                    notes.Add("The service runs as root. To run it as another account, create the account and pass --user <account>.");
                }

                break;
            default:
                string account = options.User ?? WindowsDefaultAccount;
                if (ServicePreflight.ExecutableUnreadableByService(executable, account) is { } unreadable)
                {
                    throw new ServicePlanException(unreadable);
                }

                steps.Add(new ServiceStep.EnsureDirectory(dataFolder));
                steps.Add(new ServiceStep.Run("icacls", WindowsDataFolderAclArguments(dataFolder, account)));
                steps.Add(new ServiceStep.RegisterEventSource(WindowsEventSource.Name));
                steps.Add(new ServiceStep.Run("sc.exe", WindowsCreateArguments(name, executable, dataFolder, account)));
                steps.Add(new ServiceStep.Run("sc.exe", ["description", name, "Draughtsman diagram server"]));
                steps.Add(new ServiceStep.Run("sc.exe", ["failure", name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000"]));
                steps.Add(new ServiceStep.Run("sc.exe", ["start", name]));
                notes.Add($"Logs: the service writes to the Windows Application event log (source {WindowsEventSource.Name}). First-run setup code: run 'draughtsman first-run-code --DRAUGHTSMAN_DATA \"{dataFolder.TrimEnd('\\', '/')}\"' from an elevated prompt (the code is kept in {dataFolder.TrimEnd('\\', '/')}\\setup-code.txt, readable by Administrators only, and is never written to a log).");
                break;
        }

        notes.Add($"Data folder: {dataFolder}. Put settings in draughtsman.yaml there; to reach the server from other machines set server.host (see draughtsman.example.yaml).");
        return new ServicePlan(target, ServiceAction.Install, name, steps, notes);
    }

    private static ServicePlan PlanUninstall(ServiceOptions options, ServiceTarget target)
    {
        string name = options.Name;
        var steps = new List<ServiceStep>();
        switch (target)
        {
            case ServiceTarget.Systemd:
                steps.Add(new ServiceStep.Run("systemctl", ["disable", "--now", name + ".service"], IgnoreFailure: true));
                steps.Add(new ServiceStep.DeleteFile(UnitPath(name)));
                steps.Add(new ServiceStep.Run("systemctl", ["daemon-reload"]));
                break;
            case ServiceTarget.Launchd:
                steps.Add(new ServiceStep.Run("launchctl", ["bootout", "system/" + LaunchdLabel(name)], IgnoreFailure: true));
                steps.Add(new ServiceStep.DeleteFile(PlistPath(name)));
                break;
            default:
                // 1062 is "the service has not been started": nothing to stop, so say nothing about it.
                steps.Add(new ServiceStep.Run("sc.exe", ["stop", name], IgnoreFailure: true, QuietExitCodes: [1062]));
                steps.Add(new ServiceStep.Run("sc.exe", ["delete", name]));
                break;
        }

        return new ServicePlan(target, ServiceAction.Uninstall, name, steps, ["The data folder is left exactly as it is."]);
    }

    private static ServicePlan PlanStatus(ServiceOptions options, ServiceTarget target)
    {
        string name = options.Name;
        ServiceStep step = target switch
        {
            ServiceTarget.Systemd => new ServiceStep.Run("systemctl", ["status", name + ".service", "--no-pager"]),
            ServiceTarget.Launchd => new ServiceStep.Run("launchctl", ["print", "system/" + LaunchdLabel(name)]),
            _ => new ServiceStep.Run("sc.exe", ["query", name]),
        };
        return new ServicePlan(target, ServiceAction.Status, name, [step], []);
    }

    private static string WindowsProgramData()
    {
        string? fromEnvironment = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("ProgramData") : null;
        return string.IsNullOrWhiteSpace(fromEnvironment) ? "C:\\ProgramData" : fromEnvironment.TrimEnd('\\');
    }

    private static string ParentOf(string path, char separator)
    {
        int cut = path.LastIndexOf(separator);
        return cut > 0 ? path[..cut] : path;
    }
}
