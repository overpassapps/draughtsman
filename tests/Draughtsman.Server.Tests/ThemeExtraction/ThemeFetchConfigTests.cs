using Draughtsman.Server.Cli;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Tests.ThemeColourExtraction;

/// <summary><c>themes.fetchFromUrl</c> in draughtsman.yaml and what <c>draughtsman config check</c> says about it.</summary>
public class ThemeFetchConfigTests
{
    private static async Task<IReadOnlyList<CheckLine>> Check(string? yaml)
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            if (yaml is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, DraughtsmanConfig.FileName), yaml);
            }

            CheckReport report = await ConfigCheckCommand.CheckAsync(folder);
            return [.. report.Lines.Where(l => l.Section == "Themes")];
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_feature_is_off_and_lists_no_hosts_unless_the_file_says_otherwise()
    {
        DraughtsmanConfig none = ConfigLoader.Parse(string.Empty);
        DraughtsmanConfig on = ConfigLoader.Parse("themes:\n  fetchFromUrl:\n    enabled: true\n    allowedHosts:\n      - wiki.corp.example\n      - 10.0.0.5\n");

        Assert.False(none.Themes.FetchFromUrl.Enabled);
        Assert.Empty(none.Themes.FetchFromUrl.AllowedHosts);
        Assert.True(on.Themes.FetchFromUrl.Enabled);
        Assert.Equal(["wiki.corp.example", "10.0.0.5"], on.Themes.FetchFromUrl.AllowedHosts);
    }

    [Fact]
    public async Task Config_check_says_the_server_fetches_nothing_when_it_is_off_and_what_it_will_do_when_it_is_on()
    {
        CheckLine off = Assert.Single(await Check(null));
        Assert.Equal(CheckLevel.Ok, off.Level);
        Assert.Contains("false (the default)", off.Message, StringComparison.Ordinal);
        Assert.Contains("fetches no web page", off.Message, StringComparison.Ordinal);

        CheckLine on = Assert.Single(await Check("themes:\n  fetchFromUrl:\n    enabled: true\n"));
        Assert.Equal(CheckLevel.Info, on.Level);
        Assert.Contains("up to 8 of its stylesheets", on.Message, StringComparison.Ordinal);
        Assert.Contains("security log", on.Message, StringComparison.Ordinal);
        Assert.Contains("Every loopback, private, link-local and metadata address is refused", on.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_check_names_the_hosts_that_may_be_reached_and_warns_about_a_bad_entry_and_about_the_metadata_address()
    {
        IReadOnlyList<CheckLine> lines = await Check("themes:\n  fetchFromUrl:\n    enabled: true\n    allowedHosts:\n      - Wiki.Corp.Example\n      - \"https://bad.example\"\n      - 169.254.169.254\n");

        Assert.Contains(lines, l => l.Level == CheckLevel.Info && l.Message.Contains("wiki.corp.example", StringComparison.Ordinal) && l.Message.Contains("169.254.169.254", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Level == CheckLevel.Warn && l.Message.Contains("'https://bad.example'", StringComparison.Ordinal) && l.Message.Contains("ignored", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Level == CheckLevel.Warn && l.Message.Contains("cloud metadata address", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Allowed_hosts_while_the_feature_is_off_are_reported_as_doing_nothing()
    {
        CheckLine line = Assert.Single(await Check("themes:\n  fetchFromUrl:\n    allowedHosts: [wiki.corp.example]\n"));

        Assert.Equal(CheckLevel.Ok, line.Level);
        Assert.Contains("do nothing while it is off", line.Message, StringComparison.Ordinal);
    }
}
