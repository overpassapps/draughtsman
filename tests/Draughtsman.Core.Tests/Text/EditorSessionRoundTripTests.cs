using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests.Text;

public class EditorSessionRoundTripTests
{
    [Theory]
    [InlineData("order-service.json")]
    [InlineData("editor-session.json")]
    public void Editor_models_round_trip_with_ids_geometry_and_all_fields(string fixture)
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", fixture));
        var before = DocumentJson.Deserialize(json);
        string text = Dsl.Print(before);
        var parsed = Dsl.Parse(text);
        Assert.Empty(parsed.Warnings);
        DocumentAssert.Equivalent(before, parsed.Document, fixture);
        Assert.Equal(text, Dsl.Print(parsed.Document));
        Assert.True(ContractFiles.Evaluate(DocumentJson.Serialize(parsed.Document)).IsValid);
    }
}
