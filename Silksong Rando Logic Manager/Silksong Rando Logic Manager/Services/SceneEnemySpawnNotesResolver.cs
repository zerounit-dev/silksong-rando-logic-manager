namespace Silksong_Rando_Logic_Manager.Services;

// EPN3 finite documentation/presence evidence. Never used as SW1 world proof.
public static class SceneEnemySpawnNotesResolver
{
    public static void Apply(SceneDumpReview review)
    {
        var nodes = Flatten(review.RootObjects).ToArray();
        var identities = nodes.Where(node => node.CaptureInstanceId is not null)
            .GroupBy(node => node.CaptureInstanceId!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        var inbound = new Dictionary<Guid, List<(bool Direct, SceneBooleanTest Fact)>>();
        var presence = new HashSet<Guid>();
        var uncertain = new HashSet<Guid>();
        // Inbound lists retain source preorder/component order for equal target depth.
        foreach (var owner in nodes)
        {
            if (!owner.HasReliableWorldComponents || owner.Components.Any(component => !component.HasReliableConditionCoverage)) uncertain.Add(owner.Id);
            foreach (var component in owner.Components)
            {
                if (component.EnemyConditionEvidence is not { } condition || component.WorldEvidence is not { } evidence) continue;
                if (!owner.HasReliableWorldComponents) continue;
                if (!condition.HasReliableTargets) { uncertain.Add(owner.Id); continue; }
                if (component.Type == "TestGameObjectActivator")
                {
                    var active = Target(evidence.Activate, identities);
                    var inactive = Target(evidence.Deactivate, identities);
                    var referencesValid = evidence.Activate is { Valid: true } && evidence.Deactivate is { Valid: true } &&
                        (evidence.Activate.IsNull || active is not null) && (evidence.Deactivate.IsNull || inactive is not null) &&
                        (active is not null || inactive is not null) &&
                        (active is null || inactive is null || active.Id != inactive.Id);
                    if (!referencesValid) uncertain.Add(owner.Id);
                    if (active is not null) Record(owner, active, condition, referencesValid ? condition.Facts : [], !referencesValid);
                    if (inactive is not null)
                    {
                        // Only the existing complete pure single-Bool schema permits inversion.
                        IReadOnlyList<SceneBooleanTest> inverse = referencesValid && evidence is { NotesValid: true, Test: { } test }
                            ? [new(test.Field, !test.Value)] : [];
                        Record(owner, inactive, condition, inverse, !referencesValid);
                    }
                }
                else
                {
                    var target = evidence.GateTarget is { Valid: true, IsNull: true } ? owner : Target(evidence.GateTarget, identities);
                    if (target is null) uncertain.Add(owner.Id);
                    else Record(owner, target, condition, condition.Facts, false);
                }
            }
        }
        foreach (var root in review.RootObjects) Resolve(root, [], false, true, null, false, false, false);

        void Record(SceneDumpObject owner, SceneDumpObject target, SceneEnemyConditionEvidence evidence, IReadOnlyList<SceneBooleanTest> facts, bool badReference)
        {
            if (evidence.HasConditions) presence.Add(target.Id);
            if (evidence.Incomplete || badReference) uncertain.Add(target.Id);
            if (!inbound.TryGetValue(target.Id, out var conditions)) inbound[target.Id] = conditions = [];
            conditions.AddRange(facts.Where(test => test.Field != "blackThreadWorld").Select(test => (owner.Id == target.Id, test)));
        }

        void Resolve(SceneDumpObject node, IReadOnlyList<SceneBooleanTest> outer, bool conditionsAbove, bool isRoot,
            bool? parentHierarchy, bool disabledSelfAbove, bool disabledHierarchyAbove, bool contradictoryAbove)
        {
            // Actor then nearest-to-outer governed ancestry; no sibling/descendant borrowing.
            node.SpawnConditions = inbound.TryGetValue(node.Id, out var own)
                ? own.OrderByDescending(item => item.Direct).Select(item => item.Fact).Concat(outer).Distinct().ToArray() : outer;
            var hasConditionsOrUncertainty = conditionsAbove || presence.Contains(node.Id) || uncertain.Contains(node.Id);
            // Preserve the approved whole-chain reliable-activity contradiction algebra.
            var self = node.HasReliableActivity ? node.ActiveSelf : null;
            var hierarchy = node.HasReliableActivity ? node.ActiveInHierarchy : null;
            var contradictory = contradictoryAbove ||
                hierarchy == true && (self == false || disabledSelfAbove || disabledHierarchyAbove) ||
                self == true && hierarchy == false && (isRoot || parentHierarchy == true);
            node.InactiveEnemyReason = !contradictory && hierarchy == false && !hasConditionsOrUncertainty && (self == false || disabledSelfAbove)
                ? self == false ? "Disabled; no activation found." : "Disabled ancestor; no activation found."
                : string.Empty;
            foreach (var child in node.Children)
                Resolve(child, node.SpawnConditions, hasConditionsOrUncertainty, false, hierarchy, disabledSelfAbove || self == false,
                    disabledHierarchyAbove || hierarchy == false, contradictory);
        }
    }

    public static string EnemyRequirements(SceneDumpObject node) => node.InactiveEnemyReason.Length > 0
        ? "Invalid" : ScenePlacementWorldResolver.EnemyRequirements(node);

    public static string EnemyNotes(SceneDumpObject node) => node.InactiveEnemyReason.Length > 0 ? node.InactiveEnemyReason
        : node.SpawnConditions.Count == 0 ? string.Empty
        : "Spawn conditions: " + string.Join(", ", node.SpawnConditions.Select(test => $"{test.Field}={(test.Value ? "true" : "false")}"));

    private static SceneDumpObject? Target(SceneWorldReference? reference, Dictionary<int, SceneDumpObject[]> identities)
    {
        if (reference is not { Valid: true, IsNull: false, Type: "UnityEngine.GameObject", InstanceId: { } id } || id == 0 ||
            !identities.TryGetValue(id, out var matches) || matches.Length != 1) return null;
        var node = matches[0];
        return node.HasReliableWorldIdentity && (reference.Name is null || reference.Name == node.Name) ? node : null;
    }

    private static IEnumerable<SceneDumpObject> Flatten(IEnumerable<SceneDumpObject> nodes) =>
        nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
}
