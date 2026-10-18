using System.Collections;
using Draughtsman.Core.Hosting;

namespace Draughtsman.Core.Tests.Hosting;

/// <summary>AU-10: what a child process of the server is given is an allow-list; secrets, known or not, are not on it.</summary>
public class ScrubbedEnvironmentTests
{
    private static readonly Hashtable s_parent = new()
    {
        ["PATH"] = "/usr/bin",
        ["HOME"] = "/home/draughtsman",
        ["LANG"] = "en_GB.UTF-8",
        ["LC_ALL"] = "en_GB.UTF-8",
        ["NODE_OPTIONS"] = "--max-old-space-size=512",
        ["SystemRoot"] = "C:\\Windows",
        ["DRAUGHTSMAN_AI_KEY"] = "sk-provider-key",
        ["DRAUGHTSMAN_KEY_ENCRYPTION_KEY"] = "a2V5LWVuY3J5cHRpb24ta2V5",
        ["DRAUGHTSMAN_KEY_CERTIFICATE_PASSWORD"] = "pfx-password",
        ["MY_CUSTOM_KEK_NAME"] = "a key configured under a name nobody predicted",
        ["AWS_SECRET_ACCESS_KEY"] = "aws",
        ["ConnectionStrings__Postgres"] = "Host=db;Password=hunter2",
    };

    [Fact]
    public void Secrets_are_not_passed_on_whatever_they_are_called()
    {
        Dictionary<string, string> child = ScrubbedEnvironment.From(s_parent);

        Assert.DoesNotContain(child, pair => pair.Key.StartsWith("DRAUGHTSMAN_", StringComparison.Ordinal));
        Assert.DoesNotContain("MY_CUSTOM_KEK_NAME", child.Keys);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", child.Keys);
        Assert.DoesNotContain("ConnectionStrings__Postgres", child.Keys);
        Assert.DoesNotContain(child.Values, v => v.Contains("hunter2", StringComparison.Ordinal) || v.Contains("sk-provider-key", StringComparison.Ordinal));
    }

    [Fact]
    public void What_a_program_needs_to_run_is_kept()
    {
        Dictionary<string, string> child = ScrubbedEnvironment.From(s_parent);

        Assert.Equal("/usr/bin", child["PATH"]);
        Assert.Equal("/home/draughtsman", child["HOME"]);
        Assert.Equal("en_GB.UTF-8", child["LANG"]);
        Assert.Equal("en_GB.UTF-8", child["LC_ALL"]);
        Assert.Equal("--max-old-space-size=512", child["NODE_OPTIONS"]);
        Assert.Equal("C:\\Windows", child["SystemRoot"]);
    }

    [Fact]
    public void What_Node_and_dotnet_need_on_Windows_survives_under_the_names_Windows_gives_it_and_the_secrets_still_do_not()
    {
        // As Windows reports them: "Path", not "PATH"; "windir" and "SystemRoot" in their own mixed case; the lookup is case-blind there.
        var windows = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = "C:\\Windows\\system32;C:\\nodejs",
            ["SystemRoot"] = "C:\\Windows",
            ["windir"] = "C:\\Windows",
            ["TEMP"] = "C:\\Users\\u\\AppData\\Local\\Temp",
            ["TMP"] = "C:\\Users\\u\\AppData\\Local\\Temp",
            ["ComSpec"] = "C:\\Windows\\system32\\cmd.exe",
            ["PATHEXT"] = ".COM;.EXE;.BAT",
            ["NUMBER_OF_PROCESSORS"] = "4",
            ["PROCESSOR_ARCHITECTURE"] = "ARM64",
            ["LOCALAPPDATA"] = "C:\\Users\\u\\AppData\\Local",
            ["APPDATA"] = "C:\\Users\\u\\AppData\\Roaming",
            ["USERPROFILE"] = "C:\\Users\\u",
            ["ProgramFiles(x86)"] = "C:\\Program Files (x86)",
            ["DRAUGHTSMAN_AI_KEY"] = "sk-provider-key",
            ["Draughtsman_Key_Encryption_Key"] = "a2V5",
            ["AZURE_CLIENT_SECRET"] = "unlisted secret",
        };

        Dictionary<string, string> child = ScrubbedEnvironment.From(windows);

        // The child's own lookups are case-blind too, whichever spelling it asks for.
        foreach (string name in new[] { "PATH", "path", "SYSTEMROOT", "WINDIR", "Temp", "tmp", "COMSPEC", "pathext", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "LocalAppData", "AppData", "UserProfile", "programfiles(x86)" })
        {
            Assert.True(child.ContainsKey(name), $"{name} was stripped from the environment");
        }

        Assert.Equal("C:\\Windows\\system32;C:\\nodejs", child["PATH"]);
        Assert.DoesNotContain(child.Keys, name => name.StartsWith("Draughtsman_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(child.Values, v => v.Contains("sk-provider-key", StringComparison.Ordinal) || v == "unlisted secret");
    }

    [Fact]
    public void Names_are_matched_without_regard_to_case_and_additions_are_added()
    {
        var parent = new Hashtable { ["path"] = "/bin", ["Draughtsman_Ai_Key"] = "secret" };

        Dictionary<string, string> child = ScrubbedEnvironment.From(parent, [new KeyValuePair<string, string>("EXTRA", "1")]);

        Assert.Equal("/bin", child["PATH"]);
        Assert.False(child.ContainsKey("Draughtsman_Ai_Key"));
        Assert.Equal("1", child["EXTRA"]);
    }
}
