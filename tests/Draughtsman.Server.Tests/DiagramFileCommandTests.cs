using System.Text.Json;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;
using Draughtsman.Server.Cli;

namespace Draughtsman.Server.Tests;

/// <summary><c>draughtsman diff</c> and <c>draughtsman check</c> through the real dispatcher: files in, exit code and text out.</summary>
public sealed class DiagramFileCommandTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-diagram-files-" + Guid.NewGuid().ToString("N"));

    public DiagramFileCommandTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private static string Contract(string file)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", file));
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        CliOutcome outcome = await ServerCli.ExecuteAsync(args, stdout, stderr);
        Assert.False(outcome.Serve, "diff and check must never start the web host");
        return (outcome.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string Order => Contract("order-service.dsl");

    [Theory]
    [InlineData("order-service.json")]
    [InlineData("checkout-sequence.json")]
    [InlineData("checkout-fragments.json")]
    [InlineData("editor-session.json")]
    public async Task A_document_diffed_against_its_own_re_exported_dsl_reports_no_changes(string file)
    {
        Document document = DocumentJson.Deserialize(Contract(file));
        string exported = Write("exported.dsl", Dsl.Print(document));
        string again = Write("again.dsl", Dsl.Print(Dsl.Parse(Dsl.Print(document)).Document));

        (int exit, string output, string error) = await Run("diff", exported, again);

        Assert.Equal(0, exit);
        Assert.Contains("No changes.", output);
        Assert.Equal(string.Empty, error);

        // The same command must be able to say otherwise.
        string edited = Write("edited.dsl", Dsl.Print(document with { Title = document.Title + " v2" }));
        Assert.Equal(1, (await Run("diff", exported, edited)).Exit);
    }

    [Fact]
    public async Task A_changed_label_is_reported_by_label_with_exit_one()
    {
        string before = Write("before.dsl", Order);
        string after = Write("after.dsl", Order.Replace("\"read/write\"", "\"reads\"", StringComparison.Ordinal));

        (int exit, string output, _) = await Run("diff", before, after);

        Assert.Equal(1, exit);
        Assert.Contains("- Edge `Orders API` to `Postgres` relabelled from `read/write` to `reads`", output);
        Assert.DoesNotContain("e_2", output);
        Assert.DoesNotContain("n_api", output);
    }

    [Fact]
    public async Task Only_layout_moved_the_default_reports_the_moves_and_ignore_layout_reports_layout_only_changes_and_exits_zero()
    {
        string before = Write("before.dsl", Order);
        string after = Write("after.dsl", Order
            .Replace("@ 40,40 320x80", "@ 60,40 320x80", StringComparison.Ordinal)
            .Replace("waypoints=[480,240]", "waypoints=[500,260]", StringComparison.Ordinal));

        (int detailedExit, string detailed, _) = await Run("diff", before, after);
        (int collapsedExit, string collapsed, _) = await Run("diff", before, after, "--ignore-layout");

        Assert.Equal(1, detailedExit);
        Assert.Contains("Node `Web app` moved from (40, 40) to (60, 40)", detailed);
        Assert.Contains("Edge `Orders API` to `Redis` rerouted (waypoints changed)", detailed);
        Assert.DoesNotContain("layout-only", detailed);

        Assert.Equal(0, collapsedExit);
        Assert.Contains("No semantic changes; 2 layout-only changes.", collapsed);
        Assert.DoesNotContain("moved from", collapsed);
    }

    [Fact]
    public async Task The_lines_every_save_rewrites_are_reported_apart_and_do_not_make_the_diff_fail()
    {
        string before = Write("before.dsl", Order);
        string after = Write("after.dsl", Order
            .Replace("updated=2026-09-20T22:05:00.000Z", "updated=2026-10-02T09:00:00.000Z", StringComparison.Ordinal)
            .Replace("provenance claude-code updated 2026-09-20T22:05:00.000Z", "provenance claude-code updated 2026-10-02T09:00:00.000Z", StringComparison.Ordinal));

        (int exit, string output, _) = await Run("diff", before, after);

        Assert.Equal(0, exit);
        Assert.Contains("No changes.", output);
        Assert.Contains("Not counted as changes", output);
        Assert.Contains("Last saved time changed", output);
    }

    [Fact]
    public async Task The_json_format_carries_the_verdict_and_the_sentences()
    {
        string before = Write("before.dsl", Order);
        string after = Write("after.dsl", Order.Replace("title \"Order service\"", "title \"Orders\"", StringComparison.Ordinal));

        (int exit, string output, _) = await Run("diff", before, after, "--format", "json");

        Assert.Equal(1, exit);
        using JsonDocument json = JsonDocument.Parse(output);
        Assert.True(json.RootElement.GetProperty("hasChanges").GetBoolean());
        Assert.Equal("Title changed from `Order service` to `Orders`", json.RootElement.GetProperty("changes")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_file_that_does_not_parse_exits_two_and_names_the_file_line_and_column()
    {
        string good = Write("good.dsl", Order);
        string bad = Write("bad.dsl", "draughtsman 1\nnode\n");

        (int exit, string output, string error) = await Run("diff", good, bad);

        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, output);
        Assert.Contains($"{bad}:2:", error);
    }

    [Fact]
    public async Task A_missing_file_a_bad_format_or_the_wrong_number_of_files_exit_two()
    {
        string good = Write("good.dsl", Order);

        Assert.Equal(2, (await Run("diff", good, Path.Combine(_folder, "nope.dsl"))).Exit);
        Assert.Equal(2, (await Run("diff", good, good, "--format", "yaml")).Exit);
        Assert.Equal(2, (await Run("diff", good)).Exit);
        Assert.Equal(2, (await Run("diff", good, good, "--bogus")).Exit);
        Assert.Equal(2, (await Run("check")).Exit);
    }

    [Fact]
    public async Task Two_elements_with_one_id_are_refused_by_the_parser_with_exit_two()
    {
        string good = Write("good.dsl", Order);
        string twice = Write("twice.dsl", Order.Replace("node n_db database \"Postgres\"", "node n_web database \"Postgres\"", StringComparison.Ordinal));

        (int exit, _, string error) = await Run("diff", good, twice);

        Assert.Equal(2, exit);
        Assert.Contains("was already used", error);
    }

    [Fact]
    public async Task Check_passes_a_clean_diagram_with_exit_zero()
    {
        (int exit, string output, _) = await Run("check", Write("ok.dsl", Order));

        Assert.Equal(0, exit);
        Assert.Contains("no problems found", output);
    }

    [Fact]
    public async Task Check_exits_one_for_an_error_severity_finding_and_names_the_rule()
    {
        string path = Write("dangling.dsl", Order + "edge e_9 flow n_web -> n_missing\n");

        (int exit, string output, _) = await Run("check", path);

        Assert.Equal(1, exit);
        Assert.Contains("error [dangling-edge]", output);
        Assert.Contains($"{path}:", output);
        Assert.Contains("warning:", output);
    }

    [Fact]
    public async Task Check_reports_a_warning_severity_finding_without_failing()
    {
        string path = Write("lonely.dsl", Order.Replace("node n_web service \"Web app\"", "node n_lonely service \"Lonely\" @ 700,40 160x80\nnode n_web service \"Web app\"", StringComparison.Ordinal));

        (int exit, string output, _) = await Run("check", path);

        Assert.Equal(0, exit);
        Assert.Contains("warning [isolated-node]", output);
        Assert.Contains("0 errors, 1 warning", output);
    }

    [Fact]
    public async Task Check_json_lists_parse_warnings_and_findings()
    {
        string path = Write("dangling.dsl", Order + "edge e_9 flow n_web -> n_missing\n");

        (int exit, string output, _) = await Run("check", path, "--format", "json");

        Assert.Equal(1, exit);
        using JsonDocument json = JsonDocument.Parse(output);
        Assert.True(json.RootElement.GetProperty("errors").GetInt32() >= 1);
        Assert.True(json.RootElement.GetProperty("parseWarnings").GetArrayLength() >= 1);
        Assert.Contains(json.RootElement.GetProperty("findings").EnumerateArray(), f => f.GetProperty("rule").GetString() == "dangling-edge");
    }

    [Fact]
    public async Task Check_exits_two_when_the_file_does_not_parse()
    {
        (int exit, _, string error) = await Run("check", Write("bad.dsl", "draughtsman 1\nnode\n"));

        Assert.Equal(2, exit);
        Assert.Contains("bad.dsl:2:", error);
    }
}
