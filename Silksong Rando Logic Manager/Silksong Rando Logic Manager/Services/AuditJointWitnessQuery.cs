using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal enum AuditWitnessTrack { Full, SelectedOwn, OriginalEvidence }
internal sealed record AuditWitnessField(AuditConditionKey Key, AuditWitnessTrack Track, int? RawValue = null, int? OwnValue = null);

// M6.1a: adjacent Full / own-value membership / (raw,own-value-or-absent)
// membership per typed key. No complete evidence set is a domain or payload.
internal sealed class AuditJointWitnessQuery : AuditFamilyQuery
{
    internal override AuditFiniteSchema Schema { get; }
    internal ImmutableArray<AuditWitnessField> Fields { get; }
    private readonly ImmutableDictionary<AuditConditionKey, int> fullFields;
    private readonly ImmutableDictionary<(AuditConditionKey Key, int Raw), int> rawFields;
    private readonly CancellationToken token;
    private readonly ImmutableHashSet<int> selectedLeaves;
    private readonly ImmutableHashSet<int> exportRoots;

    internal AuditJointWitnessQuery(AuditRequirementProgram program, CancellationToken token,
        IReadOnlyDictionary<AuditOccurrenceHandle, ImmutableArray<int>>? ownPaths = null)
    {
        this.token = token;
        var leaves = ImmutableHashSet.CreateBuilder<int>(); var roots = ImmutableHashSet.CreateBuilder<int>();
        foreach (var (occurrence, application) in program.ApplicationNodes)
        {
            token.ThrowIfCancellationRequested();
            var root = ownPaths is null ? application : ownPaths.TryGetValue(occurrence, out var path) ? program.BoundNode(occurrence, path) : -1;
            if (root < 0) continue;
            roots.Add(root); Collect(root);
        }
        void Collect(int index)
        {
            token.ThrowIfCancellationRequested(); var node = program.Nodes[index];
            if (node.Condition is not null) leaves.Add(index);
            foreach (var child in node.Children) Collect(child);
        }
        if (ownPaths is not null && ownPaths.Keys.Any(occurrence => !program.ApplicationNodes.ContainsKey(occurrence)))
            throw new InvalidOperationException("Part prerequisite owner outside witness scope");
        selectedLeaves = leaves.ToImmutable(); exportRoots = roots.ToImmutable();
        var encountered = program.Nodes.Where(node => node.Condition is not null).Select(node => node.Condition!.Value.Key).Distinct().ToImmutableArray();
        var keys = encountered.Where(AuditFullRequirementQuery.IsDashSide).Concat(encountered.Where(key => !AuditFullRequirementQuery.IsDashSide(key)));
        var fields = ImmutableArray.CreateBuilder<AuditWitnessField>(); var domains = ImmutableArray.CreateBuilder<int>();
        var full = ImmutableDictionary.CreateBuilder<AuditConditionKey, int>();
        var raw = ImmutableDictionary.CreateBuilder<(AuditConditionKey, int), int>();
        foreach (var key in keys)
        {
            token.ThrowIfCancellationRequested();
            var count = Domain(key); full.Add(key, fields.Count);
            fields.Add(new(key, AuditWitnessTrack.Full)); domains.Add(count);
            for (var value = 1; value < count; value++)
            { fields.Add(new(key, AuditWitnessTrack.SelectedOwn, OwnValue: value)); domains.Add(2); }
            var rawValues = program.Nodes.Where(node => node.Condition?.Key == key).Select(node => node.Condition!.Value.Value).Distinct().Order().ToImmutableArray();
            foreach (var value in rawValues)
            {
                raw.Add((key, value), fields.Count);
                for (var own = 0; own < count; own++)
                { fields.Add(new(key, AuditWitnessTrack.OriginalEvidence, value, own)); domains.Add(2); }
            }
        }
        Fields = fields.ToImmutable(); Schema = new(domains.ToImmutable()); fullFields = full.ToImmutable(); rawFields = raw.ToImmutable();
    }
    private static int Domain(AuditConditionKey key) => key.Kind switch { AuditConditionKind.Level => 5, AuditConditionKind.Act => 8, _ => 3 };
    internal override AuditRelation Neutral(AuditMddArena output) => ScalarVector(output, null, false);
    internal override AuditRelation Scalar(AuditMddArena output, AuditProgramNode node, bool selected) => ScalarVector(output, node.Condition, selected && selectedLeaves.Contains(node.Index));
    private AuditRelation ScalarVector(AuditMddArena output, AuditConstraint? condition, bool selected)
    {
        var full = condition is { } c ? fullFields[c.Key] : -1;
        var raw = selected && condition is { } original ? rawFields[(original.Key, original.Value)] : -1;
        var result = output.True;
        for (var field = Fields.Length - 1; field >= 0; field--)
        {
            token.ThrowIfCancellationRequested();
            var value = field == full ? AuditFullRequirementQuery.Encode(condition!.Value) : field == raw ? 1 : 0;
            var children = Enumerable.Repeat(output.False, Schema.Domains[field]).ToArray(); children[value] = result;
            result = output.Mk(field, children.ToImmutableArray());
        }
        return result;
    }
    internal override AuditFieldMerge Merge(AuditProgramKind boundary, int field, int state, int left, int right)
    {
        var descriptor = Fields[field];
        if (descriptor.Track != AuditWitnessTrack.Full) return new(true, left | right, state);
        var key = descriptor.Key;
        var merged = AuditConstraintNormalization.Merge(key, AuditFullRequirementQuery.Decode(key, left), AuditFullRequirementQuery.Decode(key, right));
        if (!merged.Compatible) return new(false, 0, state);
        if (AuditFullRequirementQuery.IsDashSide(key) && (left != 0 || right != 0)) state = 1;
        var value = key.Kind == AuditConditionKind.Fact && key.Key == "move:dash:run" && state != 0 ? 0 :
            merged.Value is { } required ? AuditFullRequirementQuery.Encode(new(key, required)) : 0;
        return new(true, value, state);
    }
    internal override AuditRelation Finish(AuditMddArena output, AuditProgramNode node, AuditRelation family, bool selected)
    {
        if (!selected || !exportRoots.Contains(node.Index)) return family;
        // Snapshot own Full and tag each raw bit with its SAME-KEY normalized
        // value. The finite state is one key's Full value, not a whole witness.
        var memo = new Dictionary<(int Field, int Root, int Full), AuditRelation>();
        AuditRelation Bit(int field, int value, AuditRelation suffix)
        {
            var children = new[] { output.False, output.False }; children[value] = suffix;
            return output.Mk(field, children.ToImmutableArray());
        }
        AuditRelation Visit(int field, AuditRelation source, int full)
        {
            if (source.IsEmpty) return output.False;
            if (field == Fields.Length) return output.True;
            var key = (field, source.Node, full);
            if (memo.TryGetValue(key, out var cached)) return cached;
            token.ThrowIfCancellationRequested(); var descriptor = Fields[field]; AuditRelation result;
            if (descriptor.Track == AuditWitnessTrack.Full)
            {
                var children = ImmutableArray.CreateBuilder<AuditRelation>(Schema.Domains[field]);
                for (var value = 0; value < Schema.Domains[field]; value++)
                    children.Add(Visit(field + 1, output.Cofactor(source, field, value), value));
                result = output.Mk(field, children.ToImmutable());
            }
            else if (descriptor.Track == AuditWitnessTrack.SelectedOwn)
            {
                // No child application exists inside this application: its
                // snapshot bits must still be zero at the export boundary.
                result = Bit(field, descriptor.OwnValue == full ? 1 : 0, Visit(field + 1, output.Cofactor(source, field, 0), full));
            }
            else
            {
                if (descriptor.OwnValue != 0) throw new InvalidOperationException("Joint witness export entered partial evidence group");
                var width = Domain(descriptor.Key); result = output.False;
                for (var raw = 0; raw <= 1; raw++)
                {
                    var suffix = output.Cofactor(source, field, raw);
                    for (var own = 1; own < width; own++) suffix = output.Cofactor(suffix, field + own, 0);
                    var mapped = Visit(field + width, suffix, full);
                    for (var own = width - 1; own >= 0; own--) mapped = Bit(field + own, own == full ? raw : 0, mapped);
                    result = output.Or(result, mapped);
                }
            }
            memo.Add(key, result); return result;
        }
        return Visit(0, family, 0);
    }
}
