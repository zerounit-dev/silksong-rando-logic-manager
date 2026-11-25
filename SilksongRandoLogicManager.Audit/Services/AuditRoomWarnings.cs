using System.Collections.Immutable;
using System.Numerics;

namespace Silksong_Rando_Logic_Manager.Services;

public enum AuditWarningKind { IslandWithExit, IslandWithoutExit, NoDirectedPath, DirectSelfLock, ApproachSelfLock, LocalDeadlock, AlternativeDeadlock }
public sealed record AuditWarningClause(string Point, ImmutableArray<string> NeedsOneOf);
public sealed record AuditRoomWarning(AuditWarningKind Kind, ImmutableArray<string> Points,
    ImmutableArray<AuditWarningClause> Clauses = default);
public sealed record AuditRoomWarningResult(string? Origin, ImmutableArray<AuditRoomWarning> Findings);

/// <summary>Room-local proof over the existing bound gates, not the current scenario or Details.</summary>
internal sealed class AuditRoomWarningAnalysis
{
    private enum Op { Input, Unknown, All, Any }
    private sealed record Signal(Op Op, BigInteger Mask, List<int> Sources, List<int> Users);

    private readonly PreparedAuditRoom room;
    private readonly CancellationToken token;
    private readonly List<Signal> graph = [];
    private readonly PreparedAuditRow[] checks;
    private readonly Dictionary<Guid, int> checkIndex;
    private readonly int[] local, gates, places, acquisitions;
    private readonly Dictionary<string, string> pointNames;
    private readonly BigInteger universe;
    private readonly int unknown;

    internal static AuditRoomWarningResult Analyze(PreparedAuditRoom room, string origin, CancellationToken token) =>
        new AuditRoomWarningAnalysis(room, token).Find(origin);

    private AuditRoomWarningAnalysis(PreparedAuditRoom room, CancellationToken token)
    {
        this.room = room; this.token = token;
        pointNames = BuildPointNames(room);
        checks = room.Rows.Where(row => row.Source.Kind == "check").ToArray();
        checkIndex = checks.Select((row, i) => (row.Source.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var arena = room.Bindings.Prerequisites;
        // The binder has positive thresholds and positive inventory/local facts. Its
        // finite two-sided conditions are Act, SoulMode and up to six setting keys.
        // Factor only settings used negatively here; positive-only inputs may all
        // be available together. Excess future negative keys are unknown, not false.
        var split = arena.Nodes.Select(node => node.Condition)
            .OfType<AuditConstraint>().Where(c => c.Key.Kind == AuditConditionKind.Fact && c.Value == 0 &&
                room.Bindings.ControlByKey.GetValueOrDefault(c.Key.Key)?.LocalCheckId is null)
            .Select(c => c.Key).Distinct().Take(6).Select((key, i) => (key, i)).ToDictionary(x => x.key, x => x.i);
        var lanes = 6 << split.Count;
        universe = (BigInteger.One << lanes) - 1;
        BigInteger Mask(Func<int, bool> admits)
        {
            var result = BigInteger.Zero;
            for (var lane = 0; lane < lanes; lane++)
                if (admits(lane)) result |= BigInteger.One << lane;
            return result;
        }
        var acts = Enumerable.Range(0, 8).Select(mask => Mask(lane => (mask & (1 << (lane % 3))) != 0)).ToArray();
        var soulOn = Mask(lane => (lane / 3 & 1) != 0);
        var settingOn = split.ToDictionary(x => x.Key, x => Mask(lane => (lane / 6 & (1 << x.Value)) != 0));

        var never = Add(Op.Input); var always = Add(Op.Input, universe); unknown = Add(Op.Unknown);
        local = checks.Select(_ => Add(Op.Input)).ToArray();
        places = room.Nodes.Select(_ => Add(Op.Any)).ToArray();
        var bound = new int[arena.Nodes.Length];
        for (var i = 0; i < bound.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var node = arena.Nodes[i];
            bound[i] = node.Operator switch
            {
                AuditPrerequisiteOperator.True => always,
                AuditPrerequisiteOperator.False => never,
                AuditPrerequisiteOperator.Unresolved => unknown,
                AuditPrerequisiteOperator.Condition => Condition(node.Condition!.Value),
                AuditPrerequisiteOperator.All => Join(Op.All, node.Children.Select(child => bound[child.Index])),
                AuditPrerequisiteOperator.Any => Join(Op.Any, node.Children.Select(child => bound[child.Index])),
                _ => throw new InvalidOperationException("Unknown bound audit gate")
            };
        }
        int Condition(AuditConstraint c)
        {
            if (c.Key.Kind == AuditConditionKind.Act) return Add(Op.Input, acts[c.Value & 7]);
            if (c.Key.Kind == AuditConditionKind.SoulMode) return Add(Op.Input, c.Value == 1 ? soulOn : universe ^ soulOn);
            if (c.Key.Kind == AuditConditionKind.Level) return c.Value <= 4 ? always : never;
            if (room.Bindings.ControlByKey.GetValueOrDefault(c.Key.Key)?.LocalCheckId is { } id)
                return c.Value == 1 && checkIndex.TryGetValue(id, out var check) ? local[check] : unknown;
            return settingOn.TryGetValue(c.Key, out var on) ? Add(Op.Input, c.Value == 1 ? on : universe ^ on) :
                c.Value == 1 ? always : unknown;
        }

        gates = room.Source.RowRoots.Select(root => Compile(root, bound)).ToArray();
        // Only prepared valid same-room outgoing edges contribute movement.
        foreach (var bucket in room.Outgoing)
            foreach (var handle in bucket)
            {
                token.ThrowIfCancellationRequested();
                var edge = room.Edges[handle.Index];
                Wire(Join(Op.All, [places[edge.From!.Value.Index], gates[edge.Row.Index]]), places[edge.To!.Value.Index]);
            }
        acquisitions = checks.Select(row => Join(Op.All,
            [row.From is { } from ? places[from.Index] : never, gates[row.Handle.Index]])).ToArray();
    }

    private static Dictionary<string, string> BuildPointNames(PreparedAuditRoom room)
    {
        // Keep Audit display names; identify collisions using both source and
        // already-disambiguated display names. Keys follow the prepared starts.
        var points = room.Nodes.Select((node, index) =>
                (Key: room.Implicit ? "room" : $"subroom:{node.Id}",
                    Source: room.Implicit ? node.Name : room.SourceSnapshot.Subrooms[index].Name,
                    Label: room.Implicit ? room.StartByKey["room"].Label : node.Name, Kind: room.Implicit ? "room" : "subroom"))
            .Concat(room.Rows.Where(row => row.Source.Kind is "exit" or "check").Select(row =>
                (Key: $"{row.Source.Kind}:{row.Source.Id}", Source: row.Source.Name, Label: row.Name,
                    Kind: row.Source.Kind == "exit" ? "exit" : CheckLocationTypeCatalogue.IsEnemy(row.Source.LocationType) ? "enemy" : "check")))
            .ToArray();
        var colliding = points.SelectMany(p => new[] { p.Source, p.Label }.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var qualified = points.Select(p => (p.Key, p.Kind, Label: colliding.Contains(p.Source) || colliding.Contains(p.Label)
            ? $"{p.Label} ({p.Kind})" : p.Label)).ToArray();
        // Authored names may themselves look like either kind of suffix. Reserve
        // every provisional label before allocating numbered labels, including
        // labels coming from a different category or a later point.
        var reserved = qualified.Select(p => p.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in qualified.GroupBy(p => p.Label, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
            var ordinal = 0;
            foreach (var point in ordered)
            {
                var label = point.Label;
                if (ordered.Length > 1)
                    do { label = FormattableString.Invariant($"{point.Label} [{point.Kind} #{++ordinal}]"); }
                    while (!reserved.Add(label));
                result.Add(point.Key, label);
            }
        }
        return result;
    }

    private string CheckName(int index) => pointNames[$"check:{checks[index].Source.Id}"];

    private int Compile(PreparedAuditExpression root, int[] bound)
    {
        var pending = new Stack<(PreparedAuditExpression Expression, bool Combine)>();
        var values = new Stack<int>(); pending.Push((root, false));
        while (pending.TryPop(out var frame))
        {
            token.ThrowIfCancellationRequested();
            switch (frame.Expression)
            {
                case PreparedAuditBoolean both when frame.Combine:
                    var right = values.Pop(); var left = values.Pop();
                    values.Push(Join(both.IsAnd ? Op.All : Op.Any, [left, right])); break;
                case PreparedAuditBoolean either:
                    pending.Push((either, true)); pending.Push((either.Right, false)); pending.Push((either.Left, false)); break;
                case PreparedAuditApplicationUse use:
                    var application = room.Source.Occurrences[use.Occurrence.Index].Application;
                    values.Push(bound[room.Bindings.Applications[application.Index].Prerequisite.Root.Index]); break;
                case PreparedAuditUnresolved: values.Push(unknown); break;
                default: throw new InvalidOperationException("Unknown prepared audit gate");
            }
        }
        return values.Pop();
    }

    private int Add(Op op, BigInteger mask = default) { graph.Add(new(op, mask, [], [])); return graph.Count - 1; }
    private void Wire(int source, int target) { graph[source].Users.Add(target); graph[target].Sources.Add(source); }
    private int Join(Op op, IEnumerable<int> sources)
    {
        var id = Add(op);
        foreach (var source in sources) Wire(source, id);
        return id;
    }

    // Each bit at each signal can rise only once. Each invocation is a separate
    // world: only its own origin, hypotheses and acquisitions can supply facts.
    private BigInteger[] Solve(bool upper, int? origin, BigInteger[] seeds, bool collect = false,
        int withhold = -1, int alsoWithhold = -1)
    {
        var values = new BigInteger[graph.Count];
        var excluded = withhold < 0 ? -1 : local[withhold];
        var alsoExcluded = alsoWithhold < 0 ? -1 : local[alsoWithhold];
        var queue = new Queue<int>(); var pending = new bool[graph.Count];
        void Grant(int signal, BigInteger bits)
        {
            var delta = bits & ~values[signal];
            if (delta.IsZero || signal == excluded || signal == alsoExcluded) return;
            values[signal] |= delta;
            if (!pending[signal]) { pending[signal] = true; queue.Enqueue(signal); }
        }
        for (var i = 0; i < graph.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var signal = graph[i];
            if (signal.Op == Op.Input) Grant(i, signal.Mask);
            else if (signal.Op == Op.Unknown && upper || signal.Op == Op.All && signal.Sources.Count == 0) Grant(i, universe);
        }
        if (origin is { } start) Grant(places[start], universe);
        for (var i = 0; i < local.Length; i++) Grant(local[i], seeds[i]);
        while (queue.TryDequeue(out var source))
        {
            token.ThrowIfCancellationRequested(); pending[source] = false;
            foreach (var user in graph[source].Users)
            {
                var bits = values[source];
                if (graph[user].Op == Op.All)
                    foreach (var input in graph[user].Sources) bits &= values[input];
                Grant(user, bits);
            }
            if (collect && collectionAt.TryGetValue(source, out var fact)) Grant(fact, values[source]);
        }
        return values;
    }

    // Hypotheses are frozen. The target fact may help reach its placement, but
    // cannot satisfy its own first activation. The tested dependency is absent
    // in both probes; a bypass must have a common lane in approach and own gate.
    private bool CanActivateFirst(int origin, int point, BigInteger[] hypotheses, int withheld = -1)
    {
        var row = checks[point];
        if (row.From is not { } from) return false;
        var approach = Solve(true, origin, hypotheses, withhold: withheld)[places[from.Index]];
        if (approach.IsZero) return false;
        var activation = UpperOwnActivation(point, hypotheses, withheld);
        return !(approach & activation).IsZero;
    }

    private BigInteger UpperOwnActivation(int point, BigInteger[] hypotheses, int withheld = -1) =>
        Solve(true, null, hypotheses, withhold: withheld, alsoWithhold: point)[gates[checks[point].Handle.Index]];

    private readonly Dictionary<int, int> collectionAt = [];

    // Descend from all hypothetical facts so mutually supporting distinct
    // checks survive. In deadlock inventory each bit needs both placement
    // (self may help) and a self-free upper activation in that same lane.
    // With no origin this remains the separate own-gate hypothesis used to
    // ask whether an authored check requires itself in the first place.
    private BigInteger[] Viable(int? origin)
    {
        var may = checks.Select(_ => universe).ToArray();
        var next = new BigInteger[may.Length];
        var ownReferences = origin is null ? null : OwnGateSelfReferences();
        bool changed;
        do
        {
            var flow = Solve(true, origin, may); changed = false;
            for (var i = 0; i < may.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var allowed = may[i] & flow[origin is null ? gates[checks[i].Handle.Index] : acquisitions[i]];
                if (!allowed.IsZero && ownReferences?[i] == true)
                    allowed &= UpperOwnActivation(i, may);
                next[i] = allowed;
                changed |= allowed != may[i];
            }
            (may, next) = (next, may);
        } while (changed);
        return may;
    }

    // Inspect the compiled own-gate signals once. For gates that cannot read
    // their own fact, the initial sweep already computes the same activation.
    private bool[] OwnGateSelfReferences()
    {
        var result = new bool[checks.Length];
        var visited = new int[graph.Count];
        var pending = new Stack<int>();
        for (var i = 0; i < checks.Length; i++)
        {
            if (graph[local[i]].Users.Count == 0) continue;
            pending.Push(gates[checks[i].Handle.Index]);
            while (pending.TryPop(out var signal))
            {
                token.ThrowIfCancellationRequested();
                if (signal == local[i]) { result[i] = true; pending.Clear(); break; }
                if (visited[signal] == i + 1) continue;
                visited[signal] = i + 1;
                foreach (var source in graph[signal].Sources) pending.Push(source);
            }
        }
        return result;
    }

    private AuditRoomWarningResult Find(string key)
    {
        var findings = ImmutableArray.CreateBuilder<AuditRoomWarning>();
        var start = room.StartByKey.GetValueOrDefault(key);
        int? origin = start is { Valid: true, NodeId: { } id } ? room.NodeByGuid[id].Index : null;
        for (var i = 0; i < local.Length; i++) collectionAt.Add(acquisitions[i], local[i]);

        var noOrigin = Viable(null);
        var readableGates = Solve(false, null, noOrigin);
        var direct = new bool[checks.Length];
        for (var i = 0; i < checks.Length; i++)
        {
            var gate = gates[checks[i].Handle.Index];
            if (graph[local[i]].Users.Count == 0 || readableGates[gate].IsZero) continue;
            direct[i] = Solve(true, null, noOrigin, withhold: i)[gate].IsZero;
            if (direct[i]) findings.Add(new(AuditWarningKind.DirectSelfLock, [CheckName(i)]));
        }
        if (origin is not { } seed) return new(null, findings.ToImmutable());

        var reached = AddTopology(seed, findings);

        var empty = new BigInteger[checks.Length];
        var possibleFirst = Solve(true, seed, empty, collect: true);
        var approach = new bool[checks.Length];
        for (var i = 0; i < checks.Length; i++)
        {
            if (direct[i] || graph[local[i]].Users.Count == 0 || checks[i].From is not { } from || from.Index == seed ||
                !reached[from.Index] || !possibleFirst[places[from.Index]].IsZero) continue;
            var place = places[from.Index];
            if (!Solve(true, seed, empty, collect: true, withhold: i)[place].IsZero) continue;
            empty[i] = universe;
            approach[i] = !Solve(false, seed, empty, collect: true)[place].IsZero;
            empty[i] = 0;
        }

        var inLoop = AddDeadlocks(seed, possibleFirst, direct, findings);
        for (var i = 0; i < checks.Length; i++)
            if (approach[i] && !inLoop.Contains(i)) findings.Add(new(AuditWarningKind.ApproachSelfLock, [CheckName(i)]));

        return new(pointNames[key], findings.ToImmutable());
    }

    private bool[] AddTopology(int origin, ImmutableArray<AuditRoomWarning>.Builder findings)
    {
        // Ignore ordinary locks per link, not along a whole route. Keep authored
        // False/contradictions, and distinguish a readable option from unknown.
        var granted = checks.Select(_ => universe).ToArray();
        var lowerGates = Solve(false, null, granted);
        var upperGates = Solve(true, null, granted);
        var lower = room.Edges.Select(edge => edge.Valid && !lowerGates[gates[edge.Row.Index]].IsZero).ToArray();
        var upper = room.Edges.Select(edge => edge.Valid && !upperGates[gates[edge.Row.Index]].IsZero).ToArray();
        var lowerDirected = FloodFromOrigin(origin, lower);
        var upperDirected = FloodFromOrigin(origin, upper);
        var lowerWeak = WeakAreas(lower);
        var upperWeak = WeakAreas(upper);
        var lowerMain = lowerWeak.ByNode[origin];
        var upperMain = upperWeak.ByNode[origin];
        foreach (var members in lowerWeak.Members)
        {
            token.ThrowIfCancellationRequested();
            var possible = upperWeak.ByNode[members[0]];
            if (possible == upperMain || upperWeak.Members[possible].Count != members.Count) continue;
            var hasExit = members.Any(i => !room.ExitsAt[i].IsEmpty);
            findings.Add(new(hasExit ? AuditWarningKind.IslandWithExit : AuditWarningKind.IslandWithoutExit, Names(members)));
        }

        // These induced subsets bound the unreachable area in EVERY intermediate
        // graph. A node only reachable in the upper graph can still join the area
        // through one unknown edge before a different edge makes it reachable.
        var certain = WeakAreas(lower, i => lowerWeak.ByNode[i] == lowerMain && !upperDirected[i]);
        var potential = WeakAreas(upper, i => upperWeak.ByNode[i] == upperMain && !lowerDirected[i]);
        foreach (var members in certain.Members)
        {
            token.ThrowIfCancellationRequested();
            var possible = potential.ByNode[members[0]];
            // Lower members are a subset of the same upper-induced component;
            // equal counts therefore prove exact membership without set products.
            if (potential.Members[possible].Count == members.Count)
                findings.Add(new(AuditWarningKind.NoDirectedPath, Names(members)));
        }
        return upperDirected; // The existing Self-Lock topology prefilter.

        ImmutableArray<string> Names(List<int> members) => members.Select(i => pointNames[$"subroom:{room.Nodes[i].Id}"]).ToImmutableArray();
    }

    private bool[] FloodFromOrigin(int origin, bool[] allowed)
    {
        var reached = new bool[places.Length]; var pending = new Queue<int>();
        reached[origin] = true; pending.Enqueue(origin);
        while (pending.TryDequeue(out var from))
        {
            token.ThrowIfCancellationRequested();
            foreach (var handle in room.Outgoing[from])
            {
                if (!allowed[handle.Index]) continue;
                var to = room.Edges[handle.Index].To!.Value.Index;
                if (!reached[to]) { reached[to] = true; pending.Enqueue(to); }
            }
        }
        return reached;
    }

    private (int[] ByNode, List<List<int>> Members) WeakAreas(bool[] allowed, Func<int, bool>? include = null)
    {
        var byNode = new int[places.Length]; Array.Fill(byNode, -1);
        var groups = new List<List<int>>(); var pending = new Queue<int>();
        for (var first = 0; first < byNode.Length; first++)
        {
            token.ThrowIfCancellationRequested();
            if (byNode[first] >= 0 || include?.Invoke(first) == false) continue;
            var group = groups.Count; groups.Add([]);
            Visit(first, group);
            while (pending.TryDequeue(out var node))
            {
                token.ThrowIfCancellationRequested();
                foreach (var edge in room.Outgoing[node])
                    if (allowed[edge.Index]) Visit(room.Edges[edge.Index].To!.Value.Index, group);
                foreach (var edge in room.Incoming[node])
                    if (allowed[edge.Index]) Visit(room.Edges[edge.Index].From!.Value.Index, group);
            }
        }
        // Retain Audit node order inside each group regardless of edge ordering.
        for (var i = 0; i < byNode.Length; i++)
            if (byNode[i] >= 0) groups[byNode[i]].Add(i);
        return (byNode, groups);

        void Visit(int node, int group)
        {
            if (byNode[node] >= 0 || include?.Invoke(node) == false) return;
            byNode[node] = group;
            pending.Enqueue(node);
        }
    }

    private HashSet<int> AddDeadlocks(int origin, BigInteger[] upper, bool[] direct,
        ImmutableArray<AuditRoomWarning>.Builder findings)
    {
        var inLoop = new HashSet<int>();
        var blocked = checks.Select((_, i) => upper[acquisitions[i]].IsZero && !direct[i]).ToArray();
        if (blocked.Count(x => x) < 2) return inLoop;
        var viable = Viable(origin);
        var actual = Solve(false, origin, new BigInteger[checks.Length], collect: true);
        var support = checks.Select((_, i) => blocked[i] ? viable[i] : actual[acquisitions[i]]).ToArray();
        // Remove hypothetical support that itself has no readable, coherent
        // acquisition without assuming that same check. Start with every plausible
        // hypothesis so that real mutually supporting checks survive the pruning.
        var blockedIndices = Enumerable.Range(0, checks.Length).Where(i => blocked[i] && !support[i].IsZero).ToArray();
        bool changed;
        do
        {
            changed = false;
            foreach (var i in blockedIndices)
            {
                if (support[i].IsZero) continue;
                var next = support[i] & Solve(false, origin, support, withhold: i)[acquisitions[i]];
                if (next != support[i]) { support[i] = next; changed = true; }
            }
        } while (changed);

        var candidates = blockedIndices.Where(i => !support[i].IsZero).ToArray();
        if (candidates.Length < 2) return inLoop;
        var requires = checks.Select(_ => new List<int>()).ToArray();
        foreach (var prerequisite in candidates)
        {
            // The edge must survive an optimistic counterfactual across ALL lanes,
            // with the acquiring check permitted in approach only (see helper).
            foreach (var point in candidates)
                if (point != prerequisite && !CanActivateFirst(origin, point, viable, prerequisite))
                    requires[point].Add(prerequisite);
        }

        // Only use cycles whose members have definite readable support in a
        // common lane. The upper comparisons, unlike readability, are unscoped
        // to that lane so off-lane or unknown options still count as bypasses.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var visitedProfiles = new HashSet<string>(StringComparer.Ordinal);
        var lanes = candidates.Aggregate(BigInteger.Zero, (bits, i) => bits | support[i]);
        while (!lanes.IsZero)
        {
            token.ThrowIfCancellationRequested();
            var lane = lanes & -lanes; lanes ^= lane;
            var active = candidates.Where(i => !(support[i] & lane).IsZero).ToArray();
            if (active.Length < 2 || !visitedProfiles.Add(string.Join(",", active))) continue;
            var included = active.ToHashSet();
            var dependencies = requires.Select((next, i) => included.Contains(i) ? next.Where(included.Contains).ToArray() : []).ToArray();
            foreach (var cycle in ProvenCycles(dependencies))
            {
                var first = Array.IndexOf(cycle, cycle.Min());
                var ordered = cycle.Skip(first).Concat(cycle.Take(first)).ToArray();
                if (reported.Add(string.Join(",", ordered)))
                {
                    inLoop.UnionWith(cycle);
                    findings.Add(new(AuditWarningKind.LocalDeadlock, ordered.Select(CheckName).ToImmutableArray()));
                }
            }
        }
        AddAlternativeDeadlocks(origin, candidates, viable, support, inLoop, findings);
        return inLoop;
    }

    private void AddAlternativeDeadlocks(int origin, int[] candidates, BigInteger[] viable, BigInteger[] support,
        HashSet<int> inLoop, ImmutableArray<AuditRoomWarning>.Builder findings)
    {
        if (candidates.Count(i => !inLoop.Contains(i)) < 2) return;
        var remaining = new SortedSet<int>(candidates.Where(i => !inLoop.Contains(i)));
        var grant = (BigInteger[])viable.Clone();
        foreach (var i in remaining) grant[i] = 0;

        // Descending closed set: treat everything outside (including previously
        // reported loops) as potentially available. Drop any candidate that can
        // activate without another member, even if the approach needs its own fact.
        bool changed;
        do
        {
            changed = false;
            foreach (var i in remaining.ToArray())
            {
                grant[i] = viable[i];
                if (CanActivateFirst(origin, i, grant))
                { remaining.Remove(i); changed = true; }
                else grant[i] = 0;
            }
        } while (changed && remaining.Count >= 2);
        if (remaining.Count < 2) return;

        // For each member shrink its withheld set by monotone grants. Failure
        // to acquire proves that at least one of the still-withheld names is
        // necessary. It does not claim that each name is necessary or sufficient.
        var dependencies = checks.Select(_ => Array.Empty<int>()).ToArray();
        foreach (var point in remaining)
        {
            var trial = (BigInteger[])grant.Clone();
            trial[point] = viable[point];
            var clause = new List<int>();
            foreach (var other in remaining)
            {
                if (other == point) continue;
                trial[other] = viable[other];
                if (CanActivateFirst(origin, point, trial))
                { trial[other] = 0; clause.Add(other); }
            }
            dependencies[point] = clause.ToArray();
        }

        // A sink strongly connected component of proved clauses is closed:
        // every clause points inside it and at least one member must go first,
        // which the upper counterfactual already rules out. Omit feeders rather
        // than listing any merely inaccessible check as part of the deadlock.
        var reverse = checks.Select(_ => new List<int>()).ToArray();
        foreach (var point in remaining)
            foreach (var next in dependencies[point]) reverse[next].Add(point);
        var considered = new HashSet<int>();
        foreach (var point in remaining)
        {
            if (considered.Contains(point)) continue;
            var group = Reach(point, dependencies);
            group.IntersectWith(Reach(point, reverse));
            considered.UnionWith(group);
            if (group.Count < 2 || group.Any(i => dependencies[i].Length == 0 || dependencies[i].Any(next => !group.Contains(next))) ||
                group.Aggregate(universe, (mask, i) => mask & support[i]).IsZero) continue;
            inLoop.UnionWith(group);
            var named = group.Order().ToArray();
            findings.Add(new(AuditWarningKind.AlternativeDeadlock, named.Select(CheckName).ToImmutableArray(),
                named.Select(i => new AuditWarningClause(CheckName(i), dependencies[i].Select(CheckName).ToImmutableArray())).ToImmutableArray()));
        }

        HashSet<int> Reach(int start, IReadOnlyList<int>[] adjacency)
        {
            var seen = new HashSet<int> { start }; var pending = new Stack<int>(); pending.Push(start);
            while (pending.TryPop(out var node))
            {
                token.ThrowIfCancellationRequested();
                foreach (var next in adjacency[node])
                    if (seen.Add(next)) pending.Push(next);
            }
            return seen;
        }
    }

    private IEnumerable<int[]> ProvenCycles(int[][] dependencies)
    {
        // Iterative DFS emits only back-edge witness chains, never enumerating
        // complete dependency routes or every rotation of the same cycle.
        var state = new byte[dependencies.Length]; var path = new List<int>();
        var frames = new Stack<(int Check, int Next)>();
        for (var i = 0; i < state.Length; i++)
        {
            if (state[i] != 0 || dependencies[i].Length == 0) continue;
            Enter(i);
            while (frames.TryPop(out var frame))
            {
                token.ThrowIfCancellationRequested();
                if (frame.Next == dependencies[frame.Check].Length)
                { state[frame.Check] = 2; path.RemoveAt(path.Count - 1); continue; }
                frames.Push((frame.Check, frame.Next + 1));
                var next = dependencies[frame.Check][frame.Next];
                if (state[next] == 0) Enter(next);
                else if (state[next] == 1) yield return path.Skip(path.IndexOf(next)).ToArray();
            }
        }
        void Enter(int check) { state[check] = 1; path.Add(check); frames.Push((check, 0)); }
    }
}
