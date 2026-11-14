using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal readonly record struct AuditConditionMerge(bool Compatible, int? Value);
internal static class AuditConstraintNormalization
{
    // M2/M3's one finite same-key primitive. Run removal is a separate ordered
    // boundary operation; current facts never enter compatibility or normalization.
    internal static AuditConditionMerge Merge(AuditConditionKey key, int? left, int? right)
    {
        if (left is null) return new(true, right);
        if (right is null) return new(true, left);
        var value = key.Kind switch
        {
            AuditConditionKind.Level => Math.Max(left.Value, right.Value),
            AuditConditionKind.Act => left.Value & right.Value,
            _ => left == right ? left.Value : -1
        };
        return new(value >= 0 && (key.Kind != AuditConditionKind.Act || value != 0), value);
    }
}
internal sealed record AuditCompiledRequirement(AuditRelation Admitted, ImmutableArray<AuditConditionKey> Order,
    ImmutableDictionary<AuditConditionKey, ImmutableDictionary<int, AuditRelation>> Cases);

// M2 source functions retain only actual source ORs, never copied output features.
// M3 will fold the original Program through its ordered boundaries, not derive an
// image by repeatedly conditioning these functions on output-vector prefixes.
internal sealed class AuditRequirementCompilation
{
    internal AuditRequirementProgram Program { get; }
    internal AuditMddArena Arena { get; }
    internal ImmutableArray<AuditCompiledRequirement> ByNode { get; }
    internal AuditCompiledRequirement Complete => ByNode[Program.Root];
    internal ImmutableDictionary<AuditOccurrenceHandle, AuditRelation> OccurrenceActive { get; }
    internal ImmutableArray<AuditRelation> Inactive { get; }

    internal AuditRequirementCompilation(AuditRequirementProgram program, CancellationToken token)
    {
        Program = program; Arena = new(program.Owner, program.Schema, token);
        var compiled = ImmutableArray.CreateBuilder<AuditCompiledRequirement>(program.Nodes.Length);
        var inactive = ImmutableArray.CreateBuilder<AuditRelation>(program.Nodes.Length);
        AuditCompiledRequirement Free() => new(Arena.True, [], ImmutableDictionary<AuditConditionKey, ImmutableDictionary<int, AuditRelation>>.Empty);
        foreach (var node in program.Nodes)
        {
            token.ThrowIfCancellationRequested();
            // Nodes are postorder; intervals and choice coordinates remain preorder.
            var zero = node.Choice is { } variable ? Arena.EqualsValue(variable, 0) : Arena.True;
            foreach (var child in node.Children) zero = Arena.And(zero, inactive[child]);
            inactive.Add(zero);
            AuditCompiledRequirement result;
            switch (node.Kind)
            {
                case AuditProgramKind.True: result = Free(); break;
                case AuditProgramKind.False: case AuditProgramKind.Unresolved: result = Free() with { Admitted = Arena.False }; break;
                case AuditProgramKind.Condition:
                    var condition = node.Condition!.Value;
                    result = condition.Key.Kind == AuditConditionKind.Level && condition.Value == 0 ? Free() :
                        new(Arena.True, [condition.Key], ImmutableDictionary<AuditConditionKey, ImmutableDictionary<int, AuditRelation>>.Empty
                            .Add(condition.Key, ImmutableDictionary<int, AuditRelation>.Empty.Add(condition.Value, Arena.True)));
                    break;
                case AuditProgramKind.Application: result = compiled[node.Children[0]]; break;
                case AuditProgramKind.AuthoredAnd: result = Combine(compiled[node.Children[0]], compiled[node.Children[1]], token); break;
                case AuditProgramKind.BoundAll: case AuditProgramKind.Route:
                    result = Free(); foreach (var child in node.Children) result = Combine(result, compiled[child], token); break;
                case AuditProgramKind.Alternative:
                    var admitted = Arena.False; var cases = new Dictionary<AuditConditionKey, Dictionary<int, AuditRelation>>();
                    var order = new List<AuditConditionKey>();
                    for (var index = 0; index < node.Children.Length; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        var branch = compiled[node.Children[index]];
                        var guard = Arena.EqualsValue(node.Choice!.Value, index + 1);
                        for (var sibling = 0; sibling < node.Children.Length; sibling++)
                            if (sibling != index) guard = Arena.And(guard, inactive[node.Children[sibling]]);
                        admitted = Arena.Or(admitted, Arena.And(guard, branch.Admitted));
                        foreach (var key in branch.Order)
                        {
                            if (!cases.TryGetValue(key, out var values)) { values = []; cases.Add(key, values); order.Add(key); }
                            foreach (var (value, predicate) in branch.Cases[key])
                            {
                                var when = Arena.And(guard, predicate);
                                values[value] = Arena.Or(values.GetValueOrDefault(value, Arena.False), when);
                            }
                        }
                    }
                    result = Freeze(admitted, order, cases); break;
                default: throw new InvalidOperationException("Unknown audit input program operator");
            }
            compiled.Add(result);
        }
        ByNode = compiled.ToImmutable(); Inactive = inactive.ToImmutable();
        var active = ImmutableDictionary.CreateBuilder<AuditOccurrenceHandle, AuditRelation>();
        AuditEvaluationFrame<bool> Activate(int index, AuditRelation guard) => new(frame => Steps(index, guard));
        IEnumerable<AuditEvaluationFrame> Steps(int index, AuditRelation guard)
        {
            token.ThrowIfCancellationRequested(); var node = program.Nodes[index];
            if (node.Kind == AuditProgramKind.Application) active.Add(node.Occurrence!.Value, Arena.And(Complete.Admitted, guard));
            for (var child = 0; child < node.Children.Length; child++)
                yield return Activate(node.Children[child], node.Kind == AuditProgramKind.Alternative ?
                    Arena.And(guard, Arena.EqualsValue(node.Choice!.Value, child + 1)) : guard);
        }
        Activate(program.Root, Arena.True).Run(); OccurrenceActive = active.ToImmutable(); token.ThrowIfCancellationRequested();
    }

    private AuditCompiledRequirement Combine(AuditCompiledRequirement left, AuditCompiledRequirement right, CancellationToken token)
    {
        var admitted = Arena.And(left.Admitted, right.Admitted); var rejected = Arena.False;
        var order = left.Order.Concat(right.Order).Distinct().ToArray();
        var cases = new Dictionary<AuditConditionKey, Dictionary<int, AuditRelation>>();
        IEnumerable<(int? Value, AuditRelation When)> Values(AuditCompiledRequirement source, AuditConditionKey key)
        {
            var present = Arena.False;
            if (source.Cases.TryGetValue(key, out var values))
                foreach (var (value, when) in values) { present = Arena.Or(present, when); yield return (value, when); }
            yield return (null, Arena.Difference(source.Admitted, present));
        }
        foreach (var key in order)
        {
            token.ThrowIfCancellationRequested(); var output = new Dictionary<int, AuditRelation>();
            foreach (var a in Values(left, key))
                foreach (var b in Values(right, key))
                {
                    token.ThrowIfCancellationRequested(); var when = Arena.And(admitted, Arena.And(a.When, b.When));
                    if (when.IsEmpty) continue;
                    var merged = AuditConstraintNormalization.Merge(key, a.Value, b.Value);
                    if (!merged.Compatible) rejected = Arena.Or(rejected, when);
                    else if (merged.Value is { } value) output[value] = Arena.Or(output.GetValueOrDefault(value, Arena.False), when);
                }
            cases.Add(key, output);
        }
        admitted = Arena.Difference(admitted, rejected);
        var dash = Arena.False;
        foreach (var side in new[] { "move:dash:left", "move:dash:right" })
            if (cases.TryGetValue(new(AuditConditionKind.Fact, side), out var values))
                foreach (var when in values.Values) dash = Arena.Or(dash, when);
        if (cases.TryGetValue(new(AuditConditionKind.Fact, "move:dash:run"), out var runs))
            foreach (var value in runs.Keys.ToArray()) runs[value] = Arena.Difference(runs[value], dash);
        return Freeze(admitted, order, cases);
    }
    private AuditCompiledRequirement Freeze(AuditRelation admitted, IEnumerable<AuditConditionKey> order,
        Dictionary<AuditConditionKey, Dictionary<int, AuditRelation>> source)
    {
        var cases = ImmutableDictionary.CreateBuilder<AuditConditionKey, ImmutableDictionary<int, AuditRelation>>();
        foreach (var key in order)
        {
            var values = source[key].Select(pair => (pair.Key, When: Arena.And(pair.Value, admitted)))
                .Where(pair => !pair.When.IsEmpty).ToImmutableDictionary(pair => pair.Key, pair => pair.When);
            if (values.Count > 0) cases.Add(key, values);
        }
        var frozen = cases.ToImmutable(); return new(admitted, order.Where(frozen.ContainsKey).ToImmutableArray(), frozen);
    }
}
