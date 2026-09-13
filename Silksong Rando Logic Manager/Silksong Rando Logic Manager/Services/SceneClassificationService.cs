namespace Silksong_Rando_Logic_Manager.Services;

public sealed class SceneClassificationService
{
    private static readonly HashSet<string> HighValueTypes = new(StringComparer.Ordinal)
    {
        "Gate",
        "BellBench",
        "RestBench",
        "InteractEvents",
        "LiftPlatform",
        "PlayMakerNPC",
        "RosaryCacheString",
        "GeoControl",
        "BreakableHolder",
        "DeactivateIfPlayerdataFalse"
    };

    public void Apply(SceneDumpReview review)
    {
        foreach (var root in review.RootObjects)
        {
            Apply(root);
        }
    }

    private static void Apply(SceneDumpObject node)
    {
        node.CandidateReasons.Clear();
        node.Classification = node.Components.Any(component => component.Type == "TransitionPoint")
            ? SceneDumpClassification.Exit
            : SceneDumpClassification.Other;

        foreach (var component in node.Components)
        {
            if (component.Type.StartsWith("Persistent", StringComparison.Ordinal))
            {
                node.CandidateReasons.Add(component.ItemDataId is { Length: > 0 }
                    ? $"{component.Type}: {component.ItemDataId}"
                    : $"{component.Type}: [no ID]");
            }
            else if (HighValueTypes.Contains(component.Type))
            {
                node.CandidateReasons.Add(component.Type);
            }
            else if (component.Type == "Breakable")
            {
                node.CandidateReasons.Add("Breakable (low confidence)");
            }
        }

        foreach (var child in node.Children)
        {
            Apply(child);
        }
    }
}
