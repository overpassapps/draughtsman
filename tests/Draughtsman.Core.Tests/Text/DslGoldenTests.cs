using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests.Text;

/// <summary>The Order service example from spec section 5 in its JSON and DSL forms must be the same document.</summary>
public class DslGoldenTests
{
    /// <summary>Verbatim from docs/spec/build-spec-v0.1.md section 5.</summary>
    private const string SpecExample = """
        draughtsman 1
        type architecture
        title "Order service"

        node n_web   service  "Web app"        @ 40,40 320x80
        node n_api   service  "Orders API"     @ 40,200 320x80  owner="platform"
        node n_db    database "Postgres"       @ 40,360
        node n_cache queue    "Redis"          @ 400,360
        container c_vpc "VPC" { n_api n_db n_cache }

        edge e_1 flow n_web -> n_api  "HTTPS"
        edge e_2 data n_api -> n_db   "read/write"
        edge e_3 data n_api -> n_cache
        """;

    [Fact]
    public void Example_dsl_parses_to_the_example_json_document()
    {
        Document expected = DocumentJson.Deserialize(ContractFiles.ReadExample());

        ParseResult result = Dsl.Parse(ContractFiles.ReadExampleDsl());

        Assert.Empty(result.Warnings);
        DocumentAssert.Equivalent(expected, result.Document, "docs/examples/order-service.dsl does not parse to docs/examples/order-service.json.");
    }

    [Fact]
    public void Example_json_prints_byte_identical_to_the_example_dsl()
    {
        Document document = DocumentJson.Deserialize(ContractFiles.ReadExample());

        string printed = Dsl.Print(document);

        Assert.Equal(ContractFiles.ReadExampleDsl(), printed);
    }

    [Fact]
    public void Example_dsl_round_trips_through_parse_and_print()
    {
        string golden = ContractFiles.ReadExampleDsl();

        ParseResult parsed = Dsl.Parse(golden);

        Assert.Equal(golden, Dsl.Print(parsed.Document));
    }

    [Fact]
    public void Spec_section_5_snippet_parses_to_the_example_graph()
    {
        Document expected = DocumentJson.Deserialize(ContractFiles.ReadExample());

        ParseResult result = Dsl.Parse(SpecExample);
        Document actual = result.Document;

        Assert.Empty(result.Warnings);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(DocumentType.Architecture, actual.Type);
        Assert.Equal(expected.Nodes.Select(n => n.Id).Order(), actual.Nodes.Select(n => n.Id).Order());
        Assert.Equal(expected.Edges.Select(e => e.Id), actual.Edges.Select(e => e.Id));

        // The snippet carries less than the JSON (no ports, routing, container geometry or meta), so compare what it does say.
        foreach (Node expectedNode in expected.Nodes)
        {
            Node actualNode = Assert.Single(actual.Nodes, n => n.Id == expectedNode.Id);
            Assert.Equal(expectedNode.Kind, actualNode.Kind);
            Assert.Equal(expectedNode.Label, actualNode.Label);
            Assert.Equal(expectedNode.Shape, actualNode.Shape);
            Assert.Equal(expectedNode.Parent, actualNode.Parent);
            if (expectedNode.Id != "c_vpc")
            {
                Assert.Equal(expectedNode.Geometry, actualNode.Geometry);
            }
        }

        Assert.Null(Assert.Single(actual.Nodes, n => n.Id == "c_vpc").Geometry);
        Assert.Equal("platform", Assert.Single(actual.Nodes, n => n.Id == "n_api").Props!["owner"].GetString());

        foreach (Edge expectedEdge in expected.Edges)
        {
            Edge actualEdge = Assert.Single(actual.Edges, e => e.Id == expectedEdge.Id);
            Assert.Equal(expectedEdge.Kind, actualEdge.Kind);
            Assert.Equal(expectedEdge.Label, actualEdge.Label);
            Assert.Equal(expectedEdge.From.Node, actualEdge.From.Node);
            Assert.Equal(expectedEdge.To.Node, actualEdge.To.Node);
        }
    }

    [Fact]
    public void Spec_snippet_is_valid_against_the_schema_once_parsed()
    {
        Document document = Dsl.Parse(SpecExample).Document;

        SchemaCheck results = ContractFiles.Evaluate(DocumentJson.Serialize(document));

        Assert.True(results.IsValid, ContractFiles.DescribeErrors(results));
    }
}
