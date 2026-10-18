using System.Diagnostics;

namespace Draughtsman.Server.Cli;

/// <summary>Performs (or, for a dry run, prints) a <see cref="ServicePlan"/>.</summary>
public static class ServiceExecutor
{
    public static async Task<int> RunAsync(ServicePlan plan, bool dryRun, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        if (dryRun)
        {
            Describe(plan, stdout);
            return 0;
        }

        try
        {
            foreach (ServiceStep step in plan.Steps)
            {
                int exit = await ExecuteAsync(step, stdout, stderr, cancellationToken);
                if (exit != 0)
                {
                    stderr.WriteLine($"draughtsman: '{Format(step)}' failed (exit {exit}); stopping.");
                    return 1;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
        {
            stderr.WriteLine($"draughtsman: {ex.Message}");
            if (ex is UnauthorizedAccessException)
            {
                stderr.WriteLine(OperatingSystem.IsWindows()
                    ? "Run this from an elevated (Administrator) prompt."
                    : "Run this as root, for example: sudo draughtsman service " + plan.Action.ToString().ToLowerInvariant());
            }

            return 1;
        }

        foreach (string note in plan.Notes)
        {
            stdout.WriteLine(note);
        }

        return 0;
    }

    /// <summary>What a dry run prints: every file in full, every command as typed, and the notes; nothing is written or run.</summary>
    public static void Describe(ServicePlan plan, TextWriter writer)
    {
        writer.WriteLine($"# dry run: service {plan.Action.ToString().ToLowerInvariant()} '{plan.Name}' for {plan.Target}; nothing is changed");
        foreach (ServiceStep step in plan.Steps)
        {
            switch (step)
            {
                case ServiceStep.WriteFile file:
                    writer.WriteLine($"# write {file.Path}");
                    writer.Write(file.Content);
                    writer.WriteLine($"# end {file.Path}");
                    break;
                default:
                    writer.WriteLine("# " + Format(step));
                    break;
            }
        }

        foreach (string note in plan.Notes)
        {
            writer.WriteLine("# " + note);
        }
    }

    public static string Format(ServiceStep step)
    {
        return step switch
        {
            ServiceStep.EnsureDirectory dir => dir.OwnerUser is null ? $"create folder {dir.Path}" : $"create folder {dir.Path} owned by {dir.OwnerUser}",
            ServiceStep.WriteFile file => $"write {file.Path}",
            ServiceStep.DeleteFile file => $"delete {file.Path}",
            ServiceStep.RegisterEventSource source => $"register the Windows event source {source.Source} in the Application log",
            ServiceStep.Run run => "run " + run.FileName + " " + string.Join(' ', run.Arguments.Select(Quote)),
            _ => step.ToString() ?? string.Empty,
        };
    }

    private static string Quote(string argument)
    {
        return argument.Length > 0 && argument.All(c => !char.IsWhiteSpace(c) && c != '"') ? argument : "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static async Task<int> ExecuteAsync(ServiceStep step, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        switch (step)
        {
            case ServiceStep.EnsureDirectory dir:
                Directory.CreateDirectory(dir.Path);
                stdout.WriteLine($"folder {dir.Path}");
                if (dir.OwnerUser is not null && !OperatingSystem.IsWindows())
                {
                    return await StartAsync("chown", ["-R", dir.OwnerUser, dir.Path], stdout, stderr, cancellationToken);
                }

                return 0;
            case ServiceStep.WriteFile file:
                await File.WriteAllTextAsync(file.Path, file.Content, cancellationToken);
                stdout.WriteLine($"wrote {file.Path}");
                return 0;
            case ServiceStep.DeleteFile file:
                File.Delete(file.Path);
                stdout.WriteLine($"removed {file.Path}");
                return 0;
            case ServiceStep.RegisterEventSource source:
                if (!OperatingSystem.IsWindows())
                {
                    return 0;
                }

                stdout.WriteLine(WindowsEventSource.Register(source.Source) ? $"registered the Windows event source {source.Source}" : $"the Windows event source {source.Source} already exists");
                return 0;
            case ServiceStep.Run run:
                // Captured, then shown unless the program said "nothing to do" with one of its quiet exit codes.
                (int exit, string output, string errors) = await CaptureAsync(run.FileName, run.Arguments, cancellationToken);
                if (run.QuietExitCodes is not null && run.QuietExitCodes.Contains(exit))
                {
                    return 0;
                }

                stdout.Write(output);
                stderr.Write(errors);
                return run.IgnoreFailure ? 0 : exit;
            default:
                throw new InvalidOperationException($"unhandled service step {step}");
        }
    }

    private static async Task<int> StartAsync(string fileName, IReadOnlyList<string> arguments, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        (int exit, string output, string errors) = await CaptureAsync(fileName, arguments, cancellationToken);
        stdout.Write(output);
        stderr.Write(errors);
        return exit;
    }

    private static async Task<(int ExitCode, string Output, string Errors)> CaptureAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new IOException($"could not start {fileName}");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }
}
