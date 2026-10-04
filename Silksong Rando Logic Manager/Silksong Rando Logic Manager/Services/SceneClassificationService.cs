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
            Apply(root, false, false, false);
        }
    }

    private static void Apply(SceneDumpObject node, bool underCorpse, bool blackThreadWorld, bool normalWorld)
    {
        underCorpse |= node.Components.Any(component => component.Type == "ActiveCorpse");
        blackThreadWorld |= node.Name == "Black Thread World";
        normalWorld |= node.Name == "Normal World";
        node.IsAct3Only = blackThreadWorld && !normalWorld;
        var actor = !underCorpse &&
            node.Components.Any(component => component.Type == "HealthManager") &&
            !node.Components.Any(component => component.Type == "HealthManager" && component.RedirectedHealthReceiver) &&
            node.Components.Any(component => component.Type == "EnemyHitEffectsRegular") &&
            node.Components.Any(component => component.Type is "EnemyDeathEffectsRegular" or "EnemyDeathEffectsNoEffect");
        var journals = node.Components.SelectMany(component => component.JournalNames).Distinct(StringComparer.Ordinal).ToArray();
        node.IsEnemyActorCandidate = actor;
        node.JournalName = !node.Components.Any(component => component.MalformedJournal) && journals.Length == 1 ? journals[0] : null;
        node.CandidateReasons.Clear();
        node.Classification = node.Components.Any(component => component.Type == "TransitionPoint")
            ? SceneDumpClassification.Exit
            : actor ? SceneDumpClassification.Enemy : SceneDumpClassification.Other;
        if (actor) node.CandidateReasons.Add("Direct enemy actor structure (spawn placement unverified)");

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
            Apply(child, underCorpse, blackThreadWorld, normalWorld);
        }
    }
}

public static class SceneDumpIdentity
{
    public static bool IsImportable(SceneDumpClassification classification) =>
        classification is SceneDumpClassification.Exit or SceneDumpClassification.Check or SceneDumpClassification.Enemy;

    public static string? GameId(SceneDumpObject node) => node.Classification switch
    {
        SceneDumpClassification.Exit => node.Name,
        SceneDumpClassification.Enemy => node.Name.EndsWith("(Clone)", StringComparison.Ordinal)
            ? node.Name[..^7].TrimEnd() : node.Name,
        SceneDumpClassification.Check => node.Components.Select(component => component.ItemDataId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) ?? node.Name,
        _ => node.Components.Select(component => component.ItemDataId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
    };
}
