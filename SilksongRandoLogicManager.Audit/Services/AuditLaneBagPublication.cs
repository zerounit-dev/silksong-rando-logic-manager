using System.Collections.Immutable;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed class AuditLaneCountQuery(AuditApplicationCounts counts, AuditLaneInputProgram input, CancellationToken token) : AuditFamilyQuery
{
    internal override AuditFiniteSchema Schema => counts.CountSchema;
    internal override AuditRelation Neutral(AuditMddArena output) => Vector(output, null);
    internal override AuditRelation Scalar(AuditMddArena output, AuditProgramNode node, bool selected) => Vector(output, input.Packets.GetValueOrDefault(node.Index));
    private AuditRelation Vector(AuditMddArena output, AuditLanePacket? packet)
    {
        var amounts = packet?.Tokens.ToDictionary(token => token.Variant, token => token.Count);
        var result = output.True;
        for (var field = Schema.Count - 1; field >= 0; field--)
        {
            token.ThrowIfCancellationRequested(); var value = amounts?.GetValueOrDefault(counts.Features[field].Variant) ?? 0;
            var children = Enumerable.Repeat(output.False, Schema.Domains[field]).ToArray(); children[value] = result;
            result = output.Mk(field, children.ToImmutableArray());
        }
        return result;
    }
    internal override AuditFieldMerge Merge(AuditProgramKind boundary, int field, int state, int left, int right) =>
        new(left + right < Schema.Domains[field], left + right, 0);
}
internal sealed record AuditBagRectangle(AuditLaneRectangle Rectangle, AuditRelation Bags, AuditRelation SourceMembers);

// M4.4/M5's actual publication loop: publish one cofactor rectangle, then remove
// its ENTIRE bag preimage. Source witnesses are recovered by exact bag membership,
// never restricted to the arbitrarily chosen representative lane arrangement.
internal sealed class AuditLaneBagPublication
{
    private readonly AuditStaticLanes layout;
    private readonly AuditApplicationCounts counts;
    private readonly CancellationToken token;
    private readonly AuditMddArena laneArena;
    private readonly ImmutableArray<ImmutableArray<AuditRelation>> features;
    internal AuditLaneBagPublication(AuditStaticLanes layout, AuditApplicationCounts counts, AuditMddArena laneArena, CancellationToken token)
    {
        if (!ReferenceEquals(layout.Schema, laneArena.Schema) || !ReferenceEquals(layout.Program.Owner, laneArena.Owner))
            throw new InvalidOperationException("Lane publication owner/schema mismatch");
        this.layout = layout; this.counts = counts; this.laneArena = laneArena; this.token = token;
        features = counts.Features.Select(feature => LaneCounts(feature)).ToImmutableArray();
    }
    private ImmutableArray<AuditRelation> LaneCounts(AuditApplicationCountFeature feature)
    {
        var current = Enumerable.Repeat(laneArena.False, feature.Bound + 1).ToArray(); current[0] = laneArena.True;
        for (var lane = 0; lane < layout.Lanes.Length; lane++)
        {
            token.ThrowIfCancellationRequested(); var next = Enumerable.Repeat(laneArena.False, feature.Bound + 1).ToArray();
            for (var value = 0; value < layout.Schema.Domains[lane]; value++)
            {
                var amount = layout.Lanes[lane].Packets[value]?.Tokens.Where(token => token.Variant == feature.Variant).Sum(token => token.Count) ?? 0;
                var when = laneArena.EqualsValue(lane, value);
                for (var count = 0; count + amount <= feature.Bound; count++)
                    next[count + amount] = laneArena.Or(next[count + amount], laneArena.And(current[count], when));
            }
            current = next;
        }
        return current.ToImmutableArray();
    }
    internal AuditRelation Bags(AuditLaneRectangle rectangle)
    {
        var input = AuditRequirementProgram.LaneProduct(layout, rectangle, token);
        var arena = new AuditMddArena(input.Program.Owner, input.Program.Schema, token);
        var query = new AuditLaneCountQuery(counts, input, token);
        return new AuditGuardedFamily(input.Program, arena, query, token).Build(arena.True);
    }
    internal AuditRelation Members(AuditLaneRectangle rectangle, AuditRelation sourceScope) =>
        counts.Compilation.Arena.And(sourceScope, counts.Preimage(Bags(rectangle), token));
    internal IEnumerable<AuditBagRectangle> Publish(AuditRelation lanes, AuditRelation sourceScope)
    {
        if (!ReferenceEquals(lanes.Arena, laneArena) || !ReferenceEquals(sourceScope.Arena, counts.Compilation.Arena))
            throw new InvalidOperationException("Bag publication scope owner mismatch");
        var remaining = lanes;
        while (!remaining.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            var rectangle = new AuditCofactorCover(remaining, layout.Lanes.Select(lane => lane.JoinClasses).ToImmutableArray(), token).Rectangles().First();
            var bags = Bags(rectangle); var members = counts.Compilation.Arena.And(sourceScope, counts.Preimage(bags, token));
            var before = remaining;
            remaining = laneArena.Difference(remaining, AuditApplicationCounts.Preimage(bags, laneArena, features, token));
            if (remaining.Node == before.Node) throw new InvalidOperationException("Bag publication failed to remove its represented members");
            yield return new(rectangle, bags, members);
        }
    }
}
