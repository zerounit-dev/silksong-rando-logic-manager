using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed record AuditCoverEdge(ImmutableArray<int> Values, AuditCoverNode Suffix);
internal sealed record AuditCoverNode(int Field, bool Leaf, ImmutableArray<AuditCoverEdge> Edges);
internal sealed record AuditLaneRectangle(ImmutableArray<ImmutableArray<int>> Values);

// M5: static packet tables supply the approved finite join class for EACH value.
// This cover only groups identical cofactors within that class. It never infers
// joinability from names, colors, condition equality or a badge-family caption.
internal sealed class AuditCofactorCover
{
    private readonly CancellationToken token;
    internal AuditCoverNode Root { get; }
    internal AuditCofactorCover(AuditRelation relation, ImmutableArray<ImmutableArray<int>> joinClasses, CancellationToken token)
    {
        this.token = token; var arena = relation.Arena;
        if (joinClasses.Length != relation.Schema.Count) throw new InvalidOperationException("Audit cover lane schema mismatch");
        for (var field = 0; field < joinClasses.Length; field++)
        {
            if (joinClasses[field].Length != relation.Schema.Domains[field]) throw new InvalidOperationException("Audit cover packet domain mismatch");
            // Every lane reserves value zero for Absent. It cannot be offered
            // alongside a mandatory option in one displayed rectangle.
            if (joinClasses[field].Skip(1).Any(group => group == joinClasses[field][0])) throw new InvalidOperationException("Absent lane value cannot join a present packet");
        }
        var memo = new Dictionary<(int Field, int Root), AuditCoverNode>();
        AuditEvaluationFrame<AuditCoverNode> Build(int field, AuditRelation remaining) => new(frame => Steps(field, remaining, frame));
        IEnumerable<AuditEvaluationFrame> Steps(int field, AuditRelation remaining, AuditEvaluationFrame<AuditCoverNode> frame)
        {
            var key = (field, remaining.Node);
            if (memo.TryGetValue(key, out var cached)) { frame.Result = cached; yield break; }
            token.ThrowIfCancellationRequested(); AuditCoverNode result;
            if (remaining.IsEmpty) result = new(field, false, []);
            else if (field == relation.Schema.Count) result = new(field, true, []);
            else
            {
                var groups = new List<(AuditRelation Suffix, List<int> Values)>();
                var groupIndex = new Dictionary<(int Suffix, int JoinClass), int>();
                for (var value = 0; value < relation.Schema.Domains[field]; value++)
                {
                    token.ThrowIfCancellationRequested(); var suffix = arena.Cofactor(remaining, field, value);
                    if (suffix.IsEmpty) continue;
                    var groupKey = (suffix.Node, joinClasses[field][value]);
                    if (!groupIndex.TryGetValue(groupKey, out var index))
                    { index = groups.Count; groupIndex.Add(groupKey, index); groups.Add((suffix, [])); }
                    groups[index].Values.Add(value);
                }
                var edges = ImmutableArray.CreateBuilder<AuditCoverEdge>(groups.Count);
                foreach (var group in groups)
                {
                    var values = group.Values.ToImmutableArray();
                    var work = Build(field + 1, group.Suffix); yield return work;
                    edges.Add(new(values, work.Result));
                }
                result = new(field, false, edges.ToImmutable());
            }
            memo.Add(key, result); frame.Result = result;
        }
        Root = Build(0, relation).Run(); token.ThrowIfCancellationRequested();
    }
    // The only enumeration is final flat publication paths through the cover
    // DAG. No complete source assignment/condition vector is materialized.
    internal IEnumerable<AuditLaneRectangle> Rectangles()
    {
        var values = new List<ImmutableArray<int>>();
        var stack = new Stack<(AuditCoverNode Node, int Edge)>();
        stack.Push((Root, -1));
        try
        {
            while (stack.TryPop(out var cursor))
            {
                var node = cursor.Node;
                if (cursor.Edge == -1)
                {
                    token.ThrowIfCancellationRequested();
                    if (node.Leaf) { yield return new(values.ToImmutableArray()); continue; }
                }
                else values.RemoveAt(values.Count - 1);
                var next = cursor.Edge + 1;
                if (next >= node.Edges.Length) continue;
                var edge = node.Edges[next];
                token.ThrowIfCancellationRequested(); values.Add(edge.Values);
                stack.Push((node, next)); stack.Push((edge.Suffix, -1));
            }
        }
        finally { values.Clear(); stack.Clear(); }
    }
}
