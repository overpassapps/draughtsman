using System.Text.Json;
using System.Xml.Linq;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// Documents written by EARLIER builds (tests/golden-documents/README.md: the first scaffold commit and the first pushed commit,
/// copied byte for byte from git) must still load, validate, print as DSL, round-trip and render in the current build, and re-saving
/// them must not change what they say. A change to the document model that would stop an old customer's diagram opening, or silently
/// rewrite it on the next save, fails here by file name.
/// </summary>
public sealed class GoldenDocumentTests
{
    private static IEnumerable<string> AllFiles()
    {
        string root = Path.Combine(UpgradeFixturePaths.RepositoryRoot, "tests", "golden-documents");
        return Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'));
    }

    public static TheoryData<string> Files
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string file in AllFiles())
            {
                data.Add(file);
            }

            return data;
        }
    }

    private static string Read(string relative)
    {
        return File.ReadAllText(Path.Combine(UpgradeFixturePaths.RepositoryRoot, "tests", "golden-documents", relative));
    }

    [Fact]
    public void The_golden_set_includes_a_document_from_the_first_pushed_commit_and_from_the_first_scaffold()
    {
        Assert.Contains("first-push-9e22f56/order-service.json", AllFiles());
        Assert.Contains("m0-8d3b6bd/order-service.json", AllFiles());
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void An_old_document_validates_against_todays_schema(string file)
    {
        IReadOnlyList<string> errors = DocumentSchema.Validate(Read(file));

        Assert.True(errors.Count == 0, $"{file}: {string.Join("; ", errors)}");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void An_old_document_loads_and_re_saves_without_changing_what_it_says(string file)
    {
        string original = Read(file);

        Document loaded = DocumentJson.Deserialize(original);
        string saved = DocumentJson.Serialize(loaded);

        Assert.Equal(Normal(original), Normal(saved));
        // Saving what was loaded again changes nothing further: one normalisation at most, never a drift.
        Assert.Equal(saved, DocumentJson.Serialize(DocumentJson.Deserialize(saved)));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void An_old_document_prints_as_dsl_that_parses_back_to_the_same_document(string file)
    {
        Document loaded = DocumentJson.Deserialize(Read(file));

        string dsl = Dsl.Print(loaded);
        ParseResult parsed = Dsl.Parse(dsl);

        Assert.Equal(Normal(DocumentJson.Serialize(loaded)), Normal(DocumentJson.Serialize(parsed.Document)));
        Assert.Equal(dsl, Dsl.Print(parsed.Document));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void The_dsl_an_old_build_printed_for_a_document_is_still_what_this_build_prints(string file)
    {
        string dslFile = file[..^".json".Length] + ".dsl";
        if (!File.Exists(Path.Combine(UpgradeFixturePaths.RepositoryRoot, "tests", "golden-documents", dslFile)))
        {
            return;
        }

        // The golden DSL was printed by the earlier build itself; printing is canonical, so a change here means every saved
        // diagram's text export changes and any DSL diff in a customer's version control with it.
        Assert.Equal(Read(dslFile).ReplaceLineEndings("\n"), Dsl.Print(DocumentJson.Deserialize(Read(file))).ReplaceLineEndings("\n"));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void An_old_document_renders_with_every_node_drawn(string file)
    {
        Document loaded = DocumentJson.Deserialize(Read(file));

        string svg = SvgRenderer.Render(loaded).Svg;

        XDocument xml = XDocument.Parse(svg);
        HashSet<string> drawn = [.. xml.Descendants().Select(e => (string?)e.Attribute("data-id")).OfType<string>()];
        foreach (Node node in loaded.Nodes)
        {
            Assert.Contains(node.Id, drawn);
        }
    }

    /// <summary>Order-insensitive, whitespace-insensitive form of a JSON text, so two serialisations of one document compare equal.</summary>
    private static string Normal(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return Write(document.RootElement);
    }

    private static string Write(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"\"{p.Name}\":{Write(p.Value)}")) + "}",
            JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Write)) + "]",
            JsonValueKind.Number => e.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => e.GetRawText(),
        };
    }
}
