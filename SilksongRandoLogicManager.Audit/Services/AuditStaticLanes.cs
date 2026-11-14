using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal readonly record struct AuditLaneUse(AuditOccurrenceHandle Occurrence, int Refinement);
internal sealed record AuditPacketToken(AuditApplicationVariant Variant, int Count);
internal sealed record AuditLanePacket(ImmutableArray<AuditPacketToken> Tokens, ImmutableArray<AuditPresentationComponent> Components,
    AuditDirectionSummary Summary, int SourceOrder);
internal sealed record AuditStaticLane(ImmutableArray<AuditLanePacket?> Packets, ImmutableArray<int> JoinClasses,
    ImmutableDictionary<(int Left, int Right), int> Merge,
    ImmutableDictionary<AuditLaneUse, int> Contributions, int SourceOrder);

// M4's factor alignment is a static view, not a replacement normalization tree.
// Original Program nodes still drive every compatibility/family boundary.
internal sealed class AuditStaticLanes
{
    internal ImmutableArray<AuditStaticLane> Lanes { get; }
    internal AuditFiniteSchema Schema { get; }
    internal AuditRequirementProgram Program { get; }
    private readonly CancellationToken token;
    private readonly Dictionary<AuditLaneUse, int> sourceOrder = [];
    private sealed class Lane
    {
        internal readonly List<AuditLanePacket?> Packets = [null];
        internal readonly Dictionary<string, int> PacketKeys = new(StringComparer.Ordinal);
        internal readonly Dictionary<(int, int), int> Merge = [];
        internal readonly Dictionary<AuditLaneUse, int> Uses = [];
        internal string Factor = "";
        internal string Base = "";
        internal int Order = int.MaxValue;
        internal bool Mandatory;
    }
    internal AuditStaticLanes(AuditRequirementProgram program, CancellationToken token)
    {
        Program = program; this.token = token;
        foreach (var node in program.Nodes.Where(node => node.Kind == AuditProgramKind.Application))
        {
            var occurrence = program.Room.Source.Occurrences[node.Occurrence!.Value.Index];
            foreach (var variant in program.Room.Bindings.Applications[occurrence.Application.Index].Presentation.Variants)
                sourceOrder.Add(new(occurrence.Handle, variant.Refinement), sourceOrder.Count);
        }
        var lanes = Build(program.Root).Run().OrderBy(lane => lane.Order).ToArray();
        Lanes = lanes.Select(Freeze).ToImmutableArray(); Schema = new(Lanes.Select(lane => lane.Packets.Length).ToImmutableArray());
    }
    private static string PacketKey(ImmutableArray<AuditPacketToken> tokens) => string.Join(";", tokens.OrderBy(token => token.Variant.Application.Index)
        .ThenBy(token => token.Variant.Refinement).Select(token => $"{token.Variant.Application.Index}:{token.Variant.Refinement}:{token.Count}"));
    private int Add(Lane lane, AuditLanePacket packet)
    {
        packet = packet with { Tokens = packet.Tokens.GroupBy(token => token.Variant).Select(group => new AuditPacketToken(group.Key, group.Sum(token => token.Count))).ToImmutableArray() };
        var key = PacketKey(packet.Tokens);
        if (lane.PacketKeys.TryGetValue(key, out var existing))
        {
            var previous = lane.Packets[existing]!;
            // Retain all honest typed header references even when different
            // source definitions share the same saved name and mechanics.
            var components = previous.Components.Select((component, index) => component with
            { Heading = component.Heading.Concat(packet.Components[index].Heading).Distinct().ToImmutableArray() }).ToImmutableArray();
            lane.Packets[existing] = previous with { Components = components, SourceOrder = Math.Min(previous.SourceOrder, packet.SourceOrder) };
            return existing;
        }
        var result = lane.Packets.Count; lane.Packets.Add(packet); lane.PacketKeys.Add(key, result); return result;
    }
    private static string Text(string value) => value.Length + ":" + value;
    private string Base(AuditBoundApplication application, AuditVisibleVariant variant)
    {
        var prepared = Program.Room.Source.Applications[application.Application.Index]; var meaning = prepared.Definition.Semantic;
        // Names only certify that a mechanically eligible common heading is
        // honest. Tokens/application equality NEVER use this alignment key.
        return string.Join("/", variant.Components.Select(component =>
        {
            var item = component.Heading.Length > 0 && component.Heading.All(name => name.Role == AuditNameRole.Item);
            var action = item ? "item:" + application.Prerequisite.ControlKey : meaning.Kind + ":" +
                (meaning.Kind == AuditBindingKind.Needle ? "needle" : meaning.Key);
            var q = component.Qualifiers;
            return Text(action) + ":" + component.Form + ":" + q.Rank + ":" + q.Direction + ":" + q.Quantity + ":" + component.Joinable + ":" +
                string.Join("", component.Heading.Select(name => Text(name.Role + ":" + name.Name)));
        }));
    }
    private AuditEvaluationFrame<List<Lane>> Build(int index) => new(frame => BuildSteps(index, frame));
    private IEnumerable<AuditEvaluationFrame> BuildSteps(int index, AuditEvaluationFrame<List<Lane>> frame)
    {
        token.ThrowIfCancellationRequested(); var node = Program.Nodes[index];
        if (node.Kind == AuditProgramKind.Application)
        {
            var occurrence = Program.Room.Source.Occurrences[node.Occurrence!.Value.Index];
            var application = Program.Room.Bindings.Applications[occurrence.Application.Index];
            if (!Program.Room.Source.Applications[occurrence.Application.Index].Admitted(Program.Requirements)) { frame.Result = []; yield break; }
            if (application.Presentation.Disposition != AuditPresentationDisposition.Body) { frame.Result = []; yield break; }
            var lane = new Lane { Factor = "A" + occurrence.Application.Index.ToString("D10"), Mandatory = true };
            foreach (var variant in application.Presentation.Variants)
            {
                var use = new AuditLaneUse(occurrence.Handle, variant.Refinement); var order = sourceOrder[use];
                lane.Base = Base(application, variant); lane.Order = Math.Min(lane.Order, order);
                lane.Uses.Add(use, Add(lane, new([new(new(occurrence.Application, variant.Refinement), 1)], variant.Components, variant.DirectionSummary, order)));
            }
            frame.Result = [lane]; yield break;
        }
        if (node.Kind == AuditProgramKind.AuthoredAnd)
        {
            var children = new List<Lane>();
            foreach (var child in Flatten(index, AuditProgramKind.AuthoredAnd))
            { var work = Build(child); yield return work; children.AddRange(work.Result); }
            FoldDirections(children); frame.Result = children; yield break;
        }
        if (node.Kind == AuditProgramKind.Route)
        {
            var children = new List<Lane>();
            foreach (var child in node.Children) { var work = Build(child); yield return work; children.AddRange(work.Result); }
            frame.Result = children; yield break;
        }
        if (node.Kind != AuditProgramKind.Alternative) { frame.Result = []; yield break; }
        var branches = new List<List<Lane>>();
        foreach (var child in Flatten(index, AuditProgramKind.Alternative))
        { var work = Build(child); yield return work; branches.Add(work.Result); }
        var result = new List<Lane>();
        foreach (var baseKey in branches.SelectMany(branch => branch).Select(lane => lane.Base).Distinct(StringComparer.Ordinal))
        {
            var ordered = branches.Select(branch => branch.Where(lane => lane.Base == baseKey).OrderBy(lane => lane.Factor, StringComparer.Ordinal).ThenBy(lane => lane.Order).ToArray()).ToArray();
            var maximum = ordered.Max(branch => branch.Length);
            for (var position = 0; position < maximum; position++)
            {
                var aligned = new Lane { Base = baseKey, Mandatory = false };
                var factors = new List<string>();
                foreach (var branch in ordered)
                {
                    if (position >= branch.Length) continue;
                    var lane = branch[position]; factors.Add(lane.Factor); aligned.Order = Math.Min(aligned.Order, lane.Order);
                    var map = new int[lane.Packets.Count];
                    for (var value = 1; value < map.Length; value++) map[value] = Add(aligned, lane.Packets[value]!);
                    foreach (var (use, value) in lane.Uses) aligned.Uses.Add(use, map[value]);
                    foreach (var (pair, value) in lane.Merge) aligned.Merge[(map[pair.Item1], map[pair.Item2])] = map[value];
                }
                aligned.Factor = "O(" + string.Join(",", factors.Order(StringComparer.Ordinal)) + ")"; result.Add(aligned);
            }
        }
        frame.Result = result;
    }
    private IEnumerable<int> Flatten(int index, AuditProgramKind kind)
    {
        // One cursor per active ancestor, not a recursively nested MoveNext chain.
        var stack = new Stack<(int Node, int Child)>(); stack.Push((index, -1));
        while (stack.TryPop(out var cursor))
        {
            var node = Program.Nodes[cursor.Node];
            if (node.Kind != kind) { yield return cursor.Node; continue; }
            var child = cursor.Child + 1;
            if (child >= node.Children.Length) continue;
            stack.Push((cursor.Node, child)); stack.Push((node.Children[child], -1));
        }
    }
    private static AuditDirection SingleDirection(Lane lane)
    {
        if (!lane.Mandatory || lane.Packets.Count != 2) return AuditDirection.Omitted;
        var packet = lane.Packets[1]!;
        if (packet.Components.Length != 1) return AuditDirection.Omitted;
        var component = packet.Components[0];
        return (component.Form is AuditPresentationForm.LeftRight or AuditPresentationForm.Directions) && component.Parts.Length == 1 &&
            (component.Parts[0].Direction is AuditDirection.Left or AuditDirection.Right or AuditDirection.Up) ? component.Parts[0].Direction : AuditDirection.Omitted;
    }
    private void FoldDirections(List<Lane> lanes)
    {
        foreach (var group in lanes.Where(lane => SingleDirection(lane) != AuditDirection.Omitted).GroupBy(lane => lane.Base).ToArray())
        {
            var remaining = group.OrderBy(lane => lane.Order).ToList();
            while (remaining.Count > 0)
            {
                var selected = new List<Lane>();
                foreach (var direction in new[] { AuditDirection.Left, AuditDirection.Right, AuditDirection.Up })
                    if (remaining.FirstOrDefault(lane => SingleDirection(lane) == direction) is { } lane) { selected.Add(lane); remaining.Remove(lane); }
                if (selected.Count <= 1) continue;
                var combined = new Lane { Base = group.Key, Mandatory = true, Order = selected.Min(lane => lane.Order),
                    Factor = "A(" + string.Join(",", selected.Select(lane => lane.Factor).Order(StringComparer.Ordinal)) + ")" };
                // At most three exposed fixed directions: these local subsets
                // declare the finite mandatory merge table, NOT source bags.
                var subsets = new int[1 << selected.Count];
                for (var mask = 1; mask < subsets.Length; mask++)
                {
                    token.ThrowIfCancellationRequested(); var packets = selected.Where((_, bit) => (mask & (1 << bit)) != 0).Select(lane => lane.Packets[1]!).ToArray();
                    var first = packets[0].Components[0]; var parts = packets.SelectMany(packet => packet.Components[0].Parts).ToImmutableArray();
                    var names = packets.SelectMany(packet => packet.Components[0].Heading).Distinct().ToImmutableArray();
                    var summary = parts.Length == 1 ? AuditDirectionSummary.Single : first.Form == AuditPresentationForm.LeftRight && parts.Length == 2 ? AuditDirectionSummary.Both :
                        first.Form == AuditPresentationForm.Directions && parts.Length == 3 ? AuditDirectionSummary.All : AuditDirectionSummary.Conjunction;
                    subsets[mask] = Add(combined, new(packets.SelectMany(packet => packet.Tokens).ToImmutableArray(), [first with { Parts = parts, Heading = names }], summary, packets.Min(packet => packet.SourceOrder)));
                }
                for (var left = 1; left < subsets.Length; left++)
                    for (var right = 1; right < subsets.Length; right++)
                        if ((left & right) == 0) combined.Merge[(subsets[left], subsets[right])] = subsets[left | right];
                for (var bit = 0; bit < selected.Count; bit++)
                {
                    foreach (var (use, _) in selected[bit].Uses) combined.Uses.Add(use, subsets[1 << bit]);
                    lanes.Remove(selected[bit]);
                }
                lanes.Add(combined);
            }
        }
    }
    private AuditStaticLane Freeze(Lane lane)
    {
        var classes = new int[lane.Packets.Count]; var next = 1;
        for (var value = 1; value < lane.Packets.Count; value++)
        {
            var packet = lane.Packets[value]!;
            var join = packet.Components.Length == 1 && packet.Components[0].Joinable && packet.Components[0].Parts.Length == 1;
            classes[value] = join ? 1 : ++next;
        }
        return new(lane.Packets.ToImmutableArray(), classes.ToImmutableArray(), lane.Merge.ToImmutableDictionary(), lane.Uses.ToImmutableDictionary(), lane.Order);
    }
}

internal sealed class AuditLaneFamilyQuery : AuditFamilyQuery
{
    private readonly AuditStaticLanes layout;
    private readonly CancellationToken token;
    private readonly ImmutableDictionary<int, ImmutableArray<(int Lane, int Value)>> emissions;
    private readonly Dictionary<AuditMddArena, AuditFamilyProduct> products = new(ReferenceEqualityComparer.Instance);
    internal override AuditFiniteSchema Schema => layout.Schema;
    internal AuditLaneFamilyQuery(AuditStaticLanes layout, CancellationToken token)
    {
        this.layout = layout; this.token = token;
        var at = new Dictionary<int, List<(int, int)>>();
        for (var lane = 0; lane < layout.Lanes.Length; lane++)
            foreach (var (use, value) in layout.Lanes[lane].Contributions)
            {
                var occurrence = layout.Program.Room.Source.Occurrences[use.Occurrence.Index];
                var application = layout.Program.Room.Bindings.Applications[occurrence.Application.Index];
                var variant = application.Presentation.Variants.Single(variant => variant.Refinement == use.Refinement);
                var node = layout.Program.ApplicationNodes[use.Occurrence];
                if (variant.Selections.Length == 1)
                {
                    var selection = variant.Selections[0];
                    node = layout.Program.Nodes[layout.Program.BoundNode(use.Occurrence, selection.ChoicePath)].Children[selection.Child];
                }
                if (!at.TryGetValue(node, out var contributions)) { contributions = []; at.Add(node, contributions); }
                contributions.Add((lane, value));
            }
        emissions = at.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray());
    }
    private AuditRelation Vector(AuditMddArena output, ImmutableArray<(int Lane, int Value)> emitted)
    {
        var values = new int[Schema.Count]; foreach (var contribution in emitted) values[contribution.Lane] = contribution.Value;
        var result = output.True;
        for (var field = Schema.Count - 1; field >= 0; field--)
        {
            token.ThrowIfCancellationRequested(); var children = Enumerable.Repeat(output.False, Schema.Domains[field]).ToArray(); children[values[field]] = result;
            result = output.Mk(field, children.ToImmutableArray());
        }
        return result;
    }
    internal override AuditRelation Neutral(AuditMddArena output) => Vector(output, []);
    internal override AuditRelation Scalar(AuditMddArena output, AuditProgramNode node, bool selected) => Neutral(output);
    internal override AuditFieldMerge Merge(AuditProgramKind boundary, int field, int state, int left, int right) =>
        left == 0 ? new(true, right, 0) : right == 0 ? new(true, left, 0) :
        layout.Lanes[field].Merge.TryGetValue((left, right), out var merged) ? new(true, merged, 0) : new(false, 0, 0);
    internal override AuditRelation Finish(AuditMddArena output, AuditProgramNode node, AuditRelation family, bool selected)
    {
        if (!emissions.TryGetValue(node.Index, out var values)) return family;
        if (!products.TryGetValue(output, out var product)) { product = new(output, this, token); products.Add(output, product); }
        return product.Apply(AuditProgramKind.BoundAll, family, Vector(output, values));
    }
}
