using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

// Physical identity is the actual directed row sequence, never requirements,
// aliases or current facts. These descriptors contain no decision relation.
internal sealed record AuditPhysicalRoute(AuditRoomOwner Owner, string Identity, int DiscoveryOrdinal, AuditStart Origin,
    AuditTarget Target, ImmutableArray<AuditRow> Steps, AuditRow? OwnGate);
internal sealed record AuditPhysicalRoutes(ImmutableArray<AuditPhysicalRoute> Paths,
    ImmutableArray<AuditAnalysisIssue> Unresolved);

public sealed partial class RoomAuditEvaluator
{
    // Reachability of the authored directed graph ONLY. Neither this closure nor
    // callers deciding physical disconnection may inspect a requirement or fact.
    private IReadOnlySet<Guid> PhysicalNodes(Guid seed, bool reverse = false)
    {
        var first = Room.NodeByGuid[seed];
        var reached = new HashSet<AuditNodeHandle> { first };
        var pending = new Queue<AuditNodeHandle>(); pending.Enqueue(first);
        while (pending.TryDequeue(out var current))
            foreach (var handle in (reverse ? Room.Incoming : Room.Outgoing)[current.Index])
            {
                var edge = Room.Edges[handle.Index];
                var neighbor = (reverse ? edge.From : edge.To)!.Value;
                if (reached.Add(neighbor)) pending.Enqueue(neighbor);
            }
        return reached.Select(node => Room.Nodes[node.Index].Id).ToHashSet();
    }

    private static string PhysicalStatus(string status, bool connected) =>
        status == "Inaccessible" && !connected ? "Disconnected" : status;

    internal AuditPhysicalRoutes DiscoverRoutes(AuditOriginScope scope, AuditTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var paths = ImmutableArray.CreateBuilder<AuditPhysicalRoute>();
        var issues = new List<AuditAnalysisIssue>();
        if (!Room.TargetByKey.TryGetValue(target, out var descriptor)) return new([], []);
        foreach (var start in ScopedStarts(scope))
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in descriptor.OwnRow is { } ownRow ? RowRoutes(ownRow, []) : Nodes(descriptor.Node!.Value, []))
            {
                token.ThrowIfCancellationRequested();
                var own = descriptor.OwnRow is null ? null : path[^1];
                var steps = own is null ? path : path.RemoveAt(path.Length - 1);
                paths.Add(new(Room.Owner, string.Join("/", new[] { start.Key }.Concat(path.Select(row => row.Id.ToString("D")))),
                    paths.Count, start, target, steps, own));
            }

            // This is the existing incoming authored discovery order and movement
            // guard, without CompileRow/Combine/admitted-relation work. A terminal
            // row is a goal gate, not another movement through its destination.
            List<ImmutableArray<AuditRow>> Nodes(AuditNodeHandle node, HashSet<AuditNodeHandle> ancestors)
            {
                token.ThrowIfCancellationRequested();
                if (Room.Nodes[node.Index].Id == start.NodeId) return [ImmutableArray<AuditRow>.Empty];
                if (ancestors.Contains(node)) return [];
                var next = new HashSet<AuditNodeHandle>(ancestors) { node };
                var result = new List<ImmutableArray<AuditRow>>();
                foreach (var edge in Room.IncomingAssignedConnections[node.Index])
                    foreach (var route in RowRoutes(Room.Edges[edge.Index].Row, next))
                    {
                        token.ThrowIfCancellationRequested();
                        if (route.Take(route.Length - 1).Any(row => Room.Row(row).From == node)) continue;
                        result.Add(route);
                    }
                return result;
            }

            List<ImmutableArray<AuditRow>> RowRoutes(AuditRowHandle handle, HashSet<AuditNodeHandle> ancestors)
            {
                token.ThrowIfCancellationRequested();
                var row = Room.Rows[handle.Index];
                if (row.AssignmentIssue is { } issue)
                { issues.Add(new(row.Source, issue)); return []; }
                var result = new List<ImmutableArray<AuditRow>>();
                foreach (var path in Nodes(row.From!.Value, ancestors))
                { token.ThrowIfCancellationRequested(); result.Add(path.Add(row.Source)); }
                return result;
            }
        }
        token.ThrowIfCancellationRequested();
        return new(paths.ToImmutable(), issues.Distinct().ToImmutableArray());
    }
}
