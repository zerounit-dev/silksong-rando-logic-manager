using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class AuditScenario
{
    public string Start { get; set; } = "";
    public int Act { get; set; } = 1;
    public bool SoulRandomizer { get; set; }
    public bool AutoCollect { get; set; }
    public Dictionary<string, bool> Facts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Levels { get; set; } = new(StringComparer.Ordinal);
    public bool Has(string key) => Facts.GetValueOrDefault(key);
    public int Level(string key) => Levels.GetValueOrDefault(key);
}
public sealed record AuditControl(string Key, string Label, string Group, bool Difficulty = false, Guid? LocalCheckId = null, bool RequiresAdvanced = false);
public sealed record AuditStart(string Key, string Label, Guid? NodeId, bool Valid);
public sealed record AuditPrerequisite(bool Passed, string Label, bool Unresolved,
    bool Alternatives, IReadOnlyList<AuditPrerequisite> Children, string? ControlKey = null);
public sealed record AuditLeafResult(AuditAtom Source, bool Passed, string Explanation, bool Unresolved = false,
    AuditPrerequisite? Evidence = null, string? ControlKey = null, bool Manual = false, Guid? LocalCheckId = null, bool WitnessUsed = false,
    IReadOnlyList<string>? Notices = null)
{
    internal AuditApplicationHandle Application { get; init; }
    internal AuditOccurrenceHandle Occurrence { get; init; }
}
public sealed record AuditRowResult(bool Available, string Access, ImmutableArray<AuditLeafResult> Leaves, bool GatePassed);
public sealed record AuditAdmissionFacts(int Act, bool SoulRandomizer, ImmutableDictionary<string, bool> Facts,
    ImmutableDictionary<string, int> Levels, ImmutableHashSet<Guid> Collected, AuditRequirementsContext Requirements);
// Admission facts and leaf truth are immutable snapshots, not a later replay under
// final Auto facts. NodeId is the destination, SourceNodeId its actual predecessor.
public sealed record AuditProof(Guid NodeId, Guid? SourceNodeId, AuditRow? Via, AuditProof? Source,
    ImmutableArray<AuditLeafResult> Leaves, int Order, AuditAdmissionFacts Admission);
public sealed record AuditEvaluation(IReadOnlySet<Guid> Reached,
    IReadOnlyDictionary<(string Kind, Guid Id), AuditRowResult> Rows, bool HasOrigin,
    IReadOnlyDictionary<Guid, AuditProof> NodeProofs, AuditFactContext Facts, IReadOnlySet<Guid> PhysicalReached);

/// <summary>One prepared room, direct origin-local current evaluation and physical graph operations.</summary>
public sealed partial class RoomAuditEvaluator
{
    public static readonly string[] DifficultyNames = ["None", "Easy", "Medium", "Hard", "CBT"];
    public static readonly (string Key, string Label, string[] Directions)[] Movement =
    [
        ("heal", "Heal", []),
        ("needle", "Needle Attack", ["left", "right", "up"]),
        ("dash", "Swift Step", ["run", "left", "right"]),
        ("ledge-grab", "Ledge Grab", ["left", "right"]),
        ("cling-grip", "Cling Grip", ["left", "right"]),
        ("swim", "Swim", []), ("drifters-cloak", "Drifter's Cloak", []),
        ("faydown-cloak", "Faydown Cloak", []), ("clawline", "Clawline", ["left", "right"]),
        ("silk-soar", "Silk Soar", [])
    ];
    private readonly RoomAuditSnapshot snapshot;
    internal PreparedAuditRoom Room { get; }
    public IReadOnlyCollection<AuditControl> Controls => Room.Bindings.Controls;
    public IReadOnlyList<AuditStart> Starts => Room.Starts;
    public RoomAuditSnapshot Snapshot => snapshot;
    public RoomAuditEvaluator(RoomAuditSnapshot snapshot, CancellationToken token = default)
    {
        this.snapshot = snapshot;
        Room = new(snapshot, token);
    }

    public static IEnumerable<AuditAtom> Atoms(AuditExpression? expression) => expression switch
    {
        AuditAtom atom => [atom],
        AuditBoolean boolean => Atoms(boolean.Left).Concat(Atoms(boolean.Right)),
        _ => []
    };
    public AuditScenario Reconcile(AuditScenario? retained)
    {
        var result = new AuditScenario();
        if (retained is not null)
        {
            result.Start = Room.StartByKey.TryGetValue(retained.Start, out var start) && start.Valid ? retained.Start : "";
            result.Act = retained.Act is >= 1 and <= 3 ? retained.Act : 1;
            result.SoulRandomizer = retained.SoulRandomizer;
            result.AutoCollect = retained.AutoCollect;
        }
        foreach (var control in Room.Bindings.Controls)
            if (control.Difficulty)
            {
                var level = retained?.Levels?.GetValueOrDefault(control.Key) ?? 0;
                result.Levels[control.Key] = level is >= 0 and <= 4 ? level : 0;
            }
            else result.Facts[control.Key] = retained?.Facts is { } facts && facts.TryGetValue(control.Key, out var value)
                ? value : control.Key == "move:heal";
        Normalize(result); return result;
    }
    public static void SetFact(AuditScenario scenario, string key, bool value)
    {
        scenario.Facts[key] = value;
        if (key == "move:dash:run" && !value)
        { scenario.Facts["move:dash:left"] = false; scenario.Facts["move:dash:right"] = false; }
        Normalize(scenario);
    }
    private static void Normalize(AuditScenario scenario)
    {
        if (scenario.Has("move:dash:left") || scenario.Has("move:dash:right")) scenario.Facts["move:dash:run"] = true;
    }
    private Guid? Node(Guid? id) => Room.NodeId(Room.ResolveNode(id));
    public AuditCurrentAccess EvaluateAccess(AuditScenario scenario, AuditOriginScope scope, AuditRequirementsContext requirements)
    {
        var origins = ScopedStarts(scope).ToImmutableDictionary(x => x.Key, x => Evaluate(scenario, x.Key, requirements), StringComparer.Ordinal);
        return new(scope, Evaluate(scenario, "", requirements), origins);
    }
    private IEnumerable<AuditStart> ScopedStarts(AuditOriginScope scope) => Starts.Where(x => x.Valid &&
        (scope.AnyEntrance ? x.Key.StartsWith("exit:", StringComparison.Ordinal) : x.Key == scope.StartKey));

    public AuditReverseAccess EvaluateReverse(AuditScenario scenario, AuditTarget goal, AuditRequirementsContext requirements)
    {
        var statuses = new Dictionary<AuditTarget, string>();
        var validGoal = Room.TargetByKey.TryGetValue(goal, out var goalDescriptor) && goalDescriptor.Valid;
        var physicalToward = validGoal ? PhysicalNodes(Room.NodeId(goalDescriptor!.Node)!.Value, true) : new HashSet<Guid>();
        var evaluations = new Dictionary<AuditNodeHandle, (AuditEvaluation Evaluation, string GoalStatus, IReadOnlySet<AuditNodeHandle> TowardGoal)>();
        (AuditEvaluation Evaluation, string GoalStatus, IReadOnlySet<AuditNodeHandle> TowardGoal) From(AuditStart start)
        {
            var seed = Room.NodeByGuid[start.NodeId!.Value];
            if (evaluations.TryGetValue(seed, out var existing)) return existing;
            var evaluation = Evaluate(scenario, start.Key, requirements);
            var goalStatus = TargetStatus(goal, evaluation);
            var toward = new HashSet<AuditNodeHandle>();
            if (goalStatus == "Accessible")
            {
                var terminal = goalDescriptor!.Node!.Value;
                var pending = new Queue<AuditNodeHandle>(); toward.Add(terminal); pending.Enqueue(terminal);
                while (pending.TryDequeue(out var node))
                    foreach (var handle in Room.Incoming[node.Index])
                    {
                        var edge = Room.Edges[handle.Index]; var row = Room.Rows[edge.Row.Index].Source;
                        if (evaluation.Rows[(row.Kind, row.Id)].GatePassed && toward.Add(edge.From!.Value)) pending.Enqueue(edge.From.Value);
                    }
            }
            var result = (evaluation, goalStatus, (IReadOnlySet<AuditNodeHandle>)toward);
            evaluations.Add(seed, result); return result;
        }
        var candidates = snapshot.Subrooms.Select(node => new AuditTarget("subroom", node.Id))
            .Concat(snapshot.Rows.Where(row => row.Kind is "exit" or "check").Select(row => new AuditTarget(row.Kind, row.Id))).ToList();
        if (Room.Implicit) candidates.Insert(0, new("room", snapshot.RoomId));
        foreach (var target in candidates)
        {
            var start = Room.StartByKey[target.Kind == "room" ? "room" : $"{target.Kind}:{target.Id}"];
            statuses.Add(target, !start.Valid ? "Unresolved subroom assignment" : From(start).GoalStatus);
        }
        foreach (var edge in Room.Edges)
        {
            var row = Room.Rows[edge.Row.Index].Source; var target = new AuditTarget(row.Kind, row.Id);
            if (!edge.Valid) { statuses.Add(target, "Unresolved subroom assignment"); continue; }
            var candidate = From(Room.FirstStartByNode[edge.From!.Value]);
            var gateStatus = TargetStatus(target, candidate.Evaluation);
            // An exact goal connection is terminal. Other connections must themselves
            // pass AND continue from their destination under this seed's final facts.
            var status = gateStatus != "Accessible" || target == goal ? gateStatus :
                candidate.GoalStatus.StartsWith("Unresolved", StringComparison.Ordinal) ? candidate.GoalStatus :
                candidate.TowardGoal.Contains(edge.To!.Value) ? "Accessible" : "Inaccessible";
            if (!validGoal && status is "Accessible" or "Inaccessible") status = "Unresolved subroom assignment";
            statuses.Add(target, PhysicalStatus(status, target == goal || physicalToward.Contains(Room.NodeId(edge.To)!.Value)));
        }
        return new(goal, statuses.ToImmutableDictionary());
    }
    public string TargetStatus(AuditTarget target, AuditCurrentAccess access)
    {
        if (access.Scope.Neutral) return "—";
        if (access.NoEntrance) return "No entrance with a resolved subroom";
        var statuses = access.Origins.Values.Select(x => TargetStatus(target, x)).ToArray();
        if (statuses.Contains("Accessible")) return "Accessible";
        return statuses.FirstOrDefault(x => x.StartsWith("Unresolved", StringComparison.Ordinal)) ??
            (statuses.Contains("Inaccessible") ? "Inaccessible" : "Disconnected");
    }

    public AuditEvaluation Evaluate(AuditScenario scenario, string startKey, AuditRequirementsContext requirements)
    {
        var reached = new HashSet<Guid>(); var collected = new HashSet<Guid>();
        var facts = new AuditFactContext(scenario, Room.Bindings.ControlByKey, collected, requirements);
        var prerequisites = Room.Bindings.Prerequisites.Evaluate(facts);
        var nodes = new Dictionary<Guid, AuditProof>(); var order = 0;
        AuditAdmissionFacts Capture() => new(scenario.Act, scenario.SoulRandomizer,
            scenario.Facts.ToImmutableDictionary(StringComparer.Ordinal), scenario.Levels.ToImmutableDictionary(StringComparer.Ordinal), collected.ToImmutableHashSet(), requirements);
        var admission = Capture();
        var start = Room.StartByKey.TryGetValue(startKey, out var candidate) && candidate.Valid ? candidate : null;
        if (start is { NodeId: { } seed })
        { reached.Add(seed); nodes.Add(seed, new(seed, null, null, null, [], order++, admission)); }
        ImmutableArray<AuditLeafResult> Leaves(PreparedAuditRow row, IReadOnlySet<AuditOccurrenceHandle>? used = null)
        {
            var leaves = ImmutableArray.CreateBuilder<AuditLeafResult>();
            foreach (var handle in Room.Source.RowOccurrences[row.Handle.Index])
            {
                var occurrence = Room.Source.Occurrences[handle.Index];
                var bound = Room.Bindings.Applications[occurrence.Application.Index]; var root = bound.Prerequisite;
                var admitted = Room.Source.Applications[occurrence.Application.Index].Admitted(requirements);
                var evidence = prerequisites.Evidence(root);
                if (!admitted) evidence = evidence with { Passed = false };
                leaves.Add(new(occurrence.Source, admitted && prerequisites.Pass(root.Root), root.Label,
                    Room.Bindings.Prerequisites[root.Root].HasUnresolved, evidence,
                    bound.ControlKey, bound.Manual, bound.LocalCheckId, used?.Contains(handle) == true)
                    { Application = occurrence.Application, Occurrence = handle });
            }
            return leaves.ToImmutable();
        }
        AuditProof Proof(PreparedAuditRow row, Guid source, Guid destination) => new(destination, source, row.Source,
            nodes[source], Leaves(row, SuccessfulOccurrences(Room.Source.RowRoots[row.Handle.Index], prerequisites).ToHashSet()), order++, admission);
        bool changed;
        do
        {
            changed = false;
            var pendingNodes = new Dictionary<Guid, AuditProof>(); var pendingChecks = new HashSet<Guid>();
            // Canonical row scan is deliberate: Off admits nodes immediately;
            // On admits every pending node/check together after this stable round.
            foreach (var row in Room.Rows)
            {
                if (Room.NodeId(row.From) is not { } source || !reached.Contains(source) ||
                    !Pass(Room.Source.RowRoots[row.Handle.Index], prerequisites)) continue;
                if (row.Source.Kind == "connection" && row.AssignmentIssue is null && Room.NodeId(row.To) is { } destination && !reached.Contains(destination))
                {
                    if (scenario.AutoCollect)
                    { if (!pendingNodes.ContainsKey(destination)) pendingNodes.Add(destination, Proof(row, source, destination)); }
                    else { var proof = Proof(row, source, destination); reached.Add(destination); nodes.Add(destination, proof); changed = true; }
                }
                if (scenario.AutoCollect && row.Source.Kind == "check" && !collected.Contains(row.Source.Id)) pendingChecks.Add(row.Source.Id);
            }
            foreach (var (id, proof) in pendingNodes) { reached.Add(id); nodes.Add(id, proof); changed = true; }
            var collectionChanged = false;
            foreach (var id in pendingChecks) collectionChanged |= collected.Add(id);
            if (collectionChanged) { changed = true; prerequisites.RetireFactsEpoch(); admission = Capture(); }
        } while (changed);
        var results = ImmutableDictionary.CreateBuilder<(string Kind, Guid Id), AuditRowResult>();
        foreach (var row in Room.Rows)
        {
            var source = Room.NodeId(row.From); var valid = row.AssignmentIssue is null;
            var access = !valid ? "Unresolved subroom assignment" : start is null ? "Select origin…" :
                !reached.Contains(source!.Value) ? "Subroom Inaccessible" : "Subroom Accessible";
            var gate = Pass(Room.Source.RowRoots[row.Handle.Index], prerequisites);
            results.Add((row.Source.Kind, row.Source.Id), new(valid && reached.Contains(source!.Value) && gate, access, Leaves(row), gate));
        }
        return new(reached.ToImmutableHashSet(), results.ToImmutable(), start is not null, nodes.ToImmutableDictionary(), facts,
            start?.NodeId is { } physicalSeed ? PhysicalNodes(physicalSeed) : ImmutableHashSet<Guid>.Empty);
    }
    private bool Pass(PreparedAuditExpression expression, AuditPrerequisiteEvaluation prerequisites) => expression switch
    {
        PreparedAuditApplicationUse use => Room.Source.Applications[Room.Source.Occurrences[use.Occurrence.Index].Application.Index].Admitted(prerequisites.Requirements) &&
            prerequisites.Pass(Room.Bindings.Applications[Room.Source.Occurrences[use.Occurrence.Index].Application.Index].Prerequisite.Root),
        PreparedAuditBoolean { IsAnd: true } both => Pass(both.Left, prerequisites) && Pass(both.Right, prerequisites),
        PreparedAuditBoolean either => Pass(either.Left, prerequisites) || Pass(either.Right, prerequisites),
        PreparedAuditUnresolved => false,
        _ => throw new InvalidOperationException("Unknown prepared audit expression")
    };
    private IEnumerable<AuditOccurrenceHandle> SuccessfulOccurrences(PreparedAuditExpression expression, AuditPrerequisiteEvaluation prerequisites) => expression switch
    {
        PreparedAuditApplicationUse use when Pass(use, prerequisites) => [use.Occurrence],
        PreparedAuditBoolean { IsAnd: true } both => SuccessfulOccurrences(both.Left, prerequisites).Concat(SuccessfulOccurrences(both.Right, prerequisites)),
        PreparedAuditBoolean either => SuccessfulOccurrences(Pass(either.Left, prerequisites) ? either.Left : either.Right, prerequisites),
        _ => []
    };
    public Guid? SourceNode(AuditRow row) => Room.NodeId(Room.Row(row).From);
    public string NodeName(Guid? id) => id is { } value && Room.NodeByGuid.TryGetValue(value, out var node) ? Room.Nodes[node.Index].Name : "Unresolved subroom";
    public string RowName(AuditRow row) => Room.Row(row).Name;
    public string TargetStatus(AuditTarget target, AuditEvaluation evaluation)
    {
        if (target.Kind is "room" or "subroom") return !evaluation.HasOrigin ? "—" : evaluation.Reached.Contains(target.Id) ? "Accessible" :
            PhysicalStatus("Inaccessible", evaluation.PhysicalReached.Contains(target.Id));
        var row = Room.Rows[Room.RowByKey[(target.Kind, target.Id)].Index].Source; var result = evaluation.Rows[(row.Kind, row.Id)];
        if (result.Access == "Unresolved subroom assignment") return "Unresolved subroom assignment";
        if (!evaluation.HasOrigin) return "—";
        if (!result.GatePassed && (row.Requirement.Expression is null || result.Leaves.Any(x => x.Unresolved))) return "Unresolved condition";
        return result.Available ? "Accessible" : PhysicalStatus("Inaccessible", evaluation.PhysicalReached.Contains(SourceNode(row)!.Value));
    }
    // Shared presentation only: own-gate truth, never source reachability or preparation state.
    public static (string Symbol, string Text, string CssClass) GateStatusPresentation(AuditRow row, AuditRowResult result) => result.GatePassed
        ? ("✓", "Requirements met", "audit-legend-met")
        : row.Requirement.Expression is null || result.Leaves.Any(x => x.Unresolved)
            ? ("?", "Requirements unknown", "audit-state-unknown")
            : ("✕", "Requirements missing", "audit-legend-missing");
    public static string GateStatus(AuditRow row, AuditRowResult result)
    {
        var status = GateStatusPresentation(row, result);
        return $"{status.Symbol} {status.Text}";
    }
    public string? StartFor(AuditTarget target) => Room.StartByKey.TryGetValue(target.Kind == "room" ? "room" : $"{target.Kind}:{target.Id}", out var start) && start.Valid ? start.Key : null;
    public static string ValueKey(string kind, AuditValue value) => $"{kind}:{value.Id:D}:{value.OutputValue}";
    public string LocalActivationKey(Guid checkId) => $"local-activation:{snapshot.RoomId:D}:{checkId:D}";
    // Built-in vocabulary only. Never use this for resolved catalogue/check Names.
    public static string Friendly(string value) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('-', ' '));
}
