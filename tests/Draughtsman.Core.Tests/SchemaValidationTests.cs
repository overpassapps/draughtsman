using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>The JSON Schema in docs/schema is the shared contract; the C# model must produce and accept what it describes.</summary>
public class SchemaValidationTests
{
    /// <summary>
    /// The DSL parser restates the schema's shape vocabulary inside Core, where the schema file is not available.
    /// Both directions of drift break something: a shape only the schema knows is silently replaced by the kind's
    /// default and called unknown, and a shape only the parser knows produces a document the schema, and so the
    /// SPA, rejects - contradicting the invariant docs/api.md and docs/spec/dsl.md publish.
    /// </summary>
    [Fact]
    public void Dsl_shape_vocabulary_agrees_with_the_schema()
    {
        Assert.Equal(ShapeAlternatives(SchemaShapePattern()), ShapeAlternatives(DslSyntax.ShapePattern().ToString()));

        foreach (string shape in SchemaShapes())
        {
            ParseResult accepted = Dsl.Parse($"draughtsman 1\ntype flowchart\ntitle \"T\"\nnode n_a process \"A\" shape={shape}\n");

            Assert.Equal(shape, accepted.Document.Nodes[0].Shape);
            Assert.Empty(accepted.Warnings);
        }

        ParseResult rejected = Dsl.Parse("draughtsman 1\ntype flowchart\ntitle \"T\"\nnode n_a process \"A\" shape=star\n");

        Assert.NotEqual("star", rejected.Document.Nodes[0].Shape);
        Assert.Single(rejected.Warnings);
    }

    /// <summary>The example document with its first node's fill replaced, so everything else in it is known to be valid.</summary>
    private static string DocumentWithFill(string fill)
    {
        System.Text.Json.Nodes.JsonNode document = System.Text.Json.Nodes.JsonNode.Parse(ContractFiles.ReadExample())!;
        System.Text.Json.Nodes.JsonObject node = document["nodes"]![0]!.AsObject();
        node["style"] = new System.Text.Json.Nodes.JsonObject { ["fill"] = fill };
        return document.ToJsonString();
    }

    // AU-17: `colour` used to be any string, so `url(https://...)` could reach the SVG paint attributes of an export.
    [Theory]
    [InlineData("#fff")]
    [InlineData("#FFFFFF")]
    [InlineData("#ff00ff80")]
    [InlineData("red")]
    [InlineData("transparent")]
    [InlineData("rgb(12, 34, 56)")]
    [InlineData("rgba(12 34 56 / 50%)")]
    [InlineData("hsl(210deg 50% 40%)")]
    [InlineData("oklch(62% 0.2 250)")]
    [InlineData("token:accent")]
    [InlineData("token:palette.blue-2")]
    public void A_colour_literal_or_token_is_valid(string fill)
    {
        Assert.True(ContractFiles.Evaluate(DocumentWithFill(fill)).IsValid, ContractFiles.DescribeErrors(ContractFiles.Evaluate(DocumentWithFill(fill))));
    }

    [Theory]
    [InlineData("url(https://evil.example/x.svg#p)")]
    [InlineData("url(#grad)")]
    [InlineData("var(--chrome-accent)")]
    [InlineData("javascript:alert(1)")]
    [InlineData("red; stroke: url(https://evil.example/)")]
    [InlineData("#12")]
    [InlineData("#1234567")]
    [InlineData("rgb(1,2,3) url(x)")]
    [InlineData("rgb(expression(alert(1)))")]
    [InlineData("\"><script>")]
    [InlineData("token:a b")]
    [InlineData("")]
    public void Anything_else_in_a_colour_field_is_invalid(string fill)
    {
        Assert.False(ContractFiles.Evaluate(DocumentWithFill(fill)).IsValid);
    }

    [Fact]
    public void A_colour_longer_than_sixty_four_characters_is_invalid()
    {
        Assert.False(ContractFiles.Evaluate(DocumentWithFill("token:" + new string('a', 70))).IsValid);
    }

    /// <summary>Every literal the schema's shape pattern admits, with the custom:&lt;lib&gt;/&lt;name&gt; branch made concrete.</summary>
    private static IEnumerable<string> SchemaShapes()
    {
        foreach (string alternative in ShapeAlternatives(SchemaShapePattern()))
        {
            yield return alternative.StartsWith("custom:", StringComparison.Ordinal) ? "custom:my-lib/thing.v2" : alternative;
        }
    }

    /// <summary>The alternatives a shape pattern admits, with the two anchor spellings (<c>$</c> and <c>\z</c>) normalised away.</summary>
    private static SortedSet<string> ShapeAlternatives(string pattern)
    {
        string body = pattern.TrimStart('^');
        body = body.EndsWith("\\z", StringComparison.Ordinal) ? body[..^2] : body.TrimEnd('$');
        return new SortedSet<string>(body.Trim('(', ')').Split('|'), StringComparer.Ordinal);
    }

    private static string SchemaShapePattern()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(ContractFiles.SchemaPath));
        return schema.RootElement
            .GetProperty("$defs").GetProperty("node").GetProperty("properties").GetProperty("shape").GetProperty("pattern")
            .GetString()!;
    }

    [Theory]
    [InlineData("document")]
    [InlineData("predefined-process")]
    [InlineData("manual-input")]
    [InlineData("off-page-connector")]
    [InlineData("delay")]
    [InlineData("manual-operation")]
    [InlineData("display")]
    [InlineData("stored-data")]
    [InlineData("multi-document")]
    [InlineData("merge")]
    [InlineData("extract")]
    [InlineData("on-page-connector")]
    [InlineData("summing-junction")]
    [InlineData("or")]
    [InlineData("collate")]
    [InlineData("sort")]
    [InlineData("loop-limit")]
    [InlineData("internal-storage")]
    [InlineData("card")]
    public void Flowchart_shapes_validate_and_survive_json_and_dsl(string shape)
    {
        Document original = DocumentJsonTests.MinimalDocument() with
        {
            Nodes = [new Node("n_shape", "process", "Shape", shape)],
        };
        string json = DocumentJson.Serialize(original);
        Assert.True(ContractFiles.Evaluate(json).IsValid);
        Document restored = DocumentJson.Deserialize(json);
        Assert.Equal(shape, Assert.Single(restored.Nodes).Shape);
        ParseResult parsed = Dsl.Parse(Dsl.Print(restored));
        Assert.Empty(parsed.Warnings);
        Assert.Equal(shape, Assert.Single(parsed.Document.Nodes).Shape);
        Assert.Equal(json, DocumentJson.Serialize(parsed.Document));
        Assert.False(ContractFiles.Evaluate(json.Replace($"\"shape\":\"{shape}\"", "\"shape\":\"not-a-shape\"", StringComparison.Ordinal)).IsValid);
    }

    [Fact]
    public void Example_document_is_valid_against_schema()
    {
        SchemaCheck results = ContractFiles.Evaluate(ContractFiles.ReadExample());

        Assert.True(results.IsValid, ContractFiles.DescribeErrors(results));
    }

    [Fact]
    public void Serialised_csharp_document_is_valid_against_schema()
    {
        Document document = DocumentJsonTests.MinimalDocument() with
        {
            Theme = new Dictionary<string, string> { ["node.fill"] = "#FFFFFF" },
            Nodes =
            [
                new Node("c_1", "container", "Lane", "rectangle") { Geometry = new Geometry(0, 0, 400, 200) },
                new Node("n_1", "process", "Step", "rounded")
                {
                    Geometry = new Geometry(10, 10, 100, 50) { Rotation = 0 },
                    Parent = "c_1",
                    Style = new NodeStyle { Fill = "#FFF", Dash = Dash.Dashed, StrokeWidth = 2 },
                    Ports = [new Port("out", 1, 0.5)],
                },
                new Node("n_2", "decision", "Ok?", "diamond") { Geometry = new Geometry(200, 10, 100, 100), Parent = "c_1" },
            ],
            Edges =
            [
                new Edge("e_1", "flow", new Endpoint("n_1") { Port = "out" }, new Endpoint("n_2"))
                {
                    Label = "next",
                    Routing = new Routing(RoutingMode.Orthogonal) { Waypoints = [new Point(150, 35)] },
                    Style = new EdgeStyle { ArrowEnd = ArrowHead.Arrow, Dash = Dash.Dotted },
                },
            ],
        };
        document = document with
        {
            Meta = document.Meta with
            {
                AiProvenance = [new AiProvenance("claude-code", document.Meta.UpdatedAt, AiAction.Created, ["n_1", "e_1"]) { Model = "claude-fable-5-1" }],
            },
        };

        string json = DocumentJson.Serialize(document);
        SchemaCheck results = ContractFiles.Evaluate(json);

        Assert.True(results.IsValid, ContractFiles.DescribeErrors(results));
    }

    [Fact]
    public void Schema_rejects_unknown_top_level_member()
    {
        string json = ContractFiles.ReadExample().TrimEnd().TrimEnd('}') + ", \"picture\": \"data:...\" }";

        SchemaCheck results = ContractFiles.Evaluate(json);

        Assert.False(results.IsValid);
    }
}
