using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal enum AuditConditionKind { Fact, Level, Act, SoulMode }
internal readonly record struct AuditConditionKey(AuditConditionKind Kind, string Key);
internal readonly record struct AuditConstraint(AuditConditionKey Key, int Value)
{
    internal bool Matches(AuditFactContext facts) => Key.Kind switch
    {
        AuditConditionKind.Level => facts.Level(Key.Key) >= Value,
        AuditConditionKind.Act => (Value & (1 << (facts.Inputs.Act - 1))) != 0,
        AuditConditionKind.SoulMode => facts.Inputs.SoulRandomizer == (Value == 1),
        AuditConditionKind.Fact => facts.Has(Key.Key) == (Value == 1),
        _ => throw new InvalidOperationException("Unknown audit constraint kind")
    };
}
internal readonly record struct AuditPrerequisiteHandle(int Index);
internal enum AuditPrerequisiteOperator { True, False, Unresolved, Condition, All, Any }
internal sealed record AuditPrerequisiteNode(AuditPrerequisiteOperator Operator, AuditConstraint? Condition,
    string? Reason, ImmutableArray<AuditPrerequisiteHandle> Children, bool HasUnresolved);

// Metadata belongs to each use's evidence/display plan, not constraint equality.
// Children retain the original ordered All/Any boundaries, even at arity zero/one.
internal sealed record AuditPrerequisiteEvidence(AuditPrerequisiteHandle Root, string Label,
    ImmutableArray<AuditPrerequisiteEvidence> Children, string? ControlKey = null,
    bool Manual = false, Guid? LocalCheckId = null, AuditNameRef? Name = null, string? ConditionLabel = null);

internal sealed class AuditPrerequisiteArena(ImmutableArray<AuditPrerequisiteNode> nodes)
{
    internal ImmutableArray<AuditPrerequisiteNode> Nodes { get; } = nodes;
    internal AuditPrerequisiteNode this[AuditPrerequisiteHandle handle] => Nodes[handle.Index];
    internal AuditPrerequisiteEvaluation Evaluate(AuditFactContext facts) => new(this, facts);
}

// This scratch builder is released after binding; the published arena has no
// interning/memo dictionaries and no mutable scenario state.
internal sealed class AuditPrerequisiteBuilder(CancellationToken token)
{
    private sealed class NodeKey(AuditPrerequisiteOperator op, AuditConstraint? condition, string? reason,
        ImmutableArray<AuditPrerequisiteHandle> children) : IEquatable<NodeKey>
    {
        private readonly int hash = Hash(op, condition, reason, children);
        private static int Hash(AuditPrerequisiteOperator op, AuditConstraint? condition, string? reason,
            ImmutableArray<AuditPrerequisiteHandle> children)
        {
            var code = new HashCode(); code.Add(op); code.Add(condition); code.Add(reason, StringComparer.Ordinal);
            foreach (var child in children) code.Add(child);
            return code.ToHashCode();
        }
        private AuditPrerequisiteOperator Operator { get; } = op;
        private AuditConstraint? Condition { get; } = condition;
        private string? Reason { get; } = reason;
        private ImmutableArray<AuditPrerequisiteHandle> Children { get; } = children;
        public bool Equals(NodeKey? other) => other is not null && hash == other.hash && Operator == other.Operator &&
            Condition == other.Condition && Reason == other.Reason && Children.SequenceEqual(other.Children);
        public override bool Equals(object? other) => other is NodeKey key && Equals(key);
        public override int GetHashCode() => hash;
    }
    private readonly List<AuditPrerequisiteNode> nodes = [];
    private readonly Dictionary<NodeKey, AuditPrerequisiteHandle> intern = [];
    internal AuditPrerequisiteNode this[AuditPrerequisiteHandle handle] => nodes[handle.Index];
    internal AuditPrerequisiteHandle Add(AuditPrerequisiteOperator op, AuditConstraint? condition = null,
        string? reason = null, ImmutableArray<AuditPrerequisiteHandle> children = default)
    {
        token.ThrowIfCancellationRequested();
        if (children.IsDefault) children = [];
        var key = new NodeKey(op, condition, reason, children);
        if (intern.TryGetValue(key, out var existing)) return existing;
        var handle = new AuditPrerequisiteHandle(nodes.Count);
        var unresolved = op == AuditPrerequisiteOperator.Unresolved || children.Any(child => nodes[child.Index].HasUnresolved);
        nodes.Add(new(op, condition, reason, children, unresolved)); intern.Add(key, handle);
        return handle;
    }
    internal AuditPrerequisiteArena Freeze()
    {
        token.ThrowIfCancellationRequested();
        return new(nodes.ToImmutableArray());
    }
}

// One invocation/one effective-fact epoch. Only Auto's newly collected facts can
// advance this context, at a complete round boundary. It is never retained in a view.
internal sealed class AuditPrerequisiteEvaluation(AuditPrerequisiteArena arena, AuditFactContext facts)
{
    internal AuditRequirementsContext Requirements => facts.Requirements;
    private readonly bool?[] values = new bool?[arena.Nodes.Length];
    private readonly Dictionary<AuditPrerequisiteEvidence, AuditPrerequisite> evidence = new(ReferenceEqualityComparer.Instance);
    internal void RetireFactsEpoch() { Array.Clear(values); evidence.Clear(); }
    internal bool Pass(AuditPrerequisiteHandle root)
    {
        if (values[root.Index] is { } cached) return cached;
        var node = arena[root];
        var pass = node.Operator switch
        {
            AuditPrerequisiteOperator.True => true,
            AuditPrerequisiteOperator.False or AuditPrerequisiteOperator.Unresolved => false,
            AuditPrerequisiteOperator.Condition => node.Condition!.Value.Matches(facts),
            AuditPrerequisiteOperator.All => node.Children.All(Pass),
            AuditPrerequisiteOperator.Any => node.Children.Any(Pass),
            _ => throw new InvalidOperationException("Unknown audit prerequisite operator")
        };
        values[root.Index] = pass;
        return pass;
    }
    internal AuditPrerequisite Evidence(AuditPrerequisiteEvidence plan)
    {
        if (evidence.TryGetValue(plan, out var cached)) return cached;
        var answer = new AuditPrerequisite(Pass(plan.Root), plan.Label,
            arena[plan.Root].HasUnresolved, arena[plan.Root].Operator == AuditPrerequisiteOperator.Any,
            plan.Children.Select(Evidence).ToImmutableArray(), plan.ControlKey);
        evidence.Add(plan, answer); return answer;
    }
}
