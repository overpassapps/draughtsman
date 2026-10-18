using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Draughtsman.Core.Tests;

/// <summary>
/// What the release image promises that only `docker build` and `scripts/docker-smoke.sh` could otherwise show: a named
/// volume starts owner-only (so the server's "data folder is open to other users" warning never fires), and the
/// documented `draughtsman` command is on PATH. The Dockerfile is read as text, so each rule has a variant of it that
/// breaks the rule and must be reported as broken.
/// </summary>
public sealed class DockerImageContractTests
{
    private static readonly string s_root = FindRepoRoot();

    [Fact]
    public void RuntimeStage_CreatesTheDataFolderOwnerOnlyAndOwnedByTheRuntimeUserBeforeItIsAVolume()
    {
        string dockerfile = File.ReadAllText(Path.Combine(s_root, "Dockerfile"));

        Assert.Null(DataFolderProblem(dockerfile));
    }

    [Fact]
    public void Checker_FlagsADataFolderThatIsNotOwnerOnlyNotOwnedOrAVolumeTooEarly()
    {
        string dockerfile = File.ReadAllText(Path.Combine(s_root, "Dockerfile"));

        // The pre-fix image: mkdir only, so the folder is 0755 and a named volume copies that.
        Assert.NotNull(DataFolderProblem(dockerfile.Replace(" \\\n    && chmod 0700 /data", string.Empty, StringComparison.Ordinal)));
        // A looser mode is not owner-only.
        Assert.NotNull(DataFolderProblem(dockerfile.Replace("chmod 0700 /data", "chmod 0755 /data", StringComparison.Ordinal)));
        // Owned by root: the runtime user could not write it.
        Assert.NotNull(DataFolderProblem(dockerfile.Replace("chown -R draughtsman:draughtsman /app /data", "chown -R draughtsman:draughtsman /app", StringComparison.Ordinal)));
        // VOLUME before the mode and owner are set: the volume would be seeded from the loose folder.
        Assert.NotNull(DataFolderProblem(dockerfile.Replace("VOLUME [\"/data\"]\n", string.Empty, StringComparison.Ordinal).Replace("RUN groupadd", "VOLUME [\"/data\"]\nRUN groupadd", StringComparison.Ordinal)));
    }

    [Fact]
    public void RuntimeStage_PutsDraughtsmanOnPathAsAnExecutableWrapper()
    {
        string dockerfile = File.ReadAllText(Path.Combine(s_root, "Dockerfile"));

        Assert.Contains("COPY docker/draughtsman /usr/local/bin/draughtsman", dockerfile, StringComparison.Ordinal);
        Assert.Matches(@"chmod \+x[^\n]*/usr/local/bin/draughtsman", dockerfile);
        Assert.True(File.Exists(Path.Combine(s_root, "docker", "draughtsman")));
    }

    [Fact]
    public void TheWrapperRunsTheDllWithEveryArgumentIntact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string wrapper = Path.Combine(s_root, "docker", "draughtsman");
        string[] args = ["backup", "--out", "/data/my backups/", "--include-secrets"];

        string[] seen = RunWithFakeDotnet(wrapper, args);

        Assert.Equal(["/app/draughtsman.dll", .. args], seen);

        // The opposite: a wrapper that does not quote its arguments splits the one with a space, and is caught.
        string unquoted = Path.Combine(Path.GetTempPath(), "draughtsman-wrapper-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(unquoted, File.ReadAllText(wrapper).Replace("\"$@\"", "$*", StringComparison.Ordinal));
            Assert.NotEqual(["/app/draughtsman.dll", .. args], RunWithFakeDotnet(unquoted, args));
        }
        finally
        {
            File.Delete(unquoted);
        }
    }

    /// <summary>The first reason the runtime stage would leave /data looser than 0700 or not the runtime user's, or null when it is fine.</summary>
    private static string? DataFolderProblem(string dockerfile)
    {
        // Join RUN continuations so a chained `mkdir && chmod` is one instruction.
        string[] instructions = Regex.Replace(dockerfile, @"\\\r?\n\s*", " ").Split('\n');
        int runtime = Array.FindIndex(instructions, line => Regex.IsMatch(line, @"^FROM\s.*\sAS\s+runtime\s*$", RegexOptions.IgnoreCase));
        if (runtime < 0)
        {
            return "no runtime stage";
        }

        string[] stage = instructions[runtime..];
        int chmod = Array.FindIndex(stage, line => line.StartsWith("RUN ", StringComparison.Ordinal) && Regex.IsMatch(line, @"\bchmod\s+0?700\s+/data\b"));
        int chown = Array.FindIndex(stage, line => line.StartsWith("RUN ", StringComparison.Ordinal) && Regex.IsMatch(line, @"\bchown\s+(-R\s+)?draughtsman:draughtsman\b[^&]*\s/data\b"));
        int volume = Array.FindIndex(stage, line => line.StartsWith("VOLUME ", StringComparison.Ordinal) && line.Contains("/data", StringComparison.Ordinal));
        if (chmod < 0)
        {
            return "/data is not chmod 0700";
        }

        if (chown < 0)
        {
            return "/data is not chowned to the runtime user";
        }

        if (volume < 0 || volume < chmod || volume < chown)
        {
            return "VOLUME /data is declared before its mode and owner are set";
        }

        return null;
    }

    /// <summary>Runs <paramref name="wrapper"/> with a stand-in <c>dotnet</c> first on PATH that prints its arguments one per line.</summary>
    private static string[] RunWithFakeDotnet(string wrapper, string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("A POSIX shell is needed.");
        }

        string bin = Path.Combine(Path.GetTempPath(), "draughtsman-fake-dotnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        try
        {
            string dotnet = Path.Combine(bin, "dotnet");
            File.WriteAllText(dotnet, "#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done\n");
            File.SetUnixFileMode(dotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add(wrapper);
            foreach (string arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            Directory.Delete(bin, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Draughtsman.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Draughtsman.sln not found above the test binary.");
    }
}
