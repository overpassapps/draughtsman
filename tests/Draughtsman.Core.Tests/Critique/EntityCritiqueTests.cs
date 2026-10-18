using System.Text.Json;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Core.Models;

namespace Draughtsman.Core.Tests;

/// <summary>The <c>entity-without-primary-key</c> rule: a table none of whose column rows is marked PK.</summary>
public class EntityCritiqueTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static Node Table(string id, string shape, params string[] columns)
    {
        return new Node(id, shape.EndsWith("weak-entity", StringComparison.Ordinal) ? "weak-entity" : "entity", id, shape)
        {
            Props = new Dictionary<string, JsonElement> { ["columns"] = JsonSerializer.SerializeToElement(columns) },
        };
    }

    private static Document Doc(DocumentType type, params Node[] nodes)
    {
        return new Document("doc_ent", Document.CurrentSchemaVersion, "Ent", type, nodes, [], new Meta(Now, Now, "test"));
    }

    private static CritiqueFinding[] Findings(Document document)
    {
        return CritiqueAnalyser.Analyse(document).Where(f => f.Rule == CritiqueAnalyser.Rules.EntityWithoutPrimaryKey).ToArray();
    }

    [Fact]
    public void A_table_with_no_pk_row_is_a_warning_naming_it_and_a_table_with_one_is_not()
    {
        Document document = Doc(DocumentType.Architecture,
            Table("t_keyless", "custom:er/entity", "Name nvarchar(50) not null", "FK OwnerId int null"),
            Table("t_keyed", "custom:er/entity", "PK Id int not null", "Name nvarchar(50) not null"));

        CritiqueFinding finding = Assert.Single(Findings(document));

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(["t_keyless"], finding.NodeIds);
        Assert.Empty(finding.EdgeIds);
        Assert.Contains("t_keyless", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pk_on_any_row_counts_including_a_composite_key_and_a_pk_that_is_also_an_fk()
    {
        Document document = Doc(DocumentType.Architecture,
            Table("t_late", "custom:er/entity", "Name nvarchar(50) not null", "PK Id int not null"),
            Table("t_composite", "custom:er/weak-entity", "PK FK OrderId int not null", "PK Line int not null"),
            Table("t_lower", "custom:er/entity", "pk Id int not null"));

        Assert.Empty(Findings(document));
    }

    [Fact]
    public void A_weak_entity_and_a_table_with_no_columns_at_all_are_checked_too()
    {
        Document document = Doc(DocumentType.Architecture,
            Table("t_weak", "custom:er/weak-entity", "Line int not null"),
            Table("t_empty", "custom:er/entity"));

        Assert.Equal(new[] { "t_empty", "t_weak" }, Findings(document).Select(f => f.NodeIds.Single()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Only_entities_are_checked_and_a_freeform_document_is_checked_too()
    {
        var plain = new Node("n_box", "service", "Box", "rectangle");
        Document architecture = Doc(DocumentType.Architecture, plain, Table("t_keyless", "custom:er/entity", "Name text null"));
        Document freeform = Doc(DocumentType.Freeform, plain, Table("t_keyless", "custom:er/entity", "Name text null"));

        Assert.Equal(["t_keyless"], Assert.Single(Findings(architecture)).NodeIds);
        Assert.Equal(["t_keyless"], Assert.Single(Findings(freeform)).NodeIds);
    }

    [Fact]
    public void An_imported_table_without_a_primary_key_is_reported_and_one_with_a_key_is_not()
    {
        const string sql = """
            CREATE TABLE Keyed (Id int NOT NULL PRIMARY KEY);
            CREATE TABLE Heap (Note nvarchar(50), OwnerId int REFERENCES Keyed (Id));
            """;

        Document document = SqlImporter.Import(sql, new SqlImportOptions { Now = Now }).Document;

        Assert.Equal(["t_Heap"], Assert.Single(Findings(document)).NodeIds);
    }
}
