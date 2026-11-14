using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal enum AuditProgramKind { True, False, Unresolved, Condition, AuthoredAnd, BoundAll, Route, Alternative, Application, OwnerSelector, LaneAlternative, Packet }
internal sealed record AuditProgramNode(int Index, AuditProgramKind Kind, int FirstChoice, int EndChoice,
    ImmutableArray<int> Children, int? Choice = null, AuditConstraint? Condition = null,
    AuditOccurrenceHandle? Occurrence = null, string? Reason = null);
internal sealed record AuditProgramIssue(AuditRowHandle Row, string Reason, bool Unresolved);
internal sealed record AuditSelectedOwnerProgram(AuditRequirementProgram Program, ImmutableArray<int> SourceVariables,
    FrozenDictionary<AuditOccurrenceHandle, int> Selectors);
internal sealed record AuditLaneInputProgram(AuditRequirementProgram Program, ImmutableDictionary<int, AuditLanePacket?> Packets);

// Operation-local input program. Actual OR coordinates belong to occurrences, even
// when their prerequisite arena node/application handle is shared. No source/output
// equality copies, selected-tree padding, display history or unused room gates.
internal sealed class AuditRequirementProgram
{
    internal PreparedAuditRoom Room { get; }
    internal AuditRequirementsContext Requirements { get; }
    internal AuditExplanationOwner Owner { get; }
    internal AuditFiniteSchema Schema { get; }
    internal ImmutableArray<AuditProgramNode> Nodes { get; }
    internal int Root { get; }
    internal ImmutableArray<AuditProgramIssue> Issues { get; }
    internal FrozenDictionary<AuditOccurrenceHandle, int> ApplicationNodes { get; }
    private AuditRequirementProgram(PreparedAuditRoom room, ImmutableArray<int> domains, ImmutableArray<AuditProgramNode> nodes,
        int root, ImmutableArray<AuditProgramIssue> issues, Dictionary<AuditOccurrenceHandle, int> applications,
        AuditRequirementsContext requirements, AuditExplanationOwner? owner = null, AuditFiniteSchema? schema = null)
    {
        Room = room; Requirements = requirements; Owner = owner ?? new(room.Owner); Schema = schema ?? new(domains); Nodes = nodes; Root = root; Issues = issues;
        ApplicationNodes = applications.ToFrozenDictionary();
    }
    internal static AuditLaneInputProgram LaneProduct(AuditStaticLanes layout, AuditLaneRectangle rectangle, CancellationToken token)
    {
        if (rectangle.Values.Length != layout.Lanes.Length) throw new InvalidOperationException("Lane rectangle/schema mismatch");
        var nodes = ImmutableArray.CreateBuilder<AuditProgramNode>(); var packets = ImmutableDictionary.CreateBuilder<int, AuditLanePacket?>();
        var factors = ImmutableArray.CreateBuilder<int>();
        for (var lane = 0; lane < layout.Lanes.Length; lane++)
        {
            token.ThrowIfCancellationRequested(); var children = ImmutableArray.CreateBuilder<int>();
            for (var value = 0; value < layout.Schema.Domains[lane]; value++)
            {
                var index = nodes.Count; var allowed = rectangle.Values[lane].Contains(value);
                nodes.Add(new(index, allowed ? AuditProgramKind.Packet : AuditProgramKind.False, lane + 1, lane + 1, []));
                if (allowed) packets.Add(index, layout.Lanes[lane].Packets[value]);
                children.Add(index);
            }
            var factor = nodes.Count;
            nodes.Add(new(factor, AuditProgramKind.LaneAlternative, lane, lane + 1, children.ToImmutable(), lane)); factors.Add(factor);
        }
        var root = nodes.Count; nodes.Add(new(root, AuditProgramKind.Route, 0, layout.Schema.Count, factors.ToImmutable()));
        return new(new(layout.Program.Room, layout.Schema.Domains, nodes.ToImmutable(), root, [], [], layout.Program.Requirements, layout.Program.Owner, layout.Schema), packets.ToImmutable());
    }
    internal int BoundNode(AuditOccurrenceHandle occurrence, ImmutableArray<int> path)
    {
        var node = Nodes[ApplicationNodes[occurrence]].Children[0];
        foreach (var child in path) node = Nodes[node].Children[child];
        return node;
    }
    // M6 selectors wrap ONE application interval. Both Boolean values consume
    // that same interval; no copied application/inactive-sibling construction.
    internal AuditSelectedOwnerProgram WithOwnerSelectors(IReadOnlySet<AuditOccurrenceHandle> candidates, CancellationToken token)
    {
        if (Nodes.Any(node => node.Kind == AuditProgramKind.OwnerSelector)) throw new InvalidOperationException("Owner selectors are query-local, not nestable");
        if (candidates.Any(occurrence => !ApplicationNodes.ContainsKey(occurrence))) throw new InvalidOperationException("Selected owner outside requirement scope");
        var nodes = ImmutableArray.CreateBuilder<AuditProgramNode>(); var domains = ImmutableArray.CreateBuilder<int>();
        var variables = new int[Schema.Count]; var selectors = new Dictionary<AuditOccurrenceHandle, int>();
        var applications = new Dictionary<AuditOccurrenceHandle, int>();
        AuditEvaluationFrame<int> Visit(int index) => new(frame => Steps(index, frame));
        IEnumerable<AuditEvaluationFrame> Steps(int index, AuditEvaluationFrame<int> frame)
        {
            token.ThrowIfCancellationRequested(); var source = Nodes[index]; var first = domains.Count;
            var selector = source.Kind == AuditProgramKind.Application && candidates.Contains(source.Occurrence!.Value) ? domains.Count : (int?)null;
            if (selector is not null) { domains.Add(2); selectors.Add(source.Occurrence!.Value, selector.Value); }
            var bodyFirst = domains.Count; int? choice = null;
            if (source.Choice is { } oldChoice)
            { choice = domains.Count; variables[oldChoice] = choice.Value; domains.Add(Schema.Domains[oldChoice]); }
            var children = ImmutableArray.CreateBuilder<int>(source.Children.Length);
            foreach (var child in source.Children) { var work = Visit(child); yield return work; children.Add(work.Result); }
            var result = nodes.Count;
            nodes.Add(source with { Index = result, FirstChoice = bodyFirst, EndChoice = domains.Count, Choice = choice, Children = children.ToImmutable() });
            if (source.Kind == AuditProgramKind.Application) applications.Add(source.Occurrence!.Value, result);
            if (selector is null) { frame.Result = result; yield break; }
            var wrapper = nodes.Count;
            nodes.Add(new(wrapper, AuditProgramKind.OwnerSelector, first, domains.Count, [result], selector, Occurrence: source.Occurrence));
            frame.Result = wrapper;
        }
        var root = Visit(Root).Run(); token.ThrowIfCancellationRequested();
        return new(new(Room, domains.ToImmutable(), nodes.ToImmutable(), root, Issues, applications, Requirements, Owner),
            variables.ToImmutableArray(), selectors.ToFrozenDictionary());
    }
    internal static AuditRequirementProgram Own(PreparedAuditRoom room, AuditRow row, AuditRequirementsContext requirements, CancellationToken token)
    {
        var loaded = room.Row(row);
        if (!ReferenceEquals(loaded.Source, row)) throw new InvalidOperationException("Own audit row owner mismatch");
        var builder = new Builder(room, requirements, token); return builder.Finish(builder.Row(loaded.Handle));
    }
    internal static AuditRequirementProgram SelectedRoute(PreparedAuditRoom room, AuditPhysicalRoute route, AuditRequirementsContext requirements, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(room.Owner, route.Owner) || !room.StartByKey.TryGetValue(route.Origin.Key, out var origin) ||
            origin != route.Origin || !origin.Valid || !room.TargetByKey.TryGetValue(route.Target, out var target) || !target.Valid)
            throw new InvalidOperationException("Selected audit route owner/endpoints mismatch");
        var builder = new Builder(room, requirements, token); var rows = ImmutableArray.CreateBuilder<int>();
        var current = room.NodeByGuid[origin.NodeId!.Value]; var visited = new HashSet<AuditNodeHandle> { current };
        foreach (var step in route.Steps)
        {
            token.ThrowIfCancellationRequested();
            var row = room.Row(step);
            if (!ReferenceEquals(row.Source, step) || !room.EdgeByRowKey.TryGetValue((step.Kind, step.Id), out var handle))
                throw new InvalidOperationException("Selected audit route step mismatch");
            var edge = room.Edges[handle.Index];
            if (!edge.Valid || edge.From != current || !visited.Add(edge.To!.Value))
                throw new InvalidOperationException("Selected audit route directed sequence mismatch");
            rows.Add(builder.Row(row.Handle)); current = edge.To.Value;
        }
        if (target.Node != current || (target.OwnRow is null) != (route.OwnGate is null))
            throw new InvalidOperationException("Selected audit route terminal mismatch");
        if (target.OwnRow is { } terminal)
        {
            if (!ReferenceEquals(room.Rows[terminal.Index].Source, route.OwnGate)) throw new InvalidOperationException("Selected audit route own-gate mismatch");
            rows.Add(builder.Row(terminal));
        }
        var identity = string.Join("/", new[] { origin.Key }.Concat(route.Steps.Select(row => row.Id.ToString("D")))
            .Concat(route.OwnGate is { } own ? [own.Id.ToString("D")] : Array.Empty<string>()));
        if (route.Identity != identity) throw new InvalidOperationException("Selected audit route identity mismatch");
        return builder.Finish(builder.Product(AuditProgramKind.Route, rows.ToImmutable()));
    }

    private sealed class Builder(PreparedAuditRoom room, AuditRequirementsContext requirements, CancellationToken token)
    {
        private readonly List<AuditProgramNode> nodes = [];
        private readonly List<int> domains = [];
        private readonly List<AuditProgramIssue> issues = [];
        private readonly Dictionary<AuditOccurrenceHandle, int> applications = [];
        internal int Row(AuditRowHandle handle)
        {
            token.ThrowIfCancellationRequested();
            var row = room.Rows[handle.Index];
            return Authored(room.Source.RowRoots[handle.Index]).Run();
            AuditEvaluationFrame<int> Authored(PreparedAuditExpression expression) => new(frame => AuthoredSteps(expression, frame));
            IEnumerable<AuditEvaluationFrame> AuthoredSteps(PreparedAuditExpression expression, AuditEvaluationFrame<int> frame)
            {
                token.ThrowIfCancellationRequested();
                var first = domains.Count;
                switch (expression)
                {
                    case PreparedAuditUnresolved unresolved:
                        foreach (var reason in unresolved.Reasons) issues.Add(new(handle, reason, true));
                        frame.Result = Add(AuditProgramKind.Unresolved, first, [], reason: unresolved.Reasons.FirstOrDefault()); break;
                    case PreparedAuditBoolean { IsAnd: true } both:
                        var left = Authored(both.Left); yield return left;
                        var right = Authored(both.Right); yield return right;
                        frame.Result = Product(AuditProgramKind.AuthoredAnd, [left.Result, right.Result], first); break;
                    case PreparedAuditBoolean either:
                        var choice = domains.Count; domains.Add(3);
                        var a = Authored(either.Left); yield return a;
                        var b = Authored(either.Right); yield return b;
                        frame.Result = Add(AuditProgramKind.Alternative, first, [a.Result, b.Result], choice); break;
                    case PreparedAuditApplicationUse use:
                        var occurrence = room.Source.Occurrences[use.Occurrence.Index];
                        var bound = room.Bindings.Applications[occurrence.Application.Index];
                        var body = Bound(bound.Prerequisite.Root, use.Occurrence); yield return body;
                        var application = Add(AuditProgramKind.Application, first, [body.Result], occurrence: use.Occurrence);
                        applications.Add(use.Occurrence, application);
                        // Keep original bound paths/diagnostics and choice intervals intact.
                        // Mode False is a conjunct, never deletion or a new blocked issue.
                        frame.Result = room.Source.Applications[occurrence.Application.Index].Admitted(requirements) ? application :
                            Product(AuditProgramKind.BoundAll, [application, Add(AuditProgramKind.False, domains.Count, [])], first); break;
                    default: throw new InvalidOperationException("Unknown prepared audit source expression");
                }
            }
            AuditEvaluationFrame<int> Bound(AuditPrerequisiteHandle root, AuditOccurrenceHandle occurrence) => new(frame => BoundSteps(root, occurrence, frame));
            IEnumerable<AuditEvaluationFrame> BoundSteps(AuditPrerequisiteHandle root, AuditOccurrenceHandle occurrence, AuditEvaluationFrame<int> frame)
            {
                token.ThrowIfCancellationRequested();
                var first = domains.Count; var node = room.Bindings.Prerequisites[root];
                switch (node.Operator)
                {
                    case AuditPrerequisiteOperator.True: frame.Result = Add(AuditProgramKind.True, first, [], occurrence: occurrence); break;
                    case AuditPrerequisiteOperator.False: case AuditPrerequisiteOperator.Unresolved:
                        if (node.Reason is { } reason) issues.Add(new(handle, reason, node.Operator == AuditPrerequisiteOperator.Unresolved));
                        frame.Result = Add(node.Operator == AuditPrerequisiteOperator.False ? AuditProgramKind.False : AuditProgramKind.Unresolved,
                            first, [], occurrence: occurrence, reason: node.Reason); break;
                    case AuditPrerequisiteOperator.Condition:
                        frame.Result = Add(AuditProgramKind.Condition, first, [], condition: node.Condition, occurrence: occurrence); break;
                    case AuditPrerequisiteOperator.All:
                        var all = ImmutableArray.CreateBuilder<int>(node.Children.Length);
                        foreach (var child in node.Children) { var work = Bound(child, occurrence); yield return work; all.Add(work.Result); }
                        frame.Result = Product(AuditProgramKind.BoundAll, all.ToImmutable(), first, occurrence); break;
                    case AuditPrerequisiteOperator.Any:
                        var choice = domains.Count; domains.Add(node.Children.Length + 1);
                        var any = ImmutableArray.CreateBuilder<int>(node.Children.Length);
                        foreach (var child in node.Children) { var work = Bound(child, occurrence); yield return work; any.Add(work.Result); }
                        frame.Result = Add(AuditProgramKind.Alternative, first, any.ToImmutable(), choice, occurrence: occurrence); break;
                    default: throw new InvalidOperationException("Unknown bound audit prerequisite");
                }
            }
        }
        internal int Product(AuditProgramKind kind, ImmutableArray<int> children, int? first = null, AuditOccurrenceHandle? occurrence = null) =>
            Add(kind, first ?? (children.Length == 0 ? domains.Count : nodes[children[0]].FirstChoice), children, occurrence: occurrence);
        private int Add(AuditProgramKind kind, int first, ImmutableArray<int> children, int? choice = null,
            AuditConstraint? condition = null, AuditOccurrenceHandle? occurrence = null, string? reason = null)
        {
            token.ThrowIfCancellationRequested(); var result = nodes.Count;
            nodes.Add(new(result, kind, first, domains.Count, children, choice, condition, occurrence, reason)); return result;
        }
        internal AuditRequirementProgram Finish(int root)
        {
            token.ThrowIfCancellationRequested();
            return new(room, domains.ToImmutableArray(), nodes.ToImmutableArray(), root, issues.Distinct().ToImmutableArray(), applications, requirements);
        }
    }
}
