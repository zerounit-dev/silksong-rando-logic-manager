using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class PreparedAuditRequirementView
{
    internal AuditRoomOwner RoomOwner { get; }
    public AuditRequirementsContext Requirements { get; }
    internal AuditExplanationOwner OperationOwner { get; }
    internal ImmutableArray<AuditPreparedRequirementGroup> Groups { get; }
    internal ImmutableArray<AuditAnalysisIssue> Unresolved { get; }
    internal ImmutableArray<AuditAnalysisIssue> Impassable { get; }
    internal bool Empty { get; }
    internal ImmutableArray<string> Notices { get; } = [];
    internal bool HasPayload => Groups.Any(group => group.Header.Length > 0 || group.Entries.Any(entry => entry.Cells.Length > 0));
    internal PreparedAuditRequirementView(AuditRequirementProgram program, bool empty, ImmutableArray<AuditPreparedRequirementGroup> groups)
    {
        RoomOwner = program.Room.Owner; Requirements = program.Requirements; OperationOwner = program.Owner; Empty = empty; Groups = groups;
        Unresolved = program.Issues.Where(issue => issue.Unresolved).Select(issue => new AuditAnalysisIssue(program.Room.Rows[issue.Row.Index].Source, issue.Reason)).Distinct().ToImmutableArray();
        Impassable = program.Issues.Where(issue => !issue.Unresolved).Select(issue => new AuditAnalysisIssue(program.Room.Rows[issue.Row.Index].Source, issue.Reason)).Distinct().ToImmutableArray();
    }
}
internal sealed record AuditPreparedRequirementGroup(ImmutableArray<AuditCondition> Header, bool HasLocal, ImmutableArray<AuditPreparedEntry> Entries);
internal sealed record AuditPreparedEntry(ImmutableArray<AuditPreparedCell> Cells, AuditPreparedWitness Witness, AuditPreparedWitness BodyWitness, bool Free);
internal sealed record AuditPreparedCell(ImmutableArray<AuditNameRef> Names, string Header, AuditPresentationForm Form,
    AuditDirectionSummary Summary, ImmutableArray<AuditPreparedOption> Options, AuditPreparedWitness Witness);
internal sealed record AuditPreparedOption(ImmutableArray<AuditPacketToken> Tokens, ImmutableArray<string> Identities, ImmutableArray<AuditPreparedPart> Parts, AuditPreparedWitness Witness);
internal sealed record AuditPreparedPart(ImmutableArray<AuditNameRef> Names, string Caption, string FullName,
    bool Header, AuditCondition? Direct, AuditPreparedWitness Witness);

internal sealed class AuditRequirementPublication
{
    private readonly AuditRequirementCompilation compilation;
    private readonly AuditRequirementProgram program;
    private readonly AuditApplicationCounts counts;
    private readonly AuditStaticLanes layout;
    private readonly ImmutableDictionary<AuditConditionKey, AuditConditionMetadata> metadata;
    private readonly CancellationToken token;
    private readonly AuditRelation publication;
    internal AuditRequirementPublication(AuditRequirementProgram program, CancellationToken token)
    {
        this.program = program; this.token = token; compilation = new(program, token);
        metadata = AuditConditionMetadata.Collect(program, token); counts = new(compilation, token); layout = new(program, token);
        publication = AuditOriginalDownProof.PublicationScope(compilation, token);
    }
    internal PreparedAuditRequirementView Prepare()
    {
        var groups = ImmutableArray.CreateBuilder<AuditPreparedRequirementGroup>();
        foreach (var (header, scope, allMembers) in Headers())
        {
            token.ThrowIfCancellationRequested();
            var query = new AuditLaneFamilyQuery(layout, token); var builder = new AuditGuardedFamily(program, compilation.Arena, query, token);
            var lanes = builder.Build(scope); var bags = new AuditLaneBagPublication(layout, counts, builder.Output, token);
            var entries = ImmutableArray.CreateBuilder<AuditPreparedEntry>();
            foreach (var rectangle in bags.Publish(lanes, scope))
            {
                var cells = ImmutableArray.CreateBuilder<AuditPreparedCell>();
                for (var lane = 0; lane < layout.Lanes.Length; lane++)
                {
                    var values = rectangle.Rectangle.Values[lane]; if (values.Length == 1 && values[0] == 0) continue;
                    var first = layout.Lanes[lane].Packets[values[0]]!;
                    for (var component = 0; component < first.Components.Length; component++)
                        cells.Add(Cell(bags, rectangle, lane, component, scope));
                }
                var complete = AuditPreparedWitness.Complete(compilation, rectangle.SourceMembers, metadata, token);
                var body = AuditPreparedWitness.Full(compilation, rectangle.SourceMembers, metadata, token, bodyOnly: true);
                entries.Add(new(cells.ToImmutable(), complete, body, header.Length == 0 && cells.Count == 0));
            }
            var local = metadata.Where(pair => pair.Value.LocalCheckId is not null).Any(pair =>
                compilation.Complete.Cases.TryGetValue(pair.Key, out var cases) && cases.Values.Any(when => !compilation.Arena.And(allMembers, when).IsEmpty));
            groups.Add(new(header, local, entries.ToImmutable()));
        }
        token.ThrowIfCancellationRequested();
        return new(program, compilation.Complete.Admitted.IsEmpty, groups.ToImmutable());
    }
    private IEnumerable<(ImmutableArray<AuditCondition> Header, AuditRelation Scope, AuditRelation AllMembers)> Headers()
    {
        var keys = metadata.Where(pair => AuditFixedHeaders.Includes(pair.Key, pair.Value)).Select(pair => pair.Key).ToHashSet();
        var query = new AuditFullRequirementQuery(program, keys); var builder = new AuditGuardedFamily(program, compilation.Arena, query, token);
        var family = builder.Build(publication); var values = new int[query.Keys.Length];
        var headers = new List<(ImmutableArray<AuditCondition>, AuditRelation, ImmutableArray<int>, AuditRelation)>();
        void Visit(int field, AuditRelation remaining)
        {
            token.ThrowIfCancellationRequested(); if (remaining.IsEmpty) return;
            if (field == values.Length)
            {
                var header = ImmutableArray.CreateBuilder<AuditCondition>(); var scope = compilation.Complete.Admitted;
                for (var index = 0; index < values.Length; index++)
                {
                    var key = query.Keys[index]; var required = AuditFullRequirementQuery.Decode(key, values[index]);
                    var cases = compilation.Complete.Cases.GetValueOrDefault(key, ImmutableDictionary<int, AuditRelation>.Empty);
                    var present = cases.Values.Aggregate(compilation.Arena.False, compilation.Arena.Or);
                    scope = compilation.Arena.And(scope, required is { } value ? cases.GetValueOrDefault(value, compilation.Arena.False) : compilation.Arena.Difference(compilation.Complete.Admitted, present));
                    if (required is { } actual) header.Add(metadata[key].Display(new(key, actual)));
                }
                headers.Add((header.OrderBy(HeaderIndex).ToImmutableArray(), compilation.Arena.And(scope, publication), HeaderSort(query.Keys, values), scope)); return;
            }
            for (var value = 0; value < query.Schema.Domains[field]; value++)
            { values[field] = value; Visit(field + 1, builder.Output.Cofactor(remaining, field, value)); }
        }
        Visit(0, family);
        headers.Sort((a, b) =>
        {
            if (a.Item1.Length == 0 || b.Item1.Length == 0) return a.Item1.Length.CompareTo(b.Item1.Length);
            for (var index = 0; index < a.Item3.Length; index++) { var compared = a.Item3[index].CompareTo(b.Item3[index]); if (compared != 0) return compared; }
            return 0;
        });
        foreach (var header in headers) yield return (header.Item1, header.Item2, header.Item4);
    }
    private static int HeaderIndex(AuditCondition condition) => condition.Kind == "soul-mode" ? 0 : condition.Kind == "act" ? 7 :
        AuditFixedHeaders.Settings.IndexOf(condition.Key["setting:".Length..]) + 1;
    private static ImmutableArray<int> HeaderSort(ImmutableArray<AuditConditionKey> keys, int[] values)
    {
        var result = new int[9];
        for (var index = 0; index < keys.Length; index++)
        {
            var key = keys[index]; var value = values[index];
            if (key.Kind == AuditConditionKind.SoulMode) result[0] = value;
            else if (key.Kind == AuditConditionKind.Fact) result[AuditFixedHeaders.Settings.IndexOf(key.Key["setting:".Length..]) + 1] = value;
            else if (value > 0) { result[7] = Enumerable.Range(0, 3).First(bit => (value & (1 << bit)) != 0) + 1; result[8] = value; }
        }
        return result.ToImmutableArray();
    }
    private AuditPreparedCell Cell(AuditLaneBagPublication bags, AuditBagRectangle rectangle, int lane, int componentIndex, AuditRelation scope)
    {
        var options = ImmutableArray.CreateBuilder<AuditPreparedOption>(); var names = ImmutableArray.CreateBuilder<AuditNameRef>();
        var packets = rectangle.Rectangle.Values[lane].Select(value => layout.Lanes[lane].Packets[value]!).ToArray();
        var first = packets[0].Components[componentIndex];
        foreach (var value in rectangle.Rectangle.Values[lane])
        {
            token.ThrowIfCancellationRequested(); var packet = layout.Lanes[lane].Packets[value]!; var component = packet.Components[componentIndex];
            names.AddRange(component.Heading);
            var optionRectangle = rectangle.Rectangle with { Values = rectangle.Rectangle.Values.SetItem(lane, [value]) };
            var members = bags.Members(optionRectangle, scope);
            var requested = packet.Tokens.ToDictionary(token => token.Variant, token => token.Count);
            var cellPaths = packet.Components.Length > 1 ? Paths(packet, component.Parts) : null;
            var witness = AuditPreparedWitness.Scoped(compilation, counts, members, requested, metadata, token, cellPaths);
            var parts = ImmutableArray.CreateBuilder<AuditPreparedPart>();
            foreach (var part in component.Parts)
            {
                var paths = Paths(packet, [part]);
                var own = AuditPreparedWitness.Scoped(compilation, counts, members, requested, metadata, token, paths);
                var caption = part.Direction != AuditDirection.Omitted ? part.Direction.ToString() : string.Join(" / ", part.Names.Select(Name).Distinct(StringComparer.Ordinal));
                var heading = Heading(component); var headerPart = part.Direction == AuditDirection.Omitted && part.Names.All(name => component.Heading.Contains(name));
                var fullName = headerPart ? heading : heading + " " + caption;
                var source = packet.Tokens.Select(token => program.Room.Source.Applications[token.Variant.Application.Index]).First();
                if (source.Definition.Semantic.Kind == AuditBindingKind.Manual) fullName = part.Own.Label;
                if (source.Definition.Semantic.Kind == AuditBindingKind.Dependency) fullName = part.Own.Label;
                AuditCondition? direct = part.Represents is { } condition && !(condition.Key.Kind == AuditConditionKind.Level && condition.Value == 0) &&
                    !AuditFixedHeaders.Includes(condition.Key, metadata[condition.Key]) ? metadata[condition.Key].Display(condition) : null;
                parts.Add(new(part.Names, caption, fullName, headerPart, direct, own));
            }
            var identities = packet.Tokens.Select(token =>
            {
                var bound = program.Room.Bindings.Applications[token.Variant.Application.Index];
                var variant = bound.Presentation.Variants.Single(variant => variant.Refinement == token.Variant.Refinement);
                return bound.Identity + (variant.SelectedDirection == AuditDirection.Omitted ? "" : " — " + variant.SelectedDirection) + (token.Count == 1 ? "" : " × " + token.Count);
            }).ToImmutableArray();
            options.Add(new(packet.Tokens, identities, parts.ToImmutable(), witness));
        }
        var summary = options.Count == 1 ? packets[0].Summary : first.Form == AuditPresentationForm.LeftRight && packets.All(packet => packet.Summary == AuditDirectionSummary.Single) &&
            packets.SelectMany(packet => packet.Components[componentIndex].Parts).Select(part => part.Direction).ToHashSet().SetEquals([AuditDirection.Left, AuditDirection.Right]) ? AuditDirectionSummary.Either :
            first.Form == AuditPresentationForm.Directions && packets.SelectMany(packet => packet.Components[componentIndex].Parts).Select(part => part.Direction).ToHashSet().SetEquals([AuditDirection.Left, AuditDirection.Right, AuditDirection.Up]) ? AuditDirectionSummary.Any : AuditDirectionSummary.Alternatives;
        return new(names.Distinct().ToImmutableArray(), Heading(first), first.Form, summary, options.ToImmutable(), AuditPreparedWitness.Union(options.Select(option => option.Witness), token));
    }
    private Dictionary<AuditOccurrenceHandle, ImmutableArray<int>> Paths(AuditLanePacket packet, ImmutableArray<AuditPartPlan> parts)
    {
        var result = new Dictionary<AuditOccurrenceHandle, ImmutableArray<int>>();
        foreach (var token in packet.Tokens)
        {
            var variant = program.Room.Bindings.Applications[token.Variant.Application.Index].Presentation.Variants.Single(variant => variant.Refinement == token.Variant.Refinement);
            var selected = variant.Components.SelectMany(component => component.Parts).Where(part => parts.Any(selectedPart => ReferenceEquals(part, selectedPart))).ToArray();
            if (selected.Length == 0) continue;
            // A whole component with several directional parts uses the complete
            // application root; an individual direction uses its declared path.
            var path = selected.Length == 1 ? selected[0].OwnPath : ImmutableArray<int>.Empty;
            foreach (var active in counts.Activations.Where(active => active.Variant == token.Variant)) result[active.Occurrence] = path;
        }
        return result;
    }
    private static string Name(AuditNameRef name) => name.Role == AuditNameRole.Check && name.RoomName is { } room ? room + " — " + name.Name : name.Name;
    private static string Heading(AuditPresentationComponent component)
    {
        var heading = string.Join(" / ", component.Heading.Select(Name).Distinct(StringComparer.Ordinal));
        // Structural Soul chrome has no catalogue NameRef; retain honest managed headings.
        if (component.Form == AuditPresentationForm.Soul && heading.Length == 0) heading = "Soul";
        if (component.Qualifiers.Rank is { } rank) heading += " ≥ " + RoomAuditEvaluator.DifficultyNames[rank];
        if (component.Qualifiers.Direction != AuditDirection.Omitted) heading += " " + component.Qualifiers.Direction;
        return heading;
    }
}

public sealed partial class RoomAuditEvaluator
{
    public PreparedAuditRequirementView PrepareOwnRequirements(AuditRow row, AuditRequirementsContext requirements, CancellationToken token) =>
        new AuditRequirementPublication(AuditRequirementProgram.Own(Room, row, requirements, token), token).Prepare();
    internal PreparedAuditRequirementView PrepareRouteRequirements(AuditPhysicalRoute route, AuditRequirementsContext requirements, CancellationToken token) =>
        new AuditRequirementPublication(AuditRequirementProgram.SelectedRoute(Room, route, requirements, token), token).Prepare();
}
