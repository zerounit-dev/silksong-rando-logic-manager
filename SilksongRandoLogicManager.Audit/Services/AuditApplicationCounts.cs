using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal readonly record struct AuditApplicationVariant(AuditApplicationHandle Application, int Refinement);
internal sealed record AuditActiveVariant(AuditOccurrenceHandle Occurrence, AuditApplicationVariant Variant, AuditRelation Active);
internal sealed record AuditApplicationCountFeature(AuditApplicationVariant Variant, int Bound, ImmutableArray<AuditRelation> EqualsCount);

// Typed source activation/count functions. These are finite count DPs, not
// occurrence subsets, complete bags or selected-tree histories.
internal sealed class AuditApplicationCounts
{
    internal AuditRequirementCompilation Compilation { get; }
    internal ImmutableArray<AuditActiveVariant> Activations { get; }
    internal ImmutableArray<AuditApplicationCountFeature> Features { get; }
    internal AuditFiniteSchema CountSchema { get; }
    internal AuditApplicationCounts(AuditRequirementCompilation compilation, CancellationToken token)
    {
        Compilation = compilation; var program = compilation.Program; var arena = compilation.Arena;
        var activations = ImmutableArray.CreateBuilder<AuditActiveVariant>();
        var order = new List<AuditApplicationVariant>(); var byVariant = new Dictionary<AuditApplicationVariant, List<AuditRelation>>();
        foreach (var node in program.Nodes.Where(node => node.Kind == AuditProgramKind.Application))
        {
            token.ThrowIfCancellationRequested();
            var occurrence = program.Room.Source.Occurrences[node.Occurrence!.Value.Index];
            var application = program.Room.Bindings.Applications[occurrence.Application.Index];
            if (application.Presentation.Disposition != AuditPresentationDisposition.Body) continue;
            foreach (var variant in application.Presentation.Variants)
            {
                var active = compilation.OccurrenceActive[occurrence.Handle];
                foreach (var selection in variant.Selections)
                {
                    var branch = program.Nodes[program.BoundNode(occurrence.Handle, selection.ChoicePath)];
                    if (branch.Kind != AuditProgramKind.Alternative || selection.Child < 0 || selection.Child >= branch.Children.Length)
                        throw new InvalidOperationException("Bound display refinement does not identify its original choice");
                    active = arena.And(active, arena.EqualsValue(branch.Choice!.Value, selection.Child + 1));
                }
                var identity = new AuditApplicationVariant(occurrence.Application, variant.Refinement);
                activations.Add(new(occurrence.Handle, identity, active));
                if (!byVariant.TryGetValue(identity, out var uses)) { uses = []; byVariant.Add(identity, uses); order.Add(identity); }
                uses.Add(active);
            }
        }
        Activations = activations.ToImmutable();
        Features = order.Select(variant => new AuditApplicationCountFeature(variant, byVariant[variant].Count,
            Count(arena, byVariant[variant], byVariant[variant].Count, token))).ToImmutableArray();
        CountSchema = new(Features.Select(feature => feature.Bound + 1).ToImmutableArray());
    }
    internal static ImmutableArray<AuditRelation> Count(AuditMddArena arena, IEnumerable<AuditRelation> predicates, int bound, CancellationToken token)
    {
        if (bound < 0) throw new ArgumentOutOfRangeException(nameof(bound));
        var current = Enumerable.Repeat(arena.False, bound + 1).ToArray(); current[0] = arena.True;
        foreach (var predicate in predicates)
        {
            token.ThrowIfCancellationRequested(); var next = Enumerable.Repeat(arena.False, bound + 1).ToArray();
            var absent = arena.Not(predicate);
            for (var count = 0; count <= bound; count++)
            {
                next[count] = arena.Or(next[count], arena.And(current[count], absent));
                if (count < bound) next[count + 1] = arena.Or(next[count + 1], arena.And(current[count], predicate));
            }
            current = next;
        }
        return current.ToImmutableArray();
    }
    internal AuditRelation Preimage(AuditRelation counts, CancellationToken token)
    {
        if (!ReferenceEquals(counts.Schema, CountSchema) || !ReferenceEquals(counts.Arena.Owner, Compilation.Program.Owner))
            throw new InvalidOperationException("Application-count preimage owner/schema mismatch");
        return Preimage(counts, Compilation.Arena, Features.Select(feature => feature.EqualsCount).ToImmutableArray(), token);
    }
    internal static AuditRelation Preimage(AuditRelation counts, AuditMddArena source, ImmutableArray<ImmutableArray<AuditRelation>> features, CancellationToken token)
    {
        if (!ReferenceEquals(counts.Arena.Owner, source.Owner) || counts.Schema.Count != features.Length ||
            features.Where((feature, field) => feature.Length != counts.Schema.Domains[field] || feature.Any(value => !ReferenceEquals(value.Arena, source))).Any())
            throw new InvalidOperationException("Count preimage feature owner/schema mismatch");
        var family = counts.Arena;
        var memo = new Dictionary<(int Field, int Root), AuditRelation>();
        AuditEvaluationFrame<AuditRelation> Visit(int field, AuditRelation relation) => new(frame => Steps(field, relation, frame));
        IEnumerable<AuditEvaluationFrame> Steps(int field, AuditRelation relation, AuditEvaluationFrame<AuditRelation> frame)
        {
            if (relation.IsEmpty) { frame.Result = source.False; yield break; }
            if (field == features.Length) { frame.Result = source.True; yield break; }
            var key = (field, relation.Node);
            if (memo.TryGetValue(key, out var cached)) { frame.Result = cached; yield break; }
            token.ThrowIfCancellationRequested(); var result = source.False;
            for (var value = 0; value < features[field].Length; value++)
            {
                var work = Visit(field + 1, family.Cofactor(relation, field, value)); yield return work;
                result = source.Or(result, source.And(features[field][value], work.Result));
            }
            memo.Add(key, result); frame.Result = result;
        }
        return Visit(0, counts).Run();
    }
}

internal sealed record AuditSelectedWitnessScope(AuditRequirementProgram Program, AuditMddArena Input, AuditRelation Members)
{
    // Packet cardinality is enforced in the SAME interleaved source/selector
    // schema consumed by Family. Unselected owners cannot lend partial facts.
    internal static AuditSelectedWitnessScope Create(AuditApplicationCounts counts, AuditRelation members,
        IReadOnlyDictionary<AuditApplicationVariant, int> requested, CancellationToken token)
    {
        var compilation = counts.Compilation; var original = compilation.Program; var source = compilation.Arena;
        if (!ReferenceEquals(members.Arena, source)) throw new InvalidOperationException("Selected witness membership owner mismatch");
        var features = counts.Features.ToDictionary(feature => feature.Variant);
        foreach (var (variant, amount) in requested)
            if (!features.TryGetValue(variant, out var feature) || amount <= 0 || amount > feature.Bound)
                throw new InvalidOperationException("Selected witness packet multiplicity outside source scope");
        var candidates = counts.Activations.Where(active => requested.ContainsKey(active.Variant)).Select(active => active.Occurrence).ToHashSet();
        var selected = original.WithOwnerSelectors(candidates, token);
        var arena = new AuditMddArena(selected.Program.Owner, selected.Program.Schema, token);
        var guard = arena.ImportOrdered(source.And(compilation.Complete.Admitted, members), selected.SourceVariables);
        var activeByOwner = candidates.ToDictionary(occurrence => occurrence, _ => arena.False);
        var selectedByVariant = requested.Keys.ToDictionary(variant => variant, _ => new List<AuditRelation>());
        foreach (var activation in counts.Activations)
        {
            token.ThrowIfCancellationRequested();
            if (!requested.ContainsKey(activation.Variant)) continue;
            var active = arena.ImportOrdered(activation.Active, selected.SourceVariables);
            activeByOwner[activation.Occurrence] = arena.Or(activeByOwner[activation.Occurrence], active);
            var selector = arena.EqualsValue(selected.Selectors[activation.Occurrence], 1);
            selectedByVariant[activation.Variant].Add(arena.And(selector, active));
        }
        foreach (var (occurrence, active) in activeByOwner)
            guard = arena.And(guard, arena.Or(arena.EqualsValue(selected.Selectors[occurrence], 0), active));
        foreach (var (variant, predicates) in selectedByVariant)
        {
            token.ThrowIfCancellationRequested(); var amount = requested[variant];
            guard = arena.And(guard, AuditApplicationCounts.Count(arena, predicates, amount, token)[amount]);
        }
        token.ThrowIfCancellationRequested(); return new(selected.Program, arena, guard);
    }
}
