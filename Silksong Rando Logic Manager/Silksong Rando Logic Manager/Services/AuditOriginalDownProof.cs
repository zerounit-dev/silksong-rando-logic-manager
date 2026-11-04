using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal static class AuditOriginalDownProof
{
    internal static AuditRelation PublicationScope(AuditRequirementCompilation compilation, CancellationToken token)
    {
        var program = compilation.Program; var arena = compilation.Arena; var admitted = compilation.Complete.Admitted;
        var candidates = program.Nodes.Where(node => node.Kind == AuditProgramKind.Application).Where(node =>
        {
            var occurrence = program.Room.Source.Occurrences[node.Occurrence!.Value.Index];
            return program.Room.Bindings.Applications[occurrence.Application.Index].Presentation.ExplicitAnyGeneratedDown;
        }).Select(node => program.Nodes[node.Children[0]]).Where(ExactExpansion).ToArray();
        bool ExactExpansion(AuditProgramNode expansion)
        {
            if (expansion.Kind != AuditProgramKind.Alternative || expansion.Children.Length != 3 || expansion.Choice is not { } choice || program.Schema.Domains[choice] != 4) return false;
            if (program.Nodes[expansion.Children[0]].Condition != new AuditConstraint(new(AuditConditionKind.Fact, "move:dash:left"), 1) ||
                program.Nodes[expansion.Children[1]].Condition != new AuditConstraint(new(AuditConditionKind.Fact, "move:dash:right"), 1)) return false;
            var down = program.Nodes[expansion.Children[2]];
            return down.Kind == AuditProgramKind.BoundAll && down.Children.Length == 3 &&
                down.Children.Select(child => program.Nodes[child].Condition).SequenceEqual(new AuditConstraint?[]
                {
                    new(new(AuditConditionKind.Fact, "move:dash:run"), 1), new(new(AuditConditionKind.Fact, "move:dash:left"), 1),
                    new(new(AuditConditionKind.Fact, "move:dash:right"), 1)
                });
        }
        var activeCandidates = candidates.Where(node => !arena.And(admitted, arena.EqualsValue(node.Choice!.Value, 3)).IsEmpty).ToArray();
        if (activeCandidates.Length == 0) return admitted;
        var raw = new Dictionary<AuditConditionKey, List<(int Value, AuditRelation Active)>>();
        void Collect(int index, AuditRelation active)
        {
            token.ThrowIfCancellationRequested(); var node = program.Nodes[index];
            if (node.Condition is { } condition && !(condition.Key.Kind == AuditConditionKind.Level && condition.Value == 0))
            {
                if (!raw.TryGetValue(condition.Key, out var leaves)) { leaves = []; raw.Add(condition.Key, leaves); }
                leaves.Add((condition.Value, active));
            }
            for (var child = 0; child < node.Children.Length; child++)
                Collect(node.Children[child], node.Kind == AuditProgramKind.Alternative ?
                    arena.And(active, arena.EqualsValue(node.Choice!.Value, child + 1)) : active);
        }
        Collect(program.Root, admitted);
        var rejected = arena.False;
        var originals = new Dictionary<AuditConditionKey, Dictionary<int, AuditRelation>>();
        // Absence is -1 ONLY in these finite per-key case maps, never a source
        // domain value. Original contradictions are excluded for proof only.
        foreach (var (key, leaves) in raw)
        {
            var cases = new Dictionary<int, AuditRelation> { [-1] = admitted };
            foreach (var (value, active) in leaves)
            {
                token.ThrowIfCancellationRequested(); var next = new Dictionary<int, AuditRelation>();
                void Add(int result, AuditRelation when) => next[result] = arena.Or(next.GetValueOrDefault(result, arena.False), when);
                foreach (var (previous, when) in cases)
                {
                    Add(previous, arena.Difference(when, active));
                    var present = arena.And(when, active);
                    var merged = AuditConstraintNormalization.Merge(key, previous < 0 ? null : previous, value);
                    if (merged.Compatible) Add(merged.Value!.Value, present); else rejected = arena.Or(rejected, present);
                }
                cases = next;
            }
            originals.Add(key, cases);
        }
        var proof = arena.Difference(admitted, rejected);
        var dash = arena.False;
        foreach (var side in new[] { "move:dash:left", "move:dash:right" })
            if (originals.TryGetValue(new(AuditConditionKind.Fact, side), out var cases))
                foreach (var (value, when) in cases) if (value >= 0) dash = arena.Or(dash, when);
        if (originals.TryGetValue(new(AuditConditionKind.Fact, "move:dash:run"), out var runs))
        {
            var removed = arena.False;
            foreach (var value in runs.Keys.Where(value => value >= 0).ToArray())
            { removed = arena.Or(removed, arena.And(runs[value], dash)); runs[value] = arena.Difference(runs[value], dash); }
            runs[-1] = arena.Or(runs.GetValueOrDefault(-1, arena.False), removed);
        }
        foreach (var cases in originals.Values)
            foreach (var value in cases.Keys.ToArray()) cases[value] = arena.And(cases[value], proof);
        var remaining = admitted;
        foreach (var candidate in activeCandidates)
        {
            token.ThrowIfCancellationRequested();
            var inside = Enumerable.Range(candidate.FirstChoice, candidate.EndChoice - candidate.FirstChoice).ToImmutableArray();
            var downMembers = arena.And(proof, arena.EqualsValue(candidate.Choice!.Value, 3));
            for (var side = 1; side <= 2; side++)
            {
                var selected = arena.And(proof, arena.EqualsValue(candidate.Choice!.Value, side));
                var unique = arena.Exists(selected, inside); var incompatible = arena.False;
                foreach (var (key, cases) in originals)
                {
                    token.ThrowIfCancellationRequested();
                    var values = cases.Select(pair => (Value: pair.Key, When: arena.Exists(arena.And(selected, pair.Value), inside))).ToArray();
                    var ambiguous = arena.False;
                    for (var a = 0; a < values.Length; a++)
                        for (var b = a + 1; b < values.Length; b++) ambiguous = arena.Or(ambiguous, arena.And(values[a].When, values[b].When));
                    unique = arena.Difference(unique, ambiguous);
                    foreach (var (value, when) in values)
                    {
                        if (value < 0) continue;
                        var covered = arena.False;
                        foreach (var (stronger, present) in cases)
                            if (stronger >= 0 && AuditFixedHeaders.CoversSameKey(new(key, value), new(key, stronger))) covered = arena.Or(covered, present);
                        if (key.Kind == AuditConditionKind.Fact && key.Key == "move:dash:run")
                            foreach (var dashSide in new[] { "move:dash:left", "move:dash:right" })
                                if (originals.TryGetValue(new(AuditConditionKind.Fact, dashSide), out var sideCases) && sideCases.TryGetValue(1, out var positive)) covered = arena.Or(covered, positive);
                        incompatible = arena.Or(incompatible, arena.Difference(when, covered));
                    }
                }
                // Side uniqueness/cases use COMPLETE ORIGINAL proof, never the
                // evolving publication filter, fixed headers or current facts.
                var suppressed = arena.And(downMembers, arena.Difference(unique, incompatible));
                remaining = arena.Difference(remaining, suppressed);
            }
        }
        token.ThrowIfCancellationRequested(); return remaining;
    }
}
