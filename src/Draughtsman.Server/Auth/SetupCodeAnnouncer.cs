using Draughtsman.Server.Hosting;

namespace Draughtsman.Server.Auth;

/// <summary>
/// Tells the operator how to get the first-run setup code, in the way that suits how the server was started. Every host
/// but a Windows service prints the code to its log (a console, <c>docker logs</c>, the journal, a launchd log file), where
/// the file logger redacts it (<c>SetupCode</c> is a redacted property). A Windows service has only the Application event
/// log, which any local user can read, so the code goes to a restricted file (<see cref="SetupCodeFile"/>) and the log says
/// where; the code itself is never in a log line there, and a failure to write the file is reported without it.
/// </summary>
public static class SetupCodeAnnouncer
{
    public static void Announce(ILogger logger, HostKind host, string dataFolder, string code)
    {
        if (host != HostKind.WindowsService)
        {
            logger.LogWarning(
                "==================================================================\n" +
                "First-run setup code: {SetupCode}\n" +
                "Enter this on the first-run page (or POST it as setupCode to /api/auth/first-run) to create the\n" +
                "administrator account. Valid only until an administrator exists; restart to get a new code.\n" +
                "==================================================================",
                code);
            return;
        }

        var file = new SetupCodeFile(dataFolder);
        try
        {
            file.Write(code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                "The first-run setup code could not be written to {Path} ({Reason}), so no administrator can be created until that is fixed. Check that the service account can write the data folder, then restart the service.",
                file.Path,
                ex.GetType().Name);
            return;
        }

        logger.LogWarning(
            "The setup code is stored at {Path}; read it as an Administrator. In an elevated prompt run: draughtsman first-run-code --DRAUGHTSMAN_DATA \"{DataFolder}\". " +
            "It is deleted as soon as the first administrator exists; restart the service to get a new code.",
            file.Path,
            dataFolder);
    }
}
