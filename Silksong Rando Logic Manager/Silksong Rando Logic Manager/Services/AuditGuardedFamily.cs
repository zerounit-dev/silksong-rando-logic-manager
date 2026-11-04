using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal readonly record struct AuditFieldMerge(bool Accepted, int Value, int State);

// A query fixes one joint finite output schema and its boundary transducer. It
// never receives a complete source assignment or complete output vector.
internal abstract class AuditFamilyQuery
{
    internal abstract AuditFiniteSchema Schema { get; }
    internal abstract AuditRelation Neutral(AuditMddArena output);
    internal abstract AuditRelation Scalar(AuditMddArena output, AuditProgramNode node, bool selected);
    internal abstract AuditFieldMerge Merge(AuditProgramKind boundary, int field, int state, int left, int right);
    internal virtual bool Accept(AuditProgramKind boundary, int state) => true;
    internal virtual AuditRelation Finish(AuditMddArena output, AuditProgramNode node, AuditRelation family, bool selected) => family;
}

// One operation-local mapped product implementation for all query schemas. The
// transducer state is finite query metadata, never a complete-vector payload.
internal sealed class AuditFamilyProduct(AuditMddArena output, AuditFamilyQuery query, CancellationToken token)
{
    private readonly Dictionary<(AuditProgramKind Boundary, int Field, int Left, int Right, int State), AuditRelation> memo = [];
    internal AuditRelation Apply(AuditProgramKind boundary, AuditRelation left, AuditRelation right) => Visit(boundary, 0, left, right, 0);
    private AuditRelation Visit(AuditProgramKind boundary, int field, AuditRelation left, AuditRelation right, int state)
    {
        if (!ReferenceEquals(left.Arena, output) || !ReferenceEquals(right.Arena, output))
            throw new InvalidOperationException("Audit family product owner/schema mismatch");
        if (left.IsEmpty || right.IsEmpty) return output.False;
        if (field == query.Schema.Count) return query.Accept(boundary, state) ? output.True : output.False;
        var key = (boundary, field, left.Node, right.Node, state);
        if (memo.TryGetValue(key, out var cached)) return cached;
        token.ThrowIfCancellationRequested();
        var children = Enumerable.Repeat(output.False, query.Schema.Domains[field]).ToArray();
        for (var x = 0; x < query.Schema.Domains[field]; x++)
        {
            var a = output.Cofactor(left, field, x); if (a.IsEmpty) continue;
            for (var y = 0; y < query.Schema.Domains[field]; y++)
            {
                token.ThrowIfCancellationRequested();
                var b = output.Cofactor(right, field, y); if (b.IsEmpty) continue;
                var merge = query.Merge(boundary, field, state, x, y); if (!merge.Accepted) continue;
                if ((uint)merge.Value >= (uint)children.Length) throw new InvalidOperationException("Audit family transducer value outside schema");
                var suffix = Visit(boundary, field + 1, a, b, merge.State);
                children[merge.Value] = output.Or(children[merge.Value], suffix);
            }
        }
        var result = output.Mk(field, children.ToImmutableArray()); memo.Add(key, result); return result;
    }
}

// M3: sparse entry/exit transfers over the membership guard's actual residuals.
// An independent hidden OR is folded locally; no recursion over output prefixes.
internal sealed class AuditGuardedFamily
{
    private readonly AuditRequirementProgram program;
    private readonly AuditMddArena input;
    private readonly AuditFamilyQuery query;
    private readonly CancellationToken token;
    internal AuditMddArena Output { get; }
    private readonly AuditFamilyProduct product;
    private readonly Dictionary<(int Node, int Entry, bool Selected), ImmutableDictionary<int, AuditRelation>> transfers = [];
    private readonly Dictionary<(int Entry, int First, int End), int> inactive = [];

    internal AuditGuardedFamily(AuditRequirementProgram program, AuditMddArena input, AuditFamilyQuery query, CancellationToken token)
    {
        if (!ReferenceEquals(program.Owner, input.Owner) || !ReferenceEquals(program.Schema, input.Schema))
            throw new InvalidOperationException("Audit family input owner/schema mismatch");
        this.program = program; this.input = input; this.query = query; this.token = token;
        Validate(); Output = new(program.Owner, query.Schema, token); product = new(Output, query, token);
    }
    internal AuditRelation Build(AuditRelation membership, bool selectAll = false)
    {
        if (!ReferenceEquals(membership.Arena, input)) throw new InvalidOperationException("Audit family guard owner mismatch");
        token.ThrowIfCancellationRequested();
        var exits = Transfer(program.Root, membership.Node, selectAll);
        if (exits.Keys.Any(exit => exit > 1)) throw new InvalidOperationException("Audit family left an unconsumed guard coordinate");
        var result = exits.GetValueOrDefault(1, Output.False);
        token.ThrowIfCancellationRequested(); return result;
    }
    private void Validate()
    {
        var root = program.Nodes[program.Root];
        if (root.FirstChoice != 0 || root.EndChoice != program.Schema.Count) throw new InvalidOperationException("Audit family program does not consume schema");
        foreach (var node in program.Nodes)
        {
            token.ThrowIfCancellationRequested();
            var position = node.FirstChoice;
            if (node.Choice is { } choice)
            {
                var validTag = node.Kind == AuditProgramKind.Alternative && program.Schema.Domains[choice] == node.Children.Length + 1 ||
                    node.Kind == AuditProgramKind.OwnerSelector && node.Children.Length == 1 && program.Schema.Domains[choice] == 2 ||
                    node.Kind == AuditProgramKind.LaneAlternative && program.Schema.Domains[choice] == node.Children.Length;
                if (choice != position || !validTag)
                    throw new InvalidOperationException("Audit family choice interval mismatch");
                position++;
            }
            foreach (var child in node.Children)
            {
                var interval = program.Nodes[child];
                if (interval.FirstChoice != position) throw new InvalidOperationException("Audit family child interval mismatch");
                if (node.Kind == AuditProgramKind.LaneAlternative && interval.EndChoice != position)
                    throw new InvalidOperationException("Lane packet cannot consume copied source coordinates");
                position = interval.EndChoice;
            }
            if (position != node.EndChoice) throw new InvalidOperationException("Audit family interval has unrelated coordinates");
        }
    }
    private int Advance(int entry, int variable, int value)
    {
        var relation = new AuditRelation(input, entry);
        if (input.Top(relation) < variable) throw new InvalidOperationException("Audit family attempted out-of-order guard consumption");
        return input.Cofactor(relation, variable, value).Node;
    }
    private int Inactive(int entry, int first, int end)
    {
        var key = (entry, first, end);
        if (inactive.TryGetValue(key, out var cached)) return cached;
        token.ThrowIfCancellationRequested(); var residual = entry;
        for (var variable = first; variable < end && residual != 0; variable++) residual = Advance(residual, variable, 0);
        inactive.Add(key, residual); return residual;
    }
    private ImmutableDictionary<int, AuditRelation> Transfer(int index, int entry, bool selected)
    {
        if (entry == 0) return ImmutableDictionary<int, AuditRelation>.Empty;
        var key = (index, entry, selected);
        if (transfers.TryGetValue(key, out var cached)) return cached;
        token.ThrowIfCancellationRequested(); var node = program.Nodes[index];
        var result = new Dictionary<int, AuditRelation>();
        void Add(int exit, AuditRelation family)
        {
            if (exit == 0 || family.IsEmpty) return;
            result[exit] = Output.Or(result.GetValueOrDefault(exit, Output.False), family);
        }
        switch (node.Kind)
        {
            case AuditProgramKind.False: case AuditProgramKind.Unresolved: break;
            case AuditProgramKind.True: case AuditProgramKind.Condition: case AuditProgramKind.Packet:
                Add(entry, query.Scalar(Output, node, selected)); break;
            case AuditProgramKind.Application:
                foreach (var (exit, family) in Transfer(node.Children[0], entry, selected))
                    Add(exit, family);
                break;
            case AuditProgramKind.OwnerSelector:
                for (var value = 0; value <= 1; value++)
                    foreach (var (exit, family) in Transfer(node.Children[0], Advance(entry, node.Choice!.Value, value), value == 1)) Add(exit, family);
                break;
            case AuditProgramKind.LaneAlternative:
                for (var value = 0; value < node.Children.Length; value++)
                    foreach (var (exit, family) in Transfer(node.Children[value], Advance(entry, node.Choice!.Value, value), selected)) Add(exit, family);
                break;
            case AuditProgramKind.Alternative:
                for (var chosen = 0; chosen < node.Children.Length; chosen++)
                {
                    token.ThrowIfCancellationRequested();
                    var residual = Advance(entry, node.Choice!.Value, chosen + 1);
                    for (var sibling = 0; sibling < chosen && residual != 0; sibling++)
                    {
                        var interval = program.Nodes[node.Children[sibling]];
                        residual = Inactive(residual, interval.FirstChoice, interval.EndChoice);
                    }
                    foreach (var (middle, family) in Transfer(node.Children[chosen], residual, selected))
                    {
                        var exit = middle;
                        for (var sibling = chosen + 1; sibling < node.Children.Length && exit != 0; sibling++)
                        {
                            var interval = program.Nodes[node.Children[sibling]];
                            exit = Inactive(exit, interval.FirstChoice, interval.EndChoice);
                        }
                        Add(exit, family);
                    }
                }
                break;
            case AuditProgramKind.AuthoredAnd: case AuditProgramKind.BoundAll: case AuditProgramKind.Route:
                // Authored binary AND starts from its LEFT operand, not an extra
                // empty Combine. Bound-All/route deliberately start from empty.
                var first = 0;
                Dictionary<int, AuditRelation> pending;
                if (node.Kind == AuditProgramKind.AuthoredAnd)
                { pending = Transfer(node.Children[0], entry, selected).ToDictionary(); first = 1; }
                else pending = new() { [entry] = query.Neutral(Output) };
                for (var child = first; child < node.Children.Length; child++)
                {
                    token.ThrowIfCancellationRequested(); var next = new Dictionary<int, AuditRelation>();
                    foreach (var (middle, left) in pending)
                        foreach (var (exit, right) in Transfer(node.Children[child], middle, selected))
                        {
                            var family = product.Apply(node.Kind, left, right); if (family.IsEmpty) continue;
                            next[exit] = Output.Or(next.GetValueOrDefault(exit, Output.False), family);
                        }
                    pending = next;
                }
                foreach (var (exit, family) in pending) Add(exit, family);
                break;
            default: throw new InvalidOperationException("Unknown audit family program operator");
        }
        var frozen = result.ToImmutableDictionary(pair => pair.Key,
            pair => query.Finish(Output, node, pair.Value, selected));
        transfers.Add(key, frozen); return frozen;
    }
}

// Full normalized requirements query. Key order is static; the two dash-presence
// coordinates precede Run so the finite product state can apply M2's exact rule.
internal sealed class AuditFullRequirementQuery : AuditFamilyQuery
{
    internal ImmutableArray<AuditConditionKey> Keys { get; }
    private readonly ImmutableDictionary<AuditConditionKey, int> indices;
    internal override AuditFiniteSchema Schema { get; }
    internal AuditFullRequirementQuery(AuditRequirementProgram program, IReadOnlySet<AuditConditionKey>? included = null)
    {
        var encountered = program.Nodes.Where(node => node.Condition is not null).Select(node => node.Condition!.Value.Key)
            .Where(key => included is null || included.Contains(key)).Distinct().ToImmutableArray();
        Keys = encountered.Where(IsDashSide).Concat(encountered.Where(key => !IsDashSide(key))).ToImmutableArray();
        indices = Keys.Select((key, index) => (key, index)).ToImmutableDictionary(pair => pair.key, pair => pair.index);
        Schema = new(Keys.Select(key => key.Kind switch
        { AuditConditionKind.Level => 5, AuditConditionKind.Act => 8, _ => 3 }).ToImmutableArray());
    }
    internal static bool IsDashSide(AuditConditionKey key) => key.Kind == AuditConditionKind.Fact && key.Key is "move:dash:left" or "move:dash:right";
    internal static int Encode(AuditConstraint condition) => condition.Key.Kind is AuditConditionKind.Fact or AuditConditionKind.SoulMode ? condition.Value + 1 : condition.Value;
    internal static int? Decode(AuditConditionKey key, int value) => value == 0 ? null : key.Kind is AuditConditionKind.Fact or AuditConditionKind.SoulMode ? value - 1 : value;
    internal override AuditRelation Neutral(AuditMddArena output) => Vector(output, null);
    private AuditRelation Vector(AuditMddArena output, AuditConstraint? condition)
    {
        var result = output.True;
        for (var field = Schema.Count - 1; field >= 0; field--)
        {
            var value = condition is { } c && indices.TryGetValue(c.Key, out var index) && index == field ? Encode(c) : 0;
            var children = Enumerable.Repeat(output.False, Schema.Domains[field]).ToArray(); children[value] = result;
            result = output.Mk(field, children.ToImmutableArray());
        }
        return result;
    }
    internal override AuditRelation Scalar(AuditMddArena output, AuditProgramNode node, bool selected) => Vector(output, node.Condition);
    internal override AuditFieldMerge Merge(AuditProgramKind boundary, int field, int state, int left, int right)
    {
        var key = Keys[field];
        var merged = AuditConstraintNormalization.Merge(key, Decode(key, left), Decode(key, right));
        if (!merged.Compatible) return new(false, 0, state);
        if (IsDashSide(key) && (left != 0 || right != 0)) state = 1;
        var value = key.Kind == AuditConditionKind.Fact && key.Key == "move:dash:run" && state != 0 ? 0 :
            merged.Value is { } required ? Encode(new(key, required)) : 0;
        return new(true, value, state);
    }
}
