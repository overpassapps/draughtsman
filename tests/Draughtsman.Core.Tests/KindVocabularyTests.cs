using Draughtsman.Core.Enums;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Tests;

public class KindVocabularyTests
{
    [Theory]
    [InlineData(DocumentType.Flowchart)]
    [InlineData(DocumentType.Architecture)]
    [InlineData(DocumentType.Sequence)]
    public void Shipped_vocabularies_load(DocumentType type)
    {
        KindVocabulary vocabulary = KindVocabulary.For(type);

        Assert.Equal(type.ToString().ToLowerInvariant(), vocabulary.Type);
        Assert.NotEmpty(vocabulary.Description);
        Assert.NotEmpty(vocabulary.NodeKinds);
        Assert.NotEmpty(vocabulary.EdgeKinds);
        Assert.All(vocabulary.NodeKinds, k => Assert.False(string.IsNullOrWhiteSpace(k.DefaultShape), $"{k.Name} has no default shape"));
        Assert.Equal(vocabulary.NodeKinds.Count, vocabulary.NodeKinds.Select(k => k.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Freeform_has_no_vocabulary()
    {
        Assert.False(KindVocabulary.TryGet(DocumentType.Freeform, out _));
        Assert.Throws<KeyNotFoundException>(() => KindVocabulary.For(DocumentType.Freeform));
    }

    [Fact]
    public void Architecture_vocabulary_covers_the_spec_example()
    {
        KindVocabulary vocabulary = KindVocabulary.For(DocumentType.Architecture);

        Assert.True(vocabulary.IsKnownNodeKind("service"));
        Assert.True(vocabulary.IsKnownNodeKind("database"));
        Assert.True(vocabulary.IsKnownNodeKind("queue"));
        Assert.True(vocabulary.IsKnownNodeKind("container"));
        Assert.True(vocabulary.IsKnownEdgeKind("flow"));
        Assert.True(vocabulary.IsKnownEdgeKind("data"));
        Assert.False(vocabulary.IsKnownNodeKind("unicorn"));
    }

    [Fact]
    public void Raw_json_is_available_for_agents()
    {
        string json = KindVocabulary.ReadRawJson(DocumentType.Architecture);

        Assert.Contains("\"service\"", json, StringComparison.Ordinal);
        Assert.Contains("\"database\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_shipped_vocabulary_has_container_note_and_association()
    {
        // A sequence diagram has participants and messages only: no containers, notes or plain association (M2 adds notes).
        foreach (KindVocabulary vocabulary in KindVocabulary.All.Values.Where(v => v.Type != "sequence"))
        {
            Assert.True(vocabulary.IsKnownNodeKind("container"), $"{vocabulary.Type} lacks container");
            Assert.True(vocabulary.IsKnownNodeKind("note"), $"{vocabulary.Type} lacks note");
            Assert.True(vocabulary.IsKnownEdgeKind("association"), $"{vocabulary.Type} lacks association");
        }
    }

    [Fact]
    public void Flowchart_vocabulary_teaches_the_swimlane_kinds()
    {
        KindVocabulary vocabulary = KindVocabulary.For(DocumentType.Flowchart);

        KindDefinition pool = Assert.Single(vocabulary.NodeKinds, k => k.Name == SwimlaneShapes.PoolKind);
        Assert.False(string.IsNullOrWhiteSpace(pool.Description));
        Assert.Equal(SwimlaneShapes.PoolShape, pool.DefaultShape);
        Assert.NotNull(pool.Props);
        Assert.Contains("orientation", pool.Props!.Keys);

        KindDefinition lane = Assert.Single(vocabulary.NodeKinds, k => k.Name == SwimlaneShapes.LaneKind);
        Assert.False(string.IsNullOrWhiteSpace(lane.Description));
        Assert.Equal(SwimlaneShapes.LaneShape, lane.DefaultShape);
    }

    [Fact]
    public void Adding_swimlane_kinds_left_the_older_kinds_untouched()
    {
        KindVocabulary vocabulary = KindVocabulary.For(DocumentType.Flowchart);

        // A document saved before this change has none of the new kinds, and these older defaults are what it prints
        // and renders by; a stray edit to them would silently change every old document.
        Assert.Equal("rounded", Assert.Single(vocabulary.NodeKinds, k => k.Name == "process").DefaultShape);
        Assert.Equal("rectangle", Assert.Single(vocabulary.NodeKinds, k => k.Name == "container").DefaultShape);
        Assert.Equal(StateShapeLibrary.Prefix + "composite-state", Assert.Single(vocabulary.NodeKinds, k => k.Name == "composite-state").DefaultShape);
        Assert.False(SwimlaneShapes.IsSwimlaneKind("container"));
        Assert.False(SwimlaneShapes.IsSwimlane("rectangle"));
    }
}
