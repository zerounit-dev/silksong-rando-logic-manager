using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Bounded original feature evidence, not a database observation or row mirror.
/// Prepared after completion/freeze; survives the existing fallible postcommit projection.</summary>
internal sealed record SharedRoomCommitPlan(
    ImmutableDictionary<Guid, LogicFinalEffects.RoomChange> Rooms,
    ImmutableDictionary<Guid, LogicFinalEffects.GroupChange> Groups,
    ImmutableArray<Wire.RoomMembershipOrder> MembershipOrders,
    ImmutableArray<Guid>? GroupOrder,
    SharedPublicationCoordinator.PreparedPublication Publication)
{
    internal ImmutableArray<Guid> AffectedRoomIds => Rooms.Keys.Order().ToImmutableArray();
    internal ImmutableArray<Wire.RoomDocumentObservation> OriginalRooms => Rooms.Values
        .Where(x => x.Original is not null).Select(x => x.Original!.Observation).OrderBy(x => x.RoomId).ToImmutableArray();
    internal ImmutableArray<RoomDocumentAdvance> Advances => Rooms.Values
        .Where(x => x.Original is not null && x.Final is not null)
        .Select(x => new RoomDocumentAdvance(x.Final!.Id, x.Original!.CacheFlagUtc, x.Final.CacheFlagUtc))
        .OrderBy(x => x.RoomId).ToImmutableArray();

    internal static async Task<SharedRoomCommitPlan> PrepareAsync(LogicDbContext db, LogicWriteScope transaction,
        SharedPublicationCoordinator.PreparedPublication publication, CancellationToken token,
        IReadOnlyCollection<Guid>? suppliedRooms = null, Guid? observedGroup = null)
    {
        var effects = transaction.FinalEffects;
        var rooms = effects.Rooms.ToBuilder();
        // Capture completeness proves each uncaptured supplied identity unchanged.
        // Read only those identities, including expected absence, in the frozen unit.
        var supplied = suppliedRooms?.Where(x => !rooms.ContainsKey(x)).Distinct().ToArray() ?? [];
        var unchanged = await ReadRoomsAsync(db, supplied, token);
        foreach (var id in supplied)
        {
            var fact = unchanged.GetValueOrDefault(id);
            rooms.Add(id, new(fact, fact));
        }
        var groups = effects.Groups.ToBuilder();
        var dependencies = rooms.Values.Where(x => x.Final?.GroupId is not null)
            .Select(x => x.Final!.GroupId!.Value).ToHashSet();
        if (observedGroup is { } observed) dependencies.Add(observed);
        var absent = dependencies.Where(id => !groups.ContainsKey(id)).ToArray();
        var unchangedGroups = await ReadGroupsAsync(db, absent, token);
        foreach (var id in absent)
            if (unchangedGroups.TryGetValue(id, out var fact)) groups.Add(id, new(fact, fact));
        foreach (var room in rooms.Values)
            if (room.Final?.GroupId is { } group && (!groups.TryGetValue(group, out var dependency) || dependency.Final is null))
                throw new InvalidOperationException("Final room publication lacks its group dependency.");
        var orders = ImmutableArray.CreateBuilder<Wire.RoomMembershipOrder>();
        foreach (var collection in effects.MembershipOrderCollections.OrderBy(x => x))
            orders.Add(new(collection, await ReadMembershipAsync(db, collection, token)));
        var groupOrder = effects.GroupOrderChanged ? await ReadGroupOrderAsync(db, token) : (ImmutableArray<Guid>?)null;
        return new(rooms.ToImmutable(), groups.ToImmutable(), orders.ToImmutable(), groupOrder, publication);
    }

    internal async Task VerifyAsync(LogicDbContext db, CancellationToken token)
    {
        var rooms = await ReadRoomsAsync(db, Rooms.Keys.ToArray(), token);
        foreach (var (id, expected) in Rooms)
            if (rooms.GetValueOrDefault(id) != expected.Final) throw Changed();
        var groups = await ReadGroupsAsync(db, Groups.Keys.ToArray(), token);
        foreach (var (id, expected) in Groups)
            if (groups.GetValueOrDefault(id) != expected.Final) throw Changed();
        foreach (var order in MembershipOrders)
            if (!order.RoomIds.SequenceEqual(await ReadMembershipAsync(db, order.GroupId, token))) throw Changed();
        if (GroupOrder is { } ordering && !ordering.SequenceEqual(await ReadGroupOrderAsync(db, token))) throw Changed();
    }

    private static InvalidOperationException Changed() => new("The committed publication footprint changed; read-only recovery is required.");

    private static async Task<Dictionary<Guid, LogicFinalEffects.RoomFact>> ReadRoomsAsync(LogicDbContext db, Guid[] ids, CancellationToken token)
    {
        if (ids.Length == 0) return [];
        var rows = await db.Rooms.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new LogicFinalEffects.RoomFact(x.Id, x.CacheFlagUtc, x.RoomGroupId, x.FriendlyName, x.SortOrder)).ToArrayAsync(token);
        return rows.Select(x => x with { CacheFlagUtc = DateTime.SpecifyKind(x.CacheFlagUtc, DateTimeKind.Utc) }).ToDictionary(x => x.Id);
    }
    private static async Task<Dictionary<Guid, SharedPublicationCoordinator.GroupWriteFact>> ReadGroupsAsync(LogicDbContext db, Guid[] ids, CancellationToken token)
    {
        if (ids.Length == 0) return [];
        var rows = await db.RoomGroups.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new SharedPublicationCoordinator.GroupWriteFact(x.Id, x.FriendlyName, x.SortOrder,
                x.ZoneReferenceText, x.ResolvedMapZoneId, x.IsVirtual, x.CreatedUtc, x.UpdatedUtc)).ToArrayAsync(token);
        return rows.Select(x => x with { CreatedUtc = DateTime.SpecifyKind(x.CreatedUtc, DateTimeKind.Utc),
            UpdatedUtc = DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc) }).ToDictionary(x => x.Id);
    }
    private static async Task<ImmutableArray<Guid>> ReadMembershipAsync(LogicDbContext db, Guid? groupId, CancellationToken token) =>
        (await db.Rooms.AsNoTracking().Where(x => x.RoomGroupId == groupId).OrderBy(x => x.SortOrder)
            .ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).Select(x => x.Id).ToArrayAsync(token)).ToImmutableArray();
    private static async Task<ImmutableArray<Guid>> ReadGroupOrderAsync(LogicDbContext db, CancellationToken token) =>
        (await db.RoomGroups.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName)
            .ThenBy(x => x.Id).Select(x => x.Id).ToArrayAsync(token)).ToImmutableArray();
}
