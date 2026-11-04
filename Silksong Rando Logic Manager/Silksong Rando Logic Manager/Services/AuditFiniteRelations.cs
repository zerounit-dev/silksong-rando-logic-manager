using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed class AuditExplanationOwner(AuditRoomOwner room)
{
    internal AuditRoomOwner Room { get; } = room;
}

// The complete ordered finite schema, not just the variables present in a root.
// Typed field/contribution metadata lives in the owning query/program. This kernel
// does not encode application identity, layout positions or source-history copies.
internal sealed class AuditFiniteSchema
{
    internal ImmutableArray<int> Domains { get; }
    internal int Count => Domains.Length;
    internal AuditFiniteSchema(ImmutableArray<int> domains)
    {
        if (domains.Any(size => size <= 0)) throw new ArgumentException("Finite domains must be nonempty", nameof(domains));
        Domains = domains;
    }
}
internal enum AuditRelationOperation { And, Or, Xor }
internal readonly record struct AuditRelation(AuditMddArena Arena, int Node)
{
    internal AuditFiniteSchema Schema => Arena.Schema;
    internal bool IsEmpty => Node == 0;
}
internal sealed record AuditMddNode(int Variable, ImmutableArray<int> Children);

/// <summary>M1's single reduced ordered finite-domain DAG backend.</summary>
internal sealed class AuditMddArena(AuditExplanationOwner owner, AuditFiniteSchema schema, CancellationToken token)
{
    private sealed class NodeKey(int variable, ImmutableArray<int> children) : IEquatable<NodeKey>
    {
        private int Variable { get; } = variable;
        private ImmutableArray<int> Children { get; } = children;
        private readonly int hash = Hash(variable, children);
        private static int Hash(int variable, ImmutableArray<int> children)
        {
            var code = new HashCode(); code.Add(variable);
            foreach (var child in children) code.Add(child);
            return code.ToHashCode();
        }
        public bool Equals(NodeKey? other) => other is not null && hash == other.hash && Variable == other.Variable && Children.SequenceEqual(other.Children);
        public override bool Equals(object? other) => other is NodeKey key && Equals(key);
        public override int GetHashCode() => hash;
    }
    internal AuditExplanationOwner Owner { get; } = owner;
    internal AuditFiniteSchema Schema { get; } = schema;
    private readonly List<AuditMddNode> nodes = [new(int.MaxValue, []), new(int.MaxValue, [])];
    private readonly Dictionary<NodeKey, int> intern = [];
    private readonly Dictionary<(AuditRelationOperation Op, int Left, int Right), int> apply = [];
    private readonly Dictionary<int, int> complement = [];
    private readonly Dictionary<(int Root, int Variable, int Value), int> cofactors = [];
    internal AuditRelation False => new(this, 0);
    internal AuditRelation True => new(this, 1);
    internal int NodeCount => nodes.Count;
    internal int Top(AuditRelation relation) { Check(relation); return nodes[relation.Node].Variable; }

    private void Check(AuditRelation relation)
    {
        if (!ReferenceEquals(relation.Arena, this) || (uint)relation.Node >= (uint)nodes.Count)
            throw new InvalidOperationException("Audit relation owner/schema mismatch");
    }
    private void Variable(int variable)
    {
        if ((uint)variable >= (uint)Schema.Count) throw new ArgumentOutOfRangeException(nameof(variable));
    }
    private void Value(int variable, int value)
    {
        Variable(variable);
        if ((uint)value >= (uint)Schema.Domains[variable]) throw new ArgumentOutOfRangeException(nameof(value));
    }
    internal AuditRelation Mk(int variable, ImmutableArray<AuditRelation> children)
    {
        Variable(variable);
        if (children.Length != Schema.Domains[variable]) throw new ArgumentException("Wrong finite-domain child count", nameof(children));
        foreach (var child in children) Check(child);
        return new(this, Make(variable, children.Select(child => child.Node).ToImmutableArray()));
    }
    private int Make(int variable, ImmutableArray<int> children)
    {
        token.ThrowIfCancellationRequested();
        if (children.Length != Schema.Domains[variable]) throw new InvalidOperationException("Wrong internal finite-domain child count");
        foreach (var child in children)
            if (nodes[child].Variable <= variable) throw new InvalidOperationException("Audit relation variable order violation");
        if (children.All(child => child == children[0])) return children[0];
        var key = new NodeKey(variable, children);
        if (intern.TryGetValue(key, out var existing)) return existing;
        var result = nodes.Count; nodes.Add(new(variable, children)); intern.Add(key, result); return result;
    }
    internal AuditRelation EqualsValue(int variable, int value)
    {
        Value(variable, value);
        return new(this, Make(variable, Enumerable.Range(0, Schema.Domains[variable]).Select(d => d == value ? 1 : 0).ToImmutableArray()));
    }
    internal AuditRelation Cofactor(AuditRelation relation, int variable, int value)
    {
        Check(relation); Value(variable, value); return new(this, Restrict(relation.Node, variable, value));
    }
    private int Restrict(int root, int variable, int value)
    {
        var node = nodes[root];
        // Skipped coordinate means every declared value, never absent/zero.
        if (node.Variable > variable) return root;
        if (node.Variable == variable) return node.Children[value];
        var key = (root, variable, value);
        if (cofactors.TryGetValue(key, out var cached)) return cached;
        token.ThrowIfCancellationRequested();
        var result = Make(node.Variable, node.Children.Select(child => Restrict(child, variable, value)).ToImmutableArray());
        cofactors.Add(key, result); return result;
    }
    internal AuditRelation Apply(AuditRelationOperation op, AuditRelation left, AuditRelation right)
    { Check(left); Check(right); return new(this, Combine(op, left.Node, right.Node)); }
    internal AuditRelation And(AuditRelation left, AuditRelation right) => Apply(AuditRelationOperation.And, left, right);
    internal AuditRelation Or(AuditRelation left, AuditRelation right) => Apply(AuditRelationOperation.Or, left, right);
    internal AuditRelation Xor(AuditRelation left, AuditRelation right) => Apply(AuditRelationOperation.Xor, left, right);
    internal AuditRelation Not(AuditRelation relation) { Check(relation); return new(this, Negate(relation.Node)); }
    internal AuditRelation Difference(AuditRelation left, AuditRelation right) => And(left, Not(right));
    // Explicit query-local schema extension (e.g. interleaved unary owner
    // selectors). This is not an implicit cross-arena handle conversion.
    internal AuditRelation ImportOrdered(AuditRelation source, ImmutableArray<int> variables)
    {
        source.Arena.Check(source);
        if (!ReferenceEquals(Owner.Room, source.Arena.Owner.Room) || variables.Length != source.Schema.Count)
            throw new InvalidOperationException("Audit relation import room/schema mismatch");
        var previous = -1;
        for (var index = 0; index < variables.Length; index++)
        {
            var variable = variables[index]; Variable(variable);
            if (variable <= previous || Schema.Domains[variable] != source.Schema.Domains[index])
                throw new InvalidOperationException("Audit relation import must preserve variable order/domains");
            previous = variable;
        }
        var memo = new Dictionary<int, int> { [0] = 0, [1] = 1 };
        int Visit(int root)
        {
            if (memo.TryGetValue(root, out var cached)) return cached;
            token.ThrowIfCancellationRequested(); var node = source.Arena.nodes[root];
            var result = Make(variables[node.Variable], node.Children.Select(Visit).ToImmutableArray());
            memo.Add(root, result); return result;
        }
        return new(this, Visit(source.Node));
    }
    internal AuditRelation ImportFrozen(AuditFrozenRelationArena source, int root)
    {
        if (!ReferenceEquals(Owner, source.Owner) || !Schema.Domains.SequenceEqual(source.Schema.Domains))
            throw new InvalidOperationException("Prepared witness union owner/schema mismatch");
        var memo = new Dictionary<int, int> { [0] = 0, [1] = 1 };
        int Visit(int node)
        {
            if (memo.TryGetValue(node, out var cached)) return cached;
            token.ThrowIfCancellationRequested(); var value = source.Nodes[node];
            var result = Make(value.Variable, value.Children.Select(Visit).ToImmutableArray()); memo.Add(node, result); return result;
        }
        return new(this, Visit(root));
    }
    private int Negate(int root)
    {
        if (root < 2) return 1 - root;
        if (complement.TryGetValue(root, out var cached)) return cached;
        token.ThrowIfCancellationRequested();
        var node = nodes[root]; var result = Make(node.Variable, node.Children.Select(Negate).ToImmutableArray());
        complement.Add(root, result); return result;
    }
    private int Combine(AuditRelationOperation op, int left, int right)
    {
        if (left > right) (left, right) = (right, left);
        switch (op)
        {
            case AuditRelationOperation.And:
                if (left == 0) return 0; if (left == 1) return right; if (left == right) return left; break;
            case AuditRelationOperation.Or:
                if (left == 0) return right; if (left == 1) return 1; if (left == right) return left; break;
            case AuditRelationOperation.Xor:
                if (left == 0) return right; if (left == 1) return Negate(right); if (left == right) return 0; break;
            default: throw new InvalidOperationException("Unknown audit relation operation");
        }
        var key = (op, left, right);
        if (apply.TryGetValue(key, out var cached)) return cached;
        token.ThrowIfCancellationRequested();
        var variable = Math.Min(nodes[left].Variable, nodes[right].Variable);
        var children = ImmutableArray.CreateBuilder<int>(Schema.Domains[variable]);
        for (var value = 0; value < Schema.Domains[variable]; value++)
        {
            token.ThrowIfCancellationRequested();
            var a = nodes[left].Variable == variable ? nodes[left].Children[value] : left;
            var b = nodes[right].Variable == variable ? nodes[right].Children[value] : right;
            children.Add(Combine(op, a, b));
        }
        var result = Make(variable, children.ToImmutable()); apply.Add(key, result); return result;
    }
    internal AuditRelation Exists(AuditRelation relation, ImmutableArray<int> removed)
    {
        Check(relation); var eliminate = new bool[Schema.Count];
        foreach (var variable in removed) { Variable(variable); eliminate[variable] = true; }
        var memo = new Dictionary<int, int>();
        int Visit(int root)
        {
            if (root < 2) return root;
            if (memo.TryGetValue(root, out var cached)) return cached;
            token.ThrowIfCancellationRequested();
            var node = nodes[root]; var children = node.Children.Select(Visit).ToImmutableArray();
            var result = eliminate[node.Variable] ? children.Aggregate(0, (sum, child) => Combine(AuditRelationOperation.Or, sum, child)) : Make(node.Variable, children);
            memo.Add(root, result); return result;
        }
        return new(this, Visit(relation.Node));
    }
    internal ImmutableArray<int> Support(AuditRelation relation)
    {
        Check(relation); var visited = new HashSet<int>(); var support = new bool[Schema.Count];
        void Visit(int root)
        {
            if (root < 2 || !visited.Add(root)) return;
            token.ThrowIfCancellationRequested(); var node = nodes[root]; support[node.Variable] = true;
            foreach (var child in node.Children) Visit(child);
        }
        Visit(relation.Node); return Enumerable.Range(0, Schema.Count).Where(variable => support[variable]).ToImmutableArray();
    }
    internal AuditFrozenRelations Freeze(ImmutableArray<AuditRelation> roots)
    {
        foreach (var root in roots) Check(root);
        var frozen = ImmutableArray.CreateBuilder<AuditMddNode>();
        frozen.Add(new(int.MaxValue, [])); frozen.Add(new(int.MaxValue, []));
        var mapping = new Dictionary<int, int> { [0] = 0, [1] = 1 };
        int Visit(int root)
        {
            if (mapping.TryGetValue(root, out var cached)) return cached;
            token.ThrowIfCancellationRequested();
            var source = nodes[root]; var children = source.Children.Select(Visit).ToImmutableArray();
            var result = frozen.Count; frozen.Add(new(source.Variable, children)); mapping.Add(root, result); return result;
        }
        var finalRoots = roots.Select(root => Visit(root.Node)).ToImmutableArray();
        token.ThrowIfCancellationRequested();
        return new(new(Owner, Schema, frozen.ToImmutable()), finalRoots);
    }
}

internal sealed record AuditFrozenRelations(AuditFrozenRelationArena Arena, ImmutableArray<int> Roots);
internal sealed class AuditFrozenRelationArena(AuditExplanationOwner owner, AuditFiniteSchema schema, ImmutableArray<AuditMddNode> nodes)
{
    internal AuditExplanationOwner Owner { get; } = owner;
    internal AuditFiniteSchema Schema { get; } = schema;
    internal ImmutableArray<AuditMddNode> Nodes { get; } = nodes;
    // Only ordered consumers (matching/description) use this read-only cofactor.
    // Full arbitrary-coordinate construction queries stay in the operation builder.
    internal int OrderedCofactor(int root, int variable, int value)
    {
        if ((uint)variable >= (uint)Schema.Count || (uint)value >= (uint)Schema.Domains[variable]) throw new ArgumentOutOfRangeException(nameof(variable));
        var node = Nodes[root];
        if (node.Variable < variable) throw new InvalidOperationException("Out-of-order frozen audit family consumer");
        return node.Variable == variable ? node.Children[value] : root;
    }
}
