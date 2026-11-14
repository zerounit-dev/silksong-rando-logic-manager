using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed record AuditConditionMetadata(string Label, bool Manual, Guid? LocalCheckId)
{
    internal AuditCondition Display(AuditConstraint condition) => new(condition.Key.Kind switch
    {
        AuditConditionKind.Level => "level", AuditConditionKind.Act => "act", AuditConditionKind.SoulMode => "soul-mode", _ => "fact"
    }, condition.Key.Key, Label, condition.Value, Manual);
    internal static ImmutableDictionary<AuditConditionKey, AuditConditionMetadata> Collect(AuditRequirementProgram program, CancellationToken token)
    {
        var result = ImmutableDictionary.CreateBuilder<AuditConditionKey, AuditConditionMetadata>();
        foreach (var node in program.Nodes.Where(node => node.Kind == AuditProgramKind.Application))
        {
            var occurrence = program.Room.Source.Occurrences[node.Occurrence!.Value.Index];
            Visit(program.Room.Bindings.Applications[occurrence.Application.Index].Prerequisite).Run();
        }
        AuditEvaluationFrame<bool> Visit(AuditPrerequisiteEvidence evidence) => new(frame => Steps(evidence));
        IEnumerable<AuditEvaluationFrame> Steps(AuditPrerequisiteEvidence evidence)
        {
            token.ThrowIfCancellationRequested();
            if (program.Room.Bindings.Prerequisites[evidence.Root].Condition is { } condition && !result.ContainsKey(condition.Key))
                result.Add(condition.Key, new(evidence.ConditionLabel ?? evidence.Label, evidence.Manual, evidence.LocalCheckId));
            foreach (var child in evidence.Children) yield return Visit(child);
        }
        return result.ToImmutable();
    }
}
internal static class AuditFixedHeaders
{
    internal static readonly ImmutableArray<string> Settings = ["steel-soul", "bellshrinesanity", "bossanity", "wishsanity", "entrance-randomizer", "advanced-knowledge"];
    internal static bool Includes(AuditConditionKey key, AuditConditionMetadata metadata) => !metadata.Manual &&
        (key.Kind == AuditConditionKind.SoulMode && key.Key == "soul-mode" || key.Kind == AuditConditionKind.Act && key.Key == "act" ||
         key.Kind == AuditConditionKind.Fact && Settings.Any(setting => key.Key == "setting:" + setting));
    internal static bool CoversSameKey(AuditConstraint own, AuditConstraint full) => own.Key == full.Key && (own.Key.Kind switch
    {
        AuditConditionKind.Level => own.Value <= full.Value,
        AuditConditionKind.Act => (own.Value & full.Value) == full.Value,
        _ => own.Value == full.Value
    });
}
internal enum AuditEvidenceDisposition { Qualifier, RemovedAtOwn, FixedHeader, Effective, Context }
internal sealed record AuditWitnessAttribute(AuditConstraint? Effective, AuditConstraint? Original = null,
    int? OwnValue = null, AuditEvidenceDisposition? Disposition = null);
internal sealed record AuditWitnessProjection(AuditMddArena Arena, AuditRelation Family, ImmutableArray<AuditWitnessAttribute> Attributes)
{
    // M6's root mapping reads Full and Own/evidence jointly, one typed key at
    // a time. No independently projected families are paired after the fact.
    internal static AuditWitnessProjection Create(AuditRelation joint, AuditJointWitnessQuery query,
        ImmutableDictionary<AuditConditionKey, AuditConditionMetadata> metadata, CancellationToken token, bool complete = false)
    {
        if (!ReferenceEquals(joint.Schema, query.Schema)) throw new InvalidOperationException("Joint witness projection schema mismatch");
        var attributes = ImmutableArray.CreateBuilder<AuditWitnessAttribute>(); var starts = new int[query.Fields.Length];
        for (var field = 0; field < query.Fields.Length; field++)
        {
            var descriptor = query.Fields[field]; starts[field] = attributes.Count;
            if (descriptor.Track == AuditWitnessTrack.Full && complete)
                for (var value = 1; value < joint.Schema.Domains[field]; value++)
                    attributes.Add(new(new(descriptor.Key, AuditFullRequirementQuery.Decode(descriptor.Key, value)!.Value)));
            else if (descriptor.Track == AuditWitnessTrack.SelectedOwn)
                attributes.Add(new(new(descriptor.Key, AuditFullRequirementQuery.Decode(descriptor.Key, descriptor.OwnValue!.Value)!.Value)));
            else if (descriptor.Track == AuditWitnessTrack.OriginalEvidence)
                foreach (var disposition in Enum.GetValues<AuditEvidenceDisposition>())
                    attributes.Add(new(null, new(descriptor.Key, descriptor.RawValue!.Value), descriptor.OwnValue, disposition));
        }
        var output = new AuditMddArena(joint.Arena.Owner, new(Enumerable.Repeat(2, attributes.Count).ToImmutableArray()), token);
        var memo = new Dictionary<(int Field, int Root, int Full), AuditRelation>();
        bool Supports(AuditConditionKey key, int own, int full)
        {
            var a = AuditFullRequirementQuery.Decode(key, own); var b = AuditFullRequirementQuery.Decode(key, full);
            return a is { } av && b is { } bv && AuditFixedHeaders.CoversSameKey(new(key, av), new(key, bv));
        }
        AuditRelation Emit(int start, int width, int? present, AuditRelation suffix)
        {
            for (var offset = width - 1; offset >= 0; offset--)
            {
                var children = new[] { output.False, output.False }; children[present == offset ? 1 : 0] = suffix;
                suffix = output.Mk(start + offset, children.ToImmutableArray());
            }
            return suffix;
        }
        AuditEvaluationFrame<AuditRelation> Visit(int field, AuditRelation remaining, int full) => new(frame => Steps(field, remaining, full, frame));
        IEnumerable<AuditEvaluationFrame> Steps(int field, AuditRelation remaining, int full, AuditEvaluationFrame<AuditRelation> frame)
        {
            if (remaining.IsEmpty) { frame.Result = output.False; yield break; }
            if (field == query.Fields.Length) { frame.Result = output.True; yield break; }
            var memoKey = (field, remaining.Node, full);
            if (memo.TryGetValue(memoKey, out var cached)) { frame.Result = cached; yield break; }
            token.ThrowIfCancellationRequested(); var descriptor = query.Fields[field]; var result = output.False;
            var header = AuditFixedHeaders.Includes(descriptor.Key, metadata[descriptor.Key]);
            for (var value = 0; value < joint.Schema.Domains[field]; value++)
            {
                var work = Visit(field + 1, joint.Arena.Cofactor(remaining, field, value), descriptor.Track == AuditWitnessTrack.Full ? value : full);
                yield return work; var suffix = work.Result;
                if (descriptor.Track == AuditWitnessTrack.Full && complete)
                    suffix = Emit(starts[field], joint.Schema.Domains[field] - 1, value == 0 ? null : value - 1, suffix);
                else if (descriptor.Track == AuditWitnessTrack.SelectedOwn)
                    suffix = Emit(starts[field], 1, value == 1 && !header && Supports(descriptor.Key, descriptor.OwnValue!.Value, full) ? 0 : null, suffix);
                else if (descriptor.Track == AuditWitnessTrack.OriginalEvidence)
                {
                    var own = descriptor.OwnValue!.Value;
                    var disposition = descriptor.Key.Kind == AuditConditionKind.Level && descriptor.RawValue == 0 ? AuditEvidenceDisposition.Qualifier :
                        own == 0 ? AuditEvidenceDisposition.RemovedAtOwn :
                        header && Supports(descriptor.Key, own, full) ? AuditEvidenceDisposition.FixedHeader :
                        !header && Supports(descriptor.Key, own, full) ? AuditEvidenceDisposition.Effective : AuditEvidenceDisposition.Context;
                    suffix = Emit(starts[field], 5, value == 1 ? (int)disposition : null, suffix);
                }
                result = output.Or(result, suffix);
            }
            memo.Add(memoKey, result); frame.Result = result;
        }
        var family = Visit(0, joint, 0).Run(); token.ThrowIfCancellationRequested();
        return new(output, family, attributes.ToImmutable());
    }
}
