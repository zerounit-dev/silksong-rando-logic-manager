using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class SceneClassificationServiceTests
{
    [Fact]
    public void Apply_ClassifiesOnlyExactTransitionPointAndKeepsOtherSignalsAsEvidence()
    {
        var exit = Node([new SceneDumpComponent("TransitionPoint", null, null, null)]);
        var candidates = Node([
            new SceneDumpComponent("PersistentItem", "check-id", null, null),
            new SceneDumpComponent("Gate", null, null, null),
            new SceneDumpComponent("Breakable", null, null, null),
            new SceneDumpComponent("PlayMakerFSM", null, null, null),
            new SceneDumpComponent("CustomTransitionPoint", null, null, null)
        ]);
        var noId = Node([new SceneDumpComponent("PersistentSwitch", "", null, null)]);
        var review = new SceneDumpReview("Room", [exit, candidates, noId], []);

        new SceneClassificationService().Apply(review);

        Assert.Equal(SceneDumpClassification.Exit, exit.Classification);
        Assert.Equal(SceneDumpClassification.Other, candidates.Classification);
        Assert.Equal(["PersistentItem: check-id", "Gate", "Breakable (low confidence)"], candidates.CandidateReasons);
        Assert.Equal(SceneDumpClassification.Other, noId.Classification);
        Assert.Equal(["PersistentSwitch: [no ID]"], noId.CandidateReasons);
    }

    [Fact]
    public void Apply_ReplacesPriorUserOrRuleStateAcrossTheHierarchy()
    {
        var child = Node([new SceneDumpComponent("BellBench", null, null, null)]);
        child.Classification = SceneDumpClassification.Check;
        child.CandidateReasons.Add("old");
        var root = new SceneDumpObject("Root", null, null, null, null, null, null, [], [child]);

        new SceneClassificationService().Apply(new SceneDumpReview("Room", [root], []));

        Assert.Equal(SceneDumpClassification.Other, child.Classification);
        Assert.Equal(["BellBench"], child.CandidateReasons);
    }

    private static SceneDumpObject Node(IReadOnlyList<SceneDumpComponent> components) =>
        new("Node", null, null, null, null, null, null, components, []);
}
