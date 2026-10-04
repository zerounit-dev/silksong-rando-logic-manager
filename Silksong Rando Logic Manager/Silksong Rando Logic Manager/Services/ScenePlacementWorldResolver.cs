namespace Silksong_Rando_Logic_Manager.Services;

// Bounded SW1 placement proof, not a gameplay/Boolean/FSM interpreter.
public static class ScenePlacementWorldResolver
{
    public static void Apply(SceneDumpReview review)
    {
        var nodes = Flatten(review.RootObjects).ToArray();
        var identities = nodes.Where(node => node.CaptureInstanceId is not null)
            .GroupBy(node => node.CaptureInstanceId!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var root in review.RootObjects) Resolve(root, [], identities);
    }

    private static void Resolve(SceneDumpObject node, List<SceneDumpObject> ancestors,
        Dictionary<int, SceneDumpObject[]> identities)
    {
        ancestors.Add(node);
        List<ScenePlayerDataCondition> conditions = [];
        List<string> uncertainty = [];
        bool black = false, normal = false, affected = false;
        // Nearest proof is first in compact evidence; every outer/self gate participates.
        foreach (var owner in ancestors.AsEnumerable().Reverse())
        {
            if (!owner.HasReliableWorldComponents) uncertainty.Add($"{owner.Id}: incomplete component evidence");
            foreach (var component in owner.Components)
            {
                if (component.WorldEvidence is not { } evidence) continue;
                var source = component.Type;
                if (source == "BlackThreadState")
                {
                    affected = true;
                    if (!evidence.Valid) uncertainty.Add($"{owner.Id}: uncertain {source}");
                    continue;
                }
                if (!owner.HasReliableWorldComponents || !evidence.Valid)
                { uncertainty.Add($"{owner.Id}: unproven {source}"); continue; }
                if (source == "PlayMakerFSM")
                {
                    if (evidence.HasFsmUncertainty) uncertainty.Add($"{owner.Id}: unproven FSM action/parameter evidence");
                    foreach (var test in evidence.FsmTests)
                    {
                        conditions.Add(new(test.Field, null, owner.Id, $"{source}/{test.State}/action[{test.ActionIndex}]", test.IsTrue, test.IsFalse));
                        affected |= test.Field == "blackThreadWorld";
                    }
                    continue;
                }
                if (source == "TestGameObjectActivator")
                {
                    var active = Target(evidence.Activate, identities);
                    var inactive = Target(evidence.Deactivate, identities);
                    if (active is null || inactive is null || active.Id == inactive.Id ||
                        active.ParentId != owner.Id || inactive.ParentId != owner.Id)
                    { uncertainty.Add($"{owner.Id}: unproven owned sibling references"); continue; }
                    if (evidence.Test is not { } test) continue;
                    var namedPair = active.Name == "Black Thread World" && inactive.Name == "Normal World" ||
                        active.Name == "Normal World" && inactive.Name == "Black Thread World";
                    if (test is { Field: "blackThreadWorld", Value: true } && namedPair && active.Name != "Black Thread World")
                    { uncertainty.Add($"{owner.Id}: named world pair has reversed true wiring"); continue; }
                    bool? required = ancestors.Contains(active) ? test.Value : ancestors.Contains(inactive) ? !test.Value : null;
                    if (required is { } value)
                    {
                        conditions.Add(new(test.Field, value, owner.Id, source));
                        Restrict(test.Field, value, ref black, ref normal);
                    }
                    continue;
                }
                var target = evidence.GateTarget is { IsNull: true } ? owner : Target(evidence.GateTarget, identities);
                if (target is null)
                { uncertainty.Add($"{owner.Id}: unresolved {source} target"); continue; }
                if (!ancestors.Contains(target)) continue;
                if (evidence.BoolName is not { } field) continue;
                var polarity = source is "DeactivateIfPlayerdataFalse" or "ActivateIfPlayerdataTrue";
                conditions.Add(new(field, polarity, owner.Id, source));
                if (source is "DeactivateIfPlayerdataFalse" or "DeactivateIfPlayerdataTrue")
                    Restrict(field, polarity, ref black, ref normal);
                else affected |= field == "blackThreadWorld";
            }
        }
        if (black && normal) uncertainty.Add("Conflicting exclusive world restrictions");
        node.PlacementWorld = black && normal ? ScenePlacementWorldAvailability.Unknown
            : black ? ScenePlacementWorldAvailability.BlackThreadOnly
            : normal ? ScenePlacementWorldAvailability.NormalOnly
            : affected ? ScenePlacementWorldAvailability.Act3AffectedOrBoth : ScenePlacementWorldAvailability.Unknown;
        node.PlayerDataConditions = conditions.ToArray();
        node.WorldUncertainty = uncertainty.ToArray();
        foreach (var child in node.Children) Resolve(child, ancestors, identities);
        ancestors.RemoveAt(ancestors.Count - 1);
    }

    private static void Restrict(string field, bool value, ref bool black, ref bool normal)
    {
        if (field != "blackThreadWorld") return;
        if (value) black = true; else normal = true;
    }

    private static SceneDumpObject? Target(SceneWorldReference? reference, Dictionary<int, SceneDumpObject[]> identities)
    {
        if (reference is not { Valid: true, IsNull: false, Type: "UnityEngine.GameObject", InstanceId: { } id } ||
            !identities.TryGetValue(id, out var matches) || matches.Length != 1) return null;
        var node = matches[0];
        return node.HasReliableWorldIdentity && (reference.Name is null || reference.Name == node.Name) ? node : null;
    }

    public static string EnemyRequirements(SceneDumpObject node) => node.PlacementWorld switch
    {
        ScenePlacementWorldAvailability.BlackThreadOnly => "Black Thread World Spawn",
        ScenePlacementWorldAvailability.NormalOnly => "Normal World Spawn",
        _ => string.Empty
    };

    private static IEnumerable<SceneDumpObject> Flatten(IEnumerable<SceneDumpObject> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}
