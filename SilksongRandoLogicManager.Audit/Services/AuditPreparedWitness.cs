using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed record AuditWitnessValue(AuditConstraint? Match, AuditCondition? Condition,
    AuditEvidenceDisposition? Disposition = null, AuditConstraint? Original = null, int? OwnValue = null);
internal sealed record AuditWitnessCoordinate(ImmutableArray<AuditWitnessValue?> Values);
internal enum AuditDescriptionKind { False, Free, Atom, All, Any }
internal sealed record AuditDescriptionNode(AuditDescriptionKind Kind, ImmutableArray<int> Children, AuditWitnessValue? Value = null);

// Immutable, consumer-owned result. Neither matching nor describing constructs a
// relation or consults source guards. Evidence coordinates never impose a gate.
internal sealed class AuditPreparedWitness
{
    internal AuditRoomOwner RoomOwner => Arena.Owner.Room;
    internal AuditExplanationOwner OperationOwner => Arena.Owner;
    private AuditFrozenRelationArena Arena { get; }
    private int Root { get; }
    private ImmutableArray<AuditWitnessCoordinate> Coordinates { get; }
    private ImmutableArray<AuditDescriptionNode> Description { get; }
    private int DescriptionRoot { get; }
    internal bool HasEffectiveRequirements { get; }
    private AuditPreparedWitness(AuditFrozenRelationArena arena, int root, ImmutableArray<AuditWitnessCoordinate> coordinates,
        ImmutableArray<AuditDescriptionNode> description, int descriptionRoot, bool hasEffective)
    { Arena = arena; Root = root; Coordinates = coordinates; Description = description; DescriptionRoot = descriptionRoot; HasEffectiveRequirements = hasEffective; }

    internal static AuditPreparedWitness Scoped(AuditSelectedWitnessScope selected,
        ImmutableDictionary<AuditConditionKey, AuditConditionMetadata> metadata, CancellationToken token,
        IReadOnlyDictionary<AuditOccurrenceHandle, ImmutableArray<int>>? ownPaths = null)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(selected.Program.Owner, selected.Input.Owner) || !ReferenceEquals(selected.Program.Schema, selected.Input.Schema) ||
            !ReferenceEquals(selected.Members.Arena, selected.Input))
            throw new InvalidOperationException("Selected witness input owner/schema mismatch");
        var query = new AuditJointWitnessQuery(selected.Program, token, ownPaths);
        var builder = new AuditGuardedFamily(selected.Program, selected.Input, query, token);
        var joint = builder.Build(selected.Members);
        var projected = AuditWitnessProjection.Create(joint, query, metadata, token);
        var coordinates = projected.Attributes.Select(attribute => new AuditWitnessCoordinate([null,
            new(attribute.Effective, metadata[(attribute.Effective ?? attribute.Original)!.Value.Key].Display(
                (attribute.Effective ?? attribute.Original)!.Value), attribute.Disposition, attribute.Original, attribute.OwnValue)])).ToImmutableArray();
        return Prepare(projected.Family, coordinates, token);
    }
    internal static AuditPreparedWitness Full(AuditRequirementCompilation compilation, AuditRelation members,
        ImmutableDictionary<AuditConditionKey, AuditConditionMetadata> metadata, CancellationToken token, bool bodyOnly = false, bool headerOnly = false)
    {
        var query = new AuditFullRequirementQuery(compilation.Program);
        var builder = new AuditGuardedFamily(compilation.Program, compilation.Arena, query, token);
        var family = builder.Build(compilation.Arena.And(compilation.Complete.Admitted, members));
        var coordinates = query.Keys.Select((key, field) => new AuditWitnessCoordinate(Enumerable.Range(0, query.Schema.Domains[field])
            .Select(value => AuditFullRequirementQuery.Decode(key, value) is { } required &&
                (!bodyOnly || !AuditFixedHeaders.Includes(key, metadata[key])) && (!headerOnly || AuditFixedHeaders.Includes(key, metadata[key])) ?
                    new AuditWitnessValue(new(key, required), metadata[key].Display(new(key, required))) : null).ToImmutableArray())).ToImmutableArray();
        return Prepare(family, coordinates, token);
    }
    internal static AuditPreparedWitness Complete(AuditRequirementCompilation compilation, AuditRelation members,
        ImmutableDictionary<AuditConditionKey, AuditConditionMetadata> metadata, CancellationToken token)
    {
        var query = new AuditJointWitnessQuery(compilation.Program, token);
        var builder = new AuditGuardedFamily(compilation.Program, compilation.Arena, query, token);
        var joint = builder.Build(compilation.Arena.And(compilation.Complete.Admitted, members), selectAll: true);
        var projected = AuditWitnessProjection.Create(joint, query, metadata, token, complete: true);
        var coordinates = projected.Attributes.Select(attribute => new AuditWitnessCoordinate([null,
            new(attribute.Effective, metadata[(attribute.Effective ?? attribute.Original)!.Value.Key].Display(
                (attribute.Effective ?? attribute.Original)!.Value), attribute.Disposition, attribute.Original, attribute.OwnValue)])).ToImmutableArray();
        return Prepare(projected.Family, coordinates, token);
    }
    internal static AuditPreparedWitness Prepare(AuditRelation family, ImmutableArray<AuditWitnessCoordinate> coordinates, CancellationToken token)
    {
        if (coordinates.Length != family.Schema.Count || coordinates.Where((coordinate, index) => coordinate.Values.Length != family.Schema.Domains[index]).Any())
            throw new InvalidOperationException("Prepared witness coordinate/schema mismatch");
        var frozen = family.Arena.Freeze([family]); var arena = frozen.Arena; var root = frozen.Roots[0];
        var nodes = new List<AuditDescriptionNode> { new(AuditDescriptionKind.False, []), new(AuditDescriptionKind.Free, []) };
        var atoms = new Dictionary<AuditWitnessValue, int>();
        var compositions = new Dictionary<(AuditDescriptionKind, string), int>();
        int Atom(AuditWitnessValue? value)
        {
            if (value is null) return 1;
            if (atoms.TryGetValue(value, out var cached)) return cached;
            var index = nodes.Count; nodes.Add(new(AuditDescriptionKind.Atom, [], value)); atoms.Add(value, index); return index;
        }
        int Compose(AuditDescriptionKind kind, IEnumerable<int> source)
        {
            var children = source.Distinct().ToImmutableArray();
            if (kind == AuditDescriptionKind.All)
            {
                if (children.Contains(0)) return 0;
                children = children.Where(child => child != 1).ToImmutableArray();
            }
            else children = children.Where(child => child != 0).ToImmutableArray();
            if (children.Length == 0) return kind == AuditDescriptionKind.All ? 1 : 0;
            if (children.Length == 1) return children[0];
            // Only identical complete terms are deduplicated. In particular
            // Any(Free,A) and Any(A,All(A,B)) must retain the stronger witness.
            var key = (kind, string.Join(',', children));
            if (compositions.TryGetValue(key, out var cached)) return cached;
            var index = nodes.Count; nodes.Add(new(kind, children)); compositions.Add(key, index); return index;
        }
        var memo = new Dictionary<(int Field, int Root), int>();
        AuditEvaluationFrame<int> Explain(int field, int remaining) => new(frame => ExplainSteps(field, remaining, frame));
        IEnumerable<AuditEvaluationFrame> ExplainSteps(int field, int remaining, AuditEvaluationFrame<int> frame)
        {
            if (remaining == 0) { frame.Result = 0; yield break; }
            if (field == coordinates.Length) { frame.Result = 1; yield break; }
            var key = (field, remaining);
            if (memo.TryGetValue(key, out var cached)) { frame.Result = cached; yield break; }
            token.ThrowIfCancellationRequested();
            var groups = new List<(int Suffix, List<int> Atoms)>(); var bySuffix = new Dictionary<int, int>();
            for (var value = 0; value < arena.Schema.Domains[field]; value++)
            {
                var suffix = arena.OrderedCofactor(remaining, field, value); if (suffix == 0) continue;
                if (!bySuffix.TryGetValue(suffix, out var index)) { index = groups.Count; bySuffix.Add(suffix, index); groups.Add((suffix, [])); }
                groups[index].Atoms.Add(Atom(coordinates[field].Values[value]));
            }
            var terms = new List<int>(groups.Count);
            foreach (var group in groups)
            {
                var alternatives = Compose(AuditDescriptionKind.Any, group.Atoms);
                var suffix = Explain(field + 1, group.Suffix); yield return suffix;
                terms.Add(Compose(AuditDescriptionKind.All, [alternatives, suffix.Result]));
            }
            var result = Compose(AuditDescriptionKind.Any, terms);
            memo.Add(key, result); frame.Result = result;
        }
        var descriptionRoot = Explain(0, root).Run(); token.ThrowIfCancellationRequested();
        var effectiveMemo = new Dictionary<(int Field, int Root), bool>();
        AuditEvaluationFrame<bool> Effective(int field, int remaining) => new(frame => EffectiveSteps(field, remaining, frame));
        IEnumerable<AuditEvaluationFrame> EffectiveSteps(int field, int remaining, AuditEvaluationFrame<bool> frame)
        {
            if (remaining == 0 || field == coordinates.Length) { frame.Result = false; yield break; }
            var key = (field, remaining); if (effectiveMemo.TryGetValue(key, out var cached)) { frame.Result = cached; yield break; }
            token.ThrowIfCancellationRequested(); var result = false;
            for (var value = 0; value < arena.Schema.Domains[field] && !result; value++)
            {
                var suffix = arena.OrderedCofactor(remaining, field, value);
                if (suffix == 0) continue;
                if (coordinates[field].Values[value]?.Match is not null) result = true;
                else { var work = Effective(field + 1, suffix); yield return work; result = work.Result; }
            }
            effectiveMemo.Add(key, result); frame.Result = result;
        }
        var hasEffective = Effective(0, root).Run(); token.ThrowIfCancellationRequested();
        return new(arena, root, coordinates, nodes.ToImmutableArray(), descriptionRoot, hasEffective);
    }
    internal static AuditPreparedWitness Union(IEnumerable<AuditPreparedWitness> source, CancellationToken token)
    {
        var witnesses = source.ToArray();
        if (witnesses.Length == 0) throw new InvalidOperationException("Consumer witness union has no complete options");
        if (witnesses.Length == 1) return witnesses[0];
        var first = witnesses[0]; var output = new AuditMddArena(first.OperationOwner, first.Arena.Schema, token); var relation = output.False;
        foreach (var witness in witnesses)
        {
            token.ThrowIfCancellationRequested();
            if (witness.Coordinates.Length != first.Coordinates.Length || witness.Coordinates.Where((coordinate, field) => !coordinate.Values.SequenceEqual(first.Coordinates[field].Values)).Any())
                throw new InvalidOperationException("Consumer witness union has different semantic fields");
            relation = output.Or(relation, output.ImportFrozen(witness.Arena, witness.Root));
        }
        return Prepare(relation, first.Coordinates, token);
    }
    internal bool Matches(AuditFactContext facts)
    {
        var memo = new Dictionary<(int Field, int Root), bool>();
        AuditEvaluationFrame<bool> Visit(int field, int remaining) => new(frame => Steps(field, remaining, frame));
        IEnumerable<AuditEvaluationFrame> Steps(int field, int remaining, AuditEvaluationFrame<bool> frame)
        {
            if (remaining == 0) { frame.Result = false; yield break; }
            if (field == Coordinates.Length) { frame.Result = true; yield break; }
            var key = (field, remaining);
            if (memo.TryGetValue(key, out var cached)) { frame.Result = cached; yield break; }
            var result = false;
            for (var value = 0; value < Arena.Schema.Domains[field] && !result; value++)
                if (Coordinates[field].Values[value]?.Match is not { } constraint || constraint.Matches(facts))
                {
                    var work = Visit(field + 1, Arena.OrderedCofactor(remaining, field, value)); yield return work; result = work.Result;
                }
            memo.Add(key, result); frame.Result = result;
        }
        return Visit(0, Root).Run();
    }
    internal string Describe(AuditFactContext facts)
    {
        var references = new int[Description.Length]; var visited = new HashSet<int>();
        AuditEvaluationFrame<bool> Count(int index) => new(frame => CountSteps(index));
        IEnumerable<AuditEvaluationFrame> CountSteps(int index)
        {
            references[index]++;
            if (!visited.Add(index)) yield break;
            foreach (var child in Description[index].Children) yield return Count(child);
        }
        Count(DescriptionRoot).Run();
        var named = visited.Where(index => references[index] > 1 && Description[index].Children.Length > 0).Order().ToArray();
        var labels = named.Select((index, ordinal) => (index, Label: "Prerequisite " + (ordinal + 1))).ToDictionary(pair => pair.index, pair => pair.Label);
        AuditEvaluationFrame<string> Text(int index, bool definition = false) => new(frame => TextSteps(index, definition, frame));
        IEnumerable<AuditEvaluationFrame> TextSteps(int index, bool definition, AuditEvaluationFrame<string> frame)
        {
            if (!definition && labels.TryGetValue(index, out var label)) { frame.Result = label; yield break; }
            var node = Description[index];
            switch (node.Kind)
            {
                case AuditDescriptionKind.False: frame.Result = "No sufficient alternatives"; break;
                case AuditDescriptionKind.Free: frame.Result = "No requirements"; break;
                case AuditDescriptionKind.Atom: frame.Result = Value(node.Value!); break;
                default:
                    var children = new List<string>(node.Children.Length);
                    foreach (var child in node.Children) { var work = Text(child); yield return work; children.Add(work.Result); }
                    frame.Result = "(" + string.Join(node.Kind == AuditDescriptionKind.All ? " AND " : " OR ", children) + ")"; break;
            }
        }
        string Value(AuditWitnessValue value)
        {
            var summary = value.Condition!.Summary;
            if (value.Match is { } condition) return summary + (condition.Matches(facts) ? " — met" : " — missing / not matched");
            var own = value.Original is { } original && value.OwnValue is { } encoded &&
                AuditFullRequirementQuery.Decode(original.Key, encoded) is { } required && required != original.Value
                    ? "; own normalized requirement: " + (value.Condition with { Value = required }).Summary : "";
            return summary + " — " + (value.Disposition switch
            {
                AuditEvidenceDisposition.Qualifier => "qualifier",
                AuditEvidenceDisposition.RemovedAtOwn => "normalized away within own requirements",
                AuditEvidenceDisposition.FixedHeader => "represented by section header",
                AuditEvidenceDisposition.Effective => "represented by effective requirement",
                _ => "context, not an additional requirement"
            }) + own;
        }
        var result = Text(DescriptionRoot).Run();
        return named.Length == 0 ? result : result + "; " + string.Join("; ", named.Select(index => labels[index] + " = " + Text(index, true).Run()));
    }
}
