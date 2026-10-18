using Draughtsman.Server.Cli;

namespace Draughtsman.Server.Tests;

public class CliCommandTests
{
    [Theory]
    [InlineData(new string[0], CliVerb.Serve)]
    [InlineData(new[] { "serve" }, CliVerb.Serve)]
    [InlineData(new[] { "--urls=http://0.0.0.0:5180" }, CliVerb.Serve)]
    [InlineData(new[] { "--DRAUGHTSMAN_DATA", "/srv/d" }, CliVerb.Serve)]
    [InlineData(new[] { "--version" }, CliVerb.Version)]
    [InlineData(new[] { "-v" }, CliVerb.Version)]
    [InlineData(new[] { "version" }, CliVerb.Version)]
    [InlineData(new[] { "--help" }, CliVerb.Help)]
    [InlineData(new[] { "-h" }, CliVerb.Help)]
    [InlineData(new[] { "help" }, CliVerb.Help)]
    [InlineData(new[] { "mcp", "--stdio" }, CliVerb.McpStdio)]
    [InlineData(new[] { "service", "install" }, CliVerb.Service)]
    [InlineData(new[] { "reset-admin", "lost@example.com", "--generate" }, CliVerb.ResetAdmin)]
    [InlineData(new[] { "backup", "--out", "b.zip" }, CliVerb.Backup)]
    [InlineData(new[] { "restore", "b.zip", "--overwrite" }, CliVerb.Restore)]
    [InlineData(new[] { "diff", "a.dsl", "b.dsl", "--ignore-layout" }, CliVerb.Diff)]
    [InlineData(new[] { "check", "a.dsl" }, CliVerb.Check)]
    public void A_command_line_selects_its_verb(string[] args, CliVerb expected)
    {
        Assert.Equal(expected, CliCommand.Parse(args).Verb);
    }

    [Theory]
    [InlineData("mcp", "--stdio", "--unexpected")]
    [InlineData("mcp", "--stdio", "--DRAUGHTSMAN_DATA=/tmp/x")]
    [InlineData("mcp", "--stdio", "extra", "words")]
    [InlineData("mcp", "--foo", "--stdio")]
    public void Mcp_stdio_with_extra_arguments_is_still_the_stdio_host_never_the_web_host(params string[] args)
    {
        CliCommand command = CliCommand.Parse(args);

        Assert.Equal(CliVerb.McpStdio, command.Verb);
        Assert.DoesNotContain("--stdio", command.Arguments);
    }

    [Theory]
    [InlineData("mcp")]
    [InlineData("mcp", "--stdo")]
    [InlineData("mcp", "--http")]
    public void Mcp_without_stdio_is_an_error_not_a_silent_web_host(params string[] args)
    {
        CliCommand command = CliCommand.Parse(args);

        Assert.Equal(CliVerb.Invalid, command.Verb);
        Assert.Contains("--stdio", command.Error);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("install")]
    [InlineData("Serve")]
    public void An_unknown_word_is_an_error_not_a_silent_web_host(string word)
    {
        CliCommand command = CliCommand.Parse([word]);

        Assert.Equal(CliVerb.Invalid, command.Verb);
        Assert.Contains(word, command.Error);
    }

    [Theory]
    [InlineData("--urls=http://0.0.0.0:6000", "http://0.0.0.0:6000")]
    [InlineData("--URLS=http://0.0.0.0:6000", "http://0.0.0.0:6000")]
    public void The_urls_option_is_read_in_its_inline_form(string arg, string expected)
    {
        Assert.Equal(expected, CliCommand.UrlsOption(["serve", arg]));
    }

    [Fact]
    public void The_urls_option_is_read_in_its_separate_form_and_absent_when_not_given()
    {
        Assert.Equal("http://127.0.0.1:9", CliCommand.UrlsOption(["--urls", "http://127.0.0.1:9"]));
        Assert.Null(CliCommand.UrlsOption(["serve"]));
        Assert.Null(CliCommand.UrlsOption(["--urls"]));
        Assert.Null(CliCommand.UrlsOption(["--urls", "--DRAUGHTSMAN_DATA=/x"]));
        Assert.Null(CliCommand.UrlsOption(["--urls="]));
    }

    [Fact]
    public async Task Version_prints_one_line_with_the_stamped_build_and_does_not_serve()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliOutcome outcome = await ServerCli.ExecuteAsync(["--version"], stdout, stderr);

        Assert.False(outcome.Serve);
        Assert.Equal(0, outcome.ExitCode);
        string line = stdout.ToString().TrimEnd();
        Assert.Matches(@"^Draughtsman \d+\.\d+\.\d+ \(build \d{4}-\d{2}-\d{2}\)$", line);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task Help_lists_every_command()
    {
        var stdout = new StringWriter();

        CliOutcome outcome = await ServerCli.ExecuteAsync(["--help"], stdout, new StringWriter());

        Assert.False(outcome.Serve);
        Assert.Equal(0, outcome.ExitCode);
        foreach (string command in new[] { "draughtsman [serve]", "draughtsman mcp --stdio", "draughtsman service install", "draughtsman service uninstall", "draughtsman service status", "--version", "--help", "--dry-run" })
        {
            Assert.Contains(command, stdout.ToString());
        }

        // The backup guide is named by its current page, not the pointer page it was merged out of.
        Assert.Contains("docs/upgrade-backup.md", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("backup-restore", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_exits_2_on_stderr_and_writes_nothing_to_stdout()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliOutcome outcome = await ServerCli.ExecuteAsync(["mcp", "--stdo"], stdout, stderr);

        Assert.False(outcome.Serve);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("--stdio", stderr.ToString());
    }

    [Fact]
    public async Task Serve_and_a_bare_run_hand_the_web_host_back_to_the_caller()
    {
        Assert.True((await ServerCli.ExecuteAsync([], new StringWriter(), new StringWriter())).Serve);
        Assert.True((await ServerCli.ExecuteAsync(["serve", "--urls=http://127.0.0.1:0"], new StringWriter(), new StringWriter())).Serve);
    }
}
