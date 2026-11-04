using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

// Handles are transient offsets in ONE prepared room, never session/persisted keys.
internal readonly record struct AuditNodeHandle(int Index);
internal readonly record struct AuditRowHandle(int Index);
internal readonly record struct AuditEdgeHandle(int Index);
internal sealed class AuditRoomOwner { }
internal sealed record PreparedAuditNode(AuditNodeHandle Handle, Guid Id, string Name);
internal sealed record PreparedAuditRow(AuditRowHandle Handle, AuditRow Source,
    AuditNodeHandle? From, AuditNodeHandle? To, string Name, string? AssignmentIssue);
internal sealed record PreparedAuditEdge(AuditEdgeHandle Handle, AuditRowHandle Row,
    AuditNodeHandle? From, AuditNodeHandle? To, bool Valid);
internal sealed record PreparedAuditTarget(AuditTarget Key, AuditNodeHandle? Node,
    AuditRowHandle? OwnRow, bool Valid);

/// <summary>
/// Immutable loaded topology/identity inventory. Adjacencies share edge handles;
/// invalid authored edges remain inspectable but never enter valid adjacency.
/// Contains no scenario, current status, route search or explanation result.
/// </summary>
internal sealed class PreparedAuditRoom
{
    internal AuditRoomOwner Owner { get; } = new();
    internal RoomAuditSnapshot SourceSnapshot { get; }
    internal bool Implicit => SourceSnapshot.Subrooms.Count == 0;
    internal ImmutableArray<PreparedAuditNode> Nodes { get; }
    internal ImmutableArray<PreparedAuditRow> Rows { get; }
    internal ImmutableArray<PreparedAuditEdge> Edges { get; }
    internal FrozenDictionary<Guid, AuditNodeHandle> NodeByGuid { get; }
    internal FrozenDictionary<(string Kind, Guid Id), AuditRowHandle> RowByKey { get; }
    internal FrozenDictionary<(string Kind, Guid Id), AuditEdgeHandle> EdgeByRowKey { get; }
    internal ImmutableArray<ImmutableArray<AuditEdgeHandle>> Outgoing { get; }
    internal ImmutableArray<ImmutableArray<AuditEdgeHandle>> Incoming { get; }
    // Includes edges with a resolved destination and unresolved source, so
    // physical discovery retains their ordered assignment diagnostics.
    internal ImmutableArray<ImmutableArray<AuditEdgeHandle>> IncomingAssignedConnections { get; }
    internal ImmutableArray<ImmutableArray<AuditRowHandle>> ChecksAt { get; }
    internal ImmutableArray<ImmutableArray<AuditRowHandle>> ExitsAt { get; }
    internal ImmutableArray<AuditStart> Starts { get; }
    internal FrozenDictionary<string, AuditStart> StartByKey { get; }
    internal FrozenDictionary<AuditNodeHandle, AuditStart> FirstStartByNode { get; }
    internal FrozenDictionary<AuditTarget, PreparedAuditTarget> TargetByKey { get; }
    internal FrozenDictionary<Guid, AuditCheckIdentity> CheckIdentityByGuid { get; }
    internal FrozenDictionary<Guid, AuditPredicateDefinition> PredicateByGuid { get; }
    internal ImmutableArray<AuditPredicateDefinitionPlan> PredicateDefinitions { get; }
    internal FrozenDictionary<Guid, AuditPredicateDefinitionPlan> DefinitionByGuid { get; }
    internal FrozenDictionary<(string Role, Guid Id), AuditValue> OperandByRoleAndGuid { get; }
    internal FrozenDictionary<(string Role, string Output), ImmutableArray<AuditValue>> OperandByOutput { get; }
    internal AuditSourceInventory Source { get; }
    internal PreparedAuditBindings Bindings { get; }

    internal AuditNodeHandle? ResolveNode(Guid? id) => Implicit ? new AuditNodeHandle(0) :
        id is { } value && NodeByGuid.TryGetValue(value, out var handle) ? handle : null;
    internal Guid? NodeId(AuditNodeHandle? handle) => handle is { } node ? Nodes[node.Index].Id : null;
    internal PreparedAuditRow Row(AuditRow source) => Rows[RowByKey[(source.Kind, source.Id)].Index];

    internal PreparedAuditRoom(RoomAuditSnapshot snapshot, CancellationToken token)
    {
        SourceSnapshot = snapshot;
        token.ThrowIfCancellationRequested();
        // Naming groups preserve existing case-insensitive disambiguation ONLY.
        // All identity indexes below use typed IDs/exact ordinal keys.
        var nodeNames = snapshot.Subrooms.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Select((node, index) => (node.Id,
                Name: group.Count() == 1 ? node.Name : $"{node.Name} (subroom {index + 1})")))
            .ToDictionary(x => x.Id, x => x.Name);
        Nodes = Implicit ? [new(new(0), snapshot.RoomId, "Room")] : snapshot.Subrooms
            .Select((node, index) => new PreparedAuditNode(new(index), node.Id, nodeNames[node.Id])).ToImmutableArray();
        NodeByGuid = Nodes.ToFrozenDictionary(x => x.Id, x => x.Handle);
        string NodeName(Guid? id) => id is { } value && NodeByGuid.TryGetValue(value, out var node)
            ? Nodes[node.Index].Name : "Unresolved subroom";

        var rowNames = new Dictionary<(string Kind, Guid Id), string>();
        foreach (var kind in snapshot.Rows.GroupBy(row => row.Kind))
            foreach (var namesakes in kind.GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var duplicate = namesakes.Count() > 1;
                foreach (var sameSource in namesakes.GroupBy(row => row.SourceId))
                {
                    var ordinal = 0;
                    var count = sameSource.Count();
                    foreach (var row in sameSource)
                    {
                        ordinal++;
                        var name = row.Kind == "connection" ? $"{row.Name}: {NodeName(row.SourceId)} → {NodeName(row.DestinationId)}" :
                            !duplicate ? row.Name : $"{row.Name} — {NodeName(NodeId(ResolveNode(row.SourceId)))}{(count > 1 ? $" ({(row.Kind == "exit" ? "exit" : "check")} {ordinal})" : "")}";
                        rowNames.Add((row.Kind, row.Id), name);
                    }
                }
            }
        var rows = ImmutableArray.CreateBuilder<PreparedAuditRow>(snapshot.Rows.Count);
        var edges = ImmutableArray.CreateBuilder<PreparedAuditEdge>();
        var rowByKey = new Dictionary<(string Kind, Guid Id), AuditRowHandle>();
        var edgeByRowKey = new Dictionary<(string Kind, Guid Id), AuditEdgeHandle>();
        var outgoing = Buckets<AuditEdgeHandle>(Nodes.Length);
        var incoming = Buckets<AuditEdgeHandle>(Nodes.Length);
        var assigned = Buckets<AuditEdgeHandle>(Nodes.Length);
        var checks = Buckets<AuditRowHandle>(Nodes.Length);
        var exits = Buckets<AuditRowHandle>(Nodes.Length);
        var targets = new Dictionary<AuditTarget, PreparedAuditTarget>();
        foreach (var node in Nodes)
        {
            var key = new AuditTarget(Implicit ? "room" : "subroom", node.Id);
            targets.Add(key, new(key, node.Handle, null, true));
        }
        foreach (var source in snapshot.Rows)
        {
            token.ThrowIfCancellationRequested();
            var handle = new AuditRowHandle(rows.Count);
            var from = ResolveNode(source.SourceId);
            var to = source.Kind == "connection" && !Implicit ? ResolveNode(source.DestinationId) : null;
            var valid = from is not null && (source.Kind != "connection" || !Implicit && to is not null);
            rows.Add(new(handle, source, from, to, rowNames[(source.Kind, source.Id)],
                valid ? null : "Unresolved subroom assignment"));
            rowByKey.Add((source.Kind, source.Id), handle);
            var target = new AuditTarget(source.Kind, source.Id);
            targets.Add(target, new(target, from, handle, valid));
            if (source.Kind == "connection")
            {
                var edge = new AuditEdgeHandle(edges.Count);
                edges.Add(new(edge, handle, from, to, valid));
                edgeByRowKey.Add((source.Kind, source.Id), edge);
                if (to is { } destination) assigned[destination.Index].Add(edge);
                if (valid)
                {
                    outgoing[from!.Value.Index].Add(edge);
                    incoming[to!.Value.Index].Add(edge);
                }
            }
            else if (from is { } location)
            {
                if (source.Kind == "check") checks[location.Index].Add(handle);
                if (source.Kind == "exit") exits[location.Index].Add(handle);
            }
        }
        Rows = rows.ToImmutable(); Edges = edges.ToImmutable();
        RowByKey = rowByKey.ToFrozenDictionary(); EdgeByRowKey = edgeByRowKey.ToFrozenDictionary();
        Outgoing = Freeze(outgoing); Incoming = Freeze(incoming);
        IncomingAssignedConnections = Freeze(assigned); ChecksAt = Freeze(checks); ExitsAt = Freeze(exits);
        TargetByKey = targets.ToFrozenDictionary();
        var starts = ImmutableArray.CreateBuilder<AuditStart>();
        foreach (var row in Rows)
            if (row.Source.Kind is "exit" or "check")
                starts.Add(new($"{row.Source.Kind}:{row.Source.Id}",
                    row.Name + (row.From is null ? " — unresolved assignment" : ""), NodeId(row.From), row.From is not null));
        if (Implicit) starts.Add(new("room", "Implicit room", snapshot.RoomId, true));
        else foreach (var node in Nodes) starts.Add(new($"subroom:{node.Id}", node.Name, node.Id, true));
        Starts = starts.ToImmutable(); StartByKey = Starts.ToFrozenDictionary(x => x.Key, StringComparer.Ordinal);
        var first = new Dictionary<AuditNodeHandle, AuditStart>();
        foreach (var start in Starts)
            if (start.Valid) first.TryAdd(NodeByGuid[start.NodeId!.Value], start);
        FirstStartByNode = first.ToFrozenDictionary();
        CheckIdentityByGuid = snapshot.Checks.ToFrozenDictionary(x => x.Id);
        PredicateByGuid = snapshot.Predicates.ToFrozenDictionary(x => x.Id);
        PredicateDefinitions = snapshot.Predicates.Select((definition, index) => new AuditPredicateDefinitionPlan(
            definition, index, AuditPredicateSemantics.Classify(definition.OutputSyntax))).ToImmutableArray();
        DefinitionByGuid = PredicateDefinitions.ToFrozenDictionary(x => x.Definition.Id);
        (string Role, IReadOnlyList<AuditValue> Values)[] catalogues = [("item", snapshot.Items), ("crest", snapshot.Crests),
            ("method", snapshot.Methods), ("soul", snapshot.Souls), ("countable", snapshot.Countables)
            ];
        var values = catalogues.SelectMany(catalogue => catalogue.Values.Select(value => (catalogue.Role, Value: value))).ToArray();
        OperandByRoleAndGuid = values.ToFrozenDictionary(x => (x.Role, x.Value.Id), x => x.Value);
        OperandByOutput = values.GroupBy(x => (x.Role, x.Value.OutputValue))
            .ToFrozenDictionary(x => x.Key, x => x.Select(value => value.Value).ToImmutableArray());
        Source = new(this, token);
        Bindings = new AuditApplicationBinder(this, token).Prepare();
        token.ThrowIfCancellationRequested();
    }

    private static List<T>[] Buckets<T>(int count) => Enumerable.Range(0, count).Select(_ => new List<T>()).ToArray();
    private static ImmutableArray<ImmutableArray<T>> Freeze<T>(List<T>[] buckets) =>
        buckets.Select(x => x.ToImmutableArray()).ToImmutableArray();
}
