namespace Draughtsman.Server.Cli;

public enum CliVerb
{
    /// <summary>Run the web host: REST API, SPA and MCP over HTTP.</summary>
    Serve,

    /// <summary>Print the version and stop.</summary>
    Version,

    /// <summary>Print usage and stop.</summary>
    Help,

    /// <summary><c>mcp --stdio</c>: the MCP tools over stdio, never the web host.</summary>
    McpStdio,

    /// <summary><c>service install|uninstall|status</c>.</summary>
    Service,

    /// <summary><c>reset-admin &lt;email&gt;</c>: the host-shell lost-admin recovery (<c>ResetAdminCommand</c>).</summary>
    ResetAdmin,

    /// <summary><c>first-run-code</c>: prints a Windows service's setup code from its restricted file (<c>FirstRunCodeCommand</c>).</summary>
    FirstRunCode,

    /// <summary><c>backup</c>: a consistent, checksummed bundle of the data folder (<c>BackupCommands</c>).</summary>
    Backup,

    /// <summary><c>restore &lt;bundle&gt;</c>: verify a bundle and restore it (<c>BackupCommands</c>).</summary>
    Restore,

    /// <summary><c>config check</c>: what starting this binary on a data folder would do, without starting it (<c>ConfigCheckCommand</c>).</summary>
    Config,

    /// <summary><c>diff &lt;before&gt; &lt;after&gt;</c>: what changed between two DSL files, in sentences (<c>DiagramFileCommands</c>).</summary>
    Diff,

    /// <summary><c>check &lt;file&gt;</c>: parse warnings and critique findings for one DSL file (<c>DiagramFileCommands</c>).</summary>
    Check,

    /// <summary><c>doctor</c>: every check that needs no network and no sign-in, with the server up or down (<c>DoctorCommand</c>).</summary>
    Doctor,

    /// <summary><c>support-bundle</c>: one zip of redacted diagnostics a customer can send (<c>SupportBundleCommand</c>).</summary>
    SupportBundle,

    /// <summary>An argument that names no command.</summary>
    Invalid,
}

/// <summary>A parsed command line. <see cref="Arguments"/> are what follows the verb; <see cref="Error"/> is set for <see cref="CliVerb.Invalid"/>.</summary>
public sealed record CliCommand(CliVerb Verb, IReadOnlyList<string> Arguments, string? Error = null)
{
    /// <summary>
    /// Reads the command line. Nothing here touches the machine, so every rule is unit-testable.
    /// A leading option (<c>--urls=...</c>, <c>--DRAUGHTSMAN_DATA=...</c>) is host configuration for the web host, which is
    /// what a bare <c>draughtsman --urls=...</c> has always meant, so it selects <c>serve</c>; only <c>--version</c> and
    /// <c>--help</c> are commands in their own right. Anything else that is not a known verb is an error rather than a
    /// silent start of the server.
    /// </summary>
    public static CliCommand Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliCommand(CliVerb.Serve, []);
        }

        string first = args[0];
        string[] rest = args[1..];
        switch (first)
        {
            case "--version" or "-v" or "version":
                return new CliCommand(CliVerb.Version, rest);
            case "--help" or "-h" or "-?" or "help":
                return new CliCommand(CliVerb.Help, rest);
            case "serve":
                return new CliCommand(CliVerb.Serve, rest);
            case "service":
                return new CliCommand(CliVerb.Service, rest);
            case "reset-admin":
                return new CliCommand(CliVerb.ResetAdmin, rest);
            case "first-run-code":
                return new CliCommand(CliVerb.FirstRunCode, rest);
            case "backup":
                return new CliCommand(CliVerb.Backup, rest);
            case "restore":
                return new CliCommand(CliVerb.Restore, rest);
            case "config":
                return new CliCommand(CliVerb.Config, rest);
            case "doctor":
                return new CliCommand(CliVerb.Doctor, rest);
            case "support-bundle":
                return new CliCommand(CliVerb.SupportBundle, rest);
            case "diff":
                return new CliCommand(CliVerb.Diff, rest);
            case "check":
                return new CliCommand(CliVerb.Check, rest);
            case "mcp":
                return ParseMcp(rest);
        }

        if (first.StartsWith('-'))
        {
            return new CliCommand(CliVerb.Serve, args);
        }

        return new CliCommand(CliVerb.Invalid, args, $"unknown command '{first}'");
    }

    /// <summary>
    /// The value of <c>--urls=&lt;value&gt;</c> or <c>--urls &lt;value&gt;</c>, if given. Program.cs lets only the ASPNETCORE_URLS
    /// environment variable override server.host and server.port (it calls UseUrls itself), so the dispatcher hands this to
    /// that variable; otherwise the documented <c>--urls</c> option would be silently ignored.
    /// </summary>
    public static string? UrlsOption(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase))
            {
                string inline = arg["--urls=".Length..];
                return inline.Length > 0 ? inline : null;
            }

            if (string.Equals(arg, "--urls", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count && !args[i + 1].StartsWith('-'))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// <c>mcp --stdio</c> tolerates further options (they are host configuration, like <c>--DRAUGHTSMAN_DATA=...</c>): what it
    /// must never do is fall through to the web host, whose logging to stdout would corrupt the JSON-RPC stream. <c>mcp</c>
    /// without <c>--stdio</c> is an error; MCP over HTTP is <c>serve</c> (the <c>/mcp</c> route).
    /// </summary>
    private static CliCommand ParseMcp(string[] rest)
    {
        if (rest.Any(a => a is "--help" or "-h" or "-?"))
        {
            return new CliCommand(CliVerb.Help, []);
        }

        return rest.Contains("--stdio")
            ? new CliCommand(CliVerb.McpStdio, rest.Where(a => a != "--stdio").ToArray())
            : new CliCommand(CliVerb.Invalid, rest, "'mcp' needs --stdio (MCP over HTTP is served by 'serve', at /mcp)");
    }
}
