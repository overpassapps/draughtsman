using Draughtsman.Server.Hardening;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>AU-14: quiet by default, configurable from the data folder, an optional rolling file, appsettings found beside the binary.</summary>
public class LoggingSetupTests
{
    [Fact]
    public async Task Framework_chatter_is_off_by_default_and_the_servers_own_lines_are_kept()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new FakeIpServerFactory(yaml: null, logs);
        HttpClient client = await factory.CreateAuthenticatedClient();
        await client.GetAsync("/api/documents");
        await client.GetAsync("/api/health");
        await factory.CreateClient().GetAsync("/api/documents"); // a 401: "Cookie was challenged"

        IReadOnlyList<CapturingLoggerProvider.LogLine> lines = logs.Lines;

        Assert.DoesNotContain(lines, l => l.Message.Contains("was challenged", StringComparison.Ordinal));

        Assert.DoesNotContain(lines, l => l.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) && l.Level < LogLevel.Warning);
        Assert.DoesNotContain(lines, l => l.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && l.Level < LogLevel.Warning);
        Assert.Contains(lines, l => l.Level == LogLevel.Information && l.Message.StartsWith("Draughtsman ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_lines_appear_when_the_framework_level_is_raised_in_the_config_file()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new FakeIpServerFactory("logging:\n  frameworkLevel: Information\n", logs);
        HttpClient client = await factory.CreateAuthenticatedClient();
        await client.GetAsync("/api/documents");

        IReadOnlyList<CapturingLoggerProvider.LogLine> lines = logs.Lines;

        Assert.Contains(lines, l => l.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) && l.Level == LogLevel.Information);
        Assert.Contains(lines, l => l.Category.StartsWith("Microsoft.AspNetCore.Hosting", StringComparison.Ordinal) && l.Level == LogLevel.Information);
    }

    [Fact]
    public void Failed_sql_command_lines_are_off_by_default_and_come_back_when_the_framework_level_is_lowered()
    {
        // The fail: lines on every clean Postgres start (EF reading the migration history table before it exists).
        const string category = "Microsoft.EntityFrameworkCore.Database.Command";
        var logs = new CapturingLoggerProvider();
        using var quiet = new FakeIpServerFactory(yaml: null, logs, WindowsEventLogLikeDefaults);
        using var chatty = new FakeIpServerFactory("logging:\n  frameworkLevel: Information\n", new CapturingLoggerProvider(), WindowsEventLogLikeDefaults);

        bool quietEnabled = quiet.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category).IsEnabled(LogLevel.Error);
        bool chattyEnabled = chatty.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category).IsEnabled(LogLevel.Error);

        Assert.False(quietEnabled);
        Assert.True(chattyEnabled);
    }

    /// <summary>
    /// What the default host adds on Windows (a provider with the alias EventLog and its own Warning-and-above rule), so a Mac or Linux run
    /// exercises the same rule selection: that rule beats any category rule that names no provider, which is how failed SQL commands reached
    /// the Windows Event Log (and failed this test on the first Windows CI run) while the Console provider was already quiet.
    /// </summary>
    private static void WindowsEventLogLikeDefaults(ILoggingBuilder logging)
    {
        logging.AddProvider(new EventLogLikeProvider());
        logging.AddFilter<EventLogLikeProvider>(level => level >= LogLevel.Warning);
    }

    [Microsoft.Extensions.Logging.ProviderAlias("EventLog")]
    private sealed class EventLogLikeProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return new EnabledLogger();
        }

        public void Dispose()
        {
        }

        private sealed class EnabledLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
            }
        }
    }

    [Fact]
    public async Task The_servers_own_level_can_be_raised_to_warning_and_leaves_only_warnings()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new FakeIpServerFactory("logging:\n  level: Warning\n", logs);
        HttpClient client = await factory.CreateAuthenticatedClient();
        await client.GetAsync("/api/documents");

        Assert.DoesNotContain(logs.Lines, l => l.Category.StartsWith("Draughtsman", StringComparison.Ordinal) && l.Level < LogLevel.Warning);
    }

    [Fact]
    public async Task No_file_log_is_written_unless_asked_for_and_then_it_holds_the_servers_lines_but_not_the_setup_code()
    {
        using var off = new DraughtsmanServerFactory();
        await off.CreateAuthenticatedClient();
        Assert.False(Directory.Exists(Path.Combine(off.DataFolder, "logs")));

        using var on = new ConfiguredServerFactory("logging:\n  file:\n    enabled: true\n");
        await on.CreateAuthenticatedClient();
        string[] files = Directory.GetFiles(Path.Combine(on.DataFolder, "logs"), "draughtsman-*.log");

        string file = Assert.Single(files);
        Assert.Matches(@"draughtsman-\d{8}\.log$", file);
        string text = ReadWhileOpen(file);
        Assert.Contains("Draughtsman ", text, StringComparison.Ordinal);
        Assert.Contains("First-run setup code", text, StringComparison.Ordinal);
        // The value itself is for the console only: four letters-and-digits groups, as SetupCodeGate issues them.
        Assert.Contains("[redacted]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_rolls_over_at_the_size_limit_and_keeps_writing()
    {
        string folder = NewFolder();
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
            using var provider = new RollingFileLoggerProvider(folder, retainDays: 14, maxFileBytes: 400, clock);
            ILogger logger = provider.CreateLogger("Test");

            for (int i = 0; i < 12; i++)
            {
                logger.LogInformation("line {Number} {Padding}", i, new string('x', 60));
            }

            string[] files = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
            Assert.Contains("draughtsman-20261001.log", files);
            Assert.Contains("draughtsman-20261001-1.log", files);
            Assert.True(files.Length >= 3, string.Join(", ", files));
            Assert.All(Directory.GetFiles(folder), f => Assert.InRange(new FileInfo(f).Length, 1, 700));
            string all = string.Concat(Directory.GetFiles(folder).Select(ReadWhileOpen));
            for (int i = 0; i < 12; i++)
            {
                Assert.Contains($"line {i} ", all, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_live_log_can_be_renamed_by_an_operator_while_the_server_has_it_open_and_logging_carries_on()
    {
        // Windows only lets a file be renamed or deleted while open if the opener allowed it (FileShare.Delete); Unix always does.
        string folder = NewFolder();
        try
        {
            using var provider = new RollingFileLoggerProvider(folder, retainDays: 14, maxFileBytes: 1_000_000);
            ILogger logger = provider.CreateLogger("Test");
            logger.LogInformation("before");
            string live = provider.CurrentFile!;

            File.Move(live, Path.Combine(folder, "rotated.txt"));
            logger.LogInformation("after");

            Assert.Contains("before", ReadWhileOpen(Path.Combine(folder, "rotated.txt")), StringComparison.Ordinal);
            string all = string.Concat(Directory.GetFiles(folder, "draughtsman-*.log").Select(ReadWhileOpen)) + ReadWhileOpen(Path.Combine(folder, "rotated.txt"));
            Assert.Contains("after", all, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Files_older_than_the_retention_are_deleted_on_a_new_day_and_other_files_are_left_alone()
    {
        string folder = NewFolder();
        try
        {
            foreach (string name in new[] { "draughtsman-20260901.log", "draughtsman-20260916-2.log", "draughtsman-20260930.log", "notes.txt", "other-20200101.log" })
            {
                File.WriteAllText(Path.Combine(folder, name), "x");
            }

            var clock = new FixedClock(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
            using (var provider = new RollingFileLoggerProvider(folder, retainDays: 14, maxFileBytes: 1_000_000, clock))
            {
                provider.CreateLogger("Test").LogWarning("today");
            }

            string[] left = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
            Assert.Equal(["draughtsman-20260930.log", "draughtsman-20261001.log", "notes.txt", "other-20200101.log"], left);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_property_named_setup_code_is_never_written_but_other_values_are()
    {
        string folder = NewFolder();
        try
        {
            using (var provider = new RollingFileLoggerProvider(folder, 14, 1_000_000))
            {
                ILogger logger = provider.CreateLogger("Test");
                logger.LogWarning("code {SetupCode} and user {User}", "ABCD-1234-EFGH", "eric");
            }

            string text = ReadWhileOpen(Directory.GetFiles(folder).Single());
            Assert.DoesNotContain("ABCD-1234-EFGH", text, StringComparison.Ordinal);
            Assert.Contains("code [redacted] and user eric", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Appsettings_beside_the_binary_is_read_when_started_elsewhere_and_the_environment_still_wins()
    {
        string working = NewFolder();
        string binary = NewFolder();
        string previous = Environment.GetEnvironmentVariable("DM_TEST_LEVEL") ?? string.Empty;
        try
        {
            File.WriteAllText(Path.Combine(working, "appsettings.json"), "{\"A\":\"from-working\",\"B\":\"from-working\",\"C\":\"from-working\"}");
            File.WriteAllText(Path.Combine(binary, "appsettings.json"), "{\"B\":\"from-binary\",\"C\":\"from-binary\",\"D\":\"from-binary\"}");
            Environment.SetEnvironmentVariable("DM_TEST_C", "from-environment");
            var configuration = new ConfigurationManager();
            configuration.Sources.Add(new JsonConfigurationSource { Path = "appsettings.json", Optional = true, FileProvider = new PhysicalFileProvider(working) });
            configuration.AddEnvironmentVariables(prefix: "DM_TEST_");

            LoggingSetup.AddAppSettingsBesideTheBinary(configuration, binary);

            Assert.Equal("from-working", configuration["A"]);
            Assert.Equal("from-binary", configuration["B"]);
            Assert.Equal("from-binary", configuration["D"]);
            Assert.Equal("from-environment", configuration["C"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DM_TEST_C", null);
            Environment.SetEnvironmentVariable("DM_TEST_LEVEL", previous.Length == 0 ? null : previous);
            Directory.Delete(working, recursive: true);
            Directory.Delete(binary, recursive: true);
        }
    }

    [Fact]
    public void Levels_parse_case_insensitively_and_a_bad_name_falls_back()
    {
        Assert.Equal(LogLevel.Warning, LoggingSetup.ParseLevel(" warning ", LogLevel.Information));
        Assert.Equal(LogLevel.None, LoggingSetup.ParseLevel("None", LogLevel.Information));
        Assert.Equal(LogLevel.Information, LoggingSetup.ParseLevel("loud", LogLevel.Information));
        Assert.Equal(LogLevel.Information, LoggingSetup.ParseLevel("99", LogLevel.Information));
    }

    /// <summary>Reads a log the way an operator tailing it does: without asking to lock out the writer, which on Windows a plain <c>File.ReadAllText</c> does.</summary>
    private static string ReadWhileOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
