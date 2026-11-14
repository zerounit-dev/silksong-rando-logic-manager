using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

internal sealed record LocalRoomFacts(ImmutableDictionary<Guid, ConfirmedRoomFacts> Rooms,
    ImmutableDictionary<Guid, RoomStatus> Statuses, ImmutableArray<Guid> Order, ImmutableArray<CatalogueGroup> Groups)
{
    internal ImmutableHashSet<Guid> ChangedRooms { get; init; } = [];
}

internal static class LocalCatalogueMaterializer
{
    internal static async Task<LocalRoomFacts> MergeAsync(LocalCatalogueSource read, LocalRoomFacts previous,
        IReadOnlyDictionary<Guid, bool> images, Func<Task> yield, CancellationToken token, LocalLoadWork? observation = null)
    {
        var work = 0;
        async Task Checkpoint()
        {
            token.ThrowIfCancellationRequested();
            if (++work % 128 == 0) { await yield(); token.ThrowIfCancellationRequested(); }
        }
        var changed = new List<LocalRoomLookupSource>();
        foreach (var row in read.Rooms)
        {
            observation?.Row(LogicCacheTable.Rooms);
            changed.Add(row); await Checkpoint();
        }
        var changedIds = changed.Select(x => x.Observation.RoomId).ToHashSet();
        async Task<ILookup<Guid, T>> Children<T>(ImmutableArray<T> source, Func<T, Guid> ownerId, LogicCacheTable table)
        {
            var rows = new List<(Guid Owner, T Row)>();
            foreach (var row in source)
            {
                observation?.Row(table);
                var owner = ownerId(row);
                if (!changedIds.Contains(owner)) throw new JsonException("Global-purpose row lacks its selected owner.");
                rows.Add((owner, row)); await Checkpoint();
            }
            return rows.ToLookup(x => x.Owner, x => x.Row);
        }
        var exits = await Children(read.Transitions, x => x.RoomId, LogicCacheTable.RoomTransitions);
        var paths = await Children(read.Connections, x => x.RoomId, LogicCacheTable.SubroomConnections);
        var checks = await Children(read.Checks, x => x.RoomId, LogicCacheTable.CheckLocations);
        var rooms = previous.Rooms;
        var changedRooms = ImmutableHashSet.CreateBuilder<Guid>();
        var inventory = read.Inventory.ToDictionary(x => x.Id);
        foreach (var id in rooms.Keys)
        {
            if (!inventory.ContainsKey(id)) { rooms = rooms.Remove(id); changedRooms.Add(id); }
            await Checkpoint();
        }
        foreach (var r in changed)
        {
            var id = r.Observation.RoomId;
            if (!inventory.ContainsKey(id)) throw new JsonException("Uninventoried global-purpose Room.");
            var transitions = ImmutableArray.CreateBuilder<TransitionLookupFacts>();
            var names = ImmutableArray.CreateBuilder<CheckLookupFacts>();
            var requirements = ImmutableArray.CreateBuilder<string>();
            foreach (var x in exits[id])
            {
                transitions.Add(x.Facts);
                if (!x.Facts.Observation.IsArchived) requirements.Add(x.Requirements!);
                await Checkpoint();
            }
            foreach (var x in paths[id])
            {
                if (!x.Identity.IsArchived) requirements.Add(x.Requirements!);
                await Checkpoint();
            }
            foreach (var x in checks[id])
            {
                names.Add(x.Facts);
                if (!x.Facts.Observation.IsArchived) requirements.Add(x.Requirements!);
                await Checkpoint();
            }
            var header = new RoomLookupHeader(r.Identity, r.Values,
                r.RoomGroupId, r.SortOrder, r.CompletionStatus, r.SceneImage,
                new(images.TryGetValue(id, out var known) ? known : null));
            var value = ConfirmedRoomFacts.Reuse(new(r.Observation, header,
                transitions.ToImmutable(), names.ToImmutable(), requirements.ToImmutable()), previous.Rooms.GetValueOrDefault(id));
            rooms = rooms.SetItem(id, value);
            if (!ReferenceEquals(value, previous.Rooms.GetValueOrDefault(id))) changedRooms.Add(id);
            await Checkpoint();
        }
        var statuses = previous.Statuses;
        foreach (var id in statuses.Keys) if (!inventory.ContainsKey(id)) statuses = statuses.Remove(id);
        var order = ImmutableArray.CreateBuilder<Guid>(read.Inventory.Length);
        foreach (var stamp in read.Inventory)
        {
            if (!rooms.TryGetValue(stamp.Id, out var room) || room.Observation.CacheFlagUtc.Ticks !=
                long.Parse(stamp.CacheFlagUtc, NumberStyles.None, CultureInfo.InvariantCulture)
                || room.Header.CompletionStatus != Status(stamp.CompletionStatus))
                throw new JsonException("Incomplete local global-purpose Room coverage.");
            bool? exists = images.TryGetValue(stamp.Id, out var known) ? known : null;
            if (room.Header.Metadata.ImageExists != exists)
            {
                rooms = rooms.SetItem(stamp.Id, room with { Header = room.Header with { Metadata = new(exists) } });
                changedRooms.Add(stamp.Id);
            }
            if (!statuses.TryGetValue(stamp.Id, out var old) || old != room.Header.CompletionStatus)
                statuses = statuses.SetItem(stamp.Id, room.Header.CompletionStatus);
            order.Add(stamp.Id); await Checkpoint();
        }
        var groups = ImmutableArray.CreateBuilder<CatalogueGroup>();
        foreach (var r in read.Groups)
        {
            observation?.Row(LogicCacheTable.RoomGroups);
            groups.Add(r); await Checkpoint();
        }
        var groupIds = groups.Select(x => x.Id).ToHashSet();
        foreach (var room in rooms.Values)
        {
            if (room.Header.RoomGroupId is { } group && !groupIds.Contains(group)) throw new JsonException("Missing global group dependency.");
            await Checkpoint();
        }
        var ordered = order.ToImmutable(); var grouped = groups.ToImmutable();
        return new(rooms, statuses, previous.Order.SequenceEqual(ordered) ? previous.Order : ordered,
            previous.Groups.SequenceEqual(grouped) ? previous.Groups : grouped) { ChangedRooms = changedRooms.ToImmutable() };
    }
    private static RoomStatus Status(int value) => value switch {
        0 => RoomStatus.Neutral, 1 => RoomStatus.Success, 2 => RoomStatus.Warning, 3 => RoomStatus.Danger,
        _ => throw new JsonException("Invalid stored completion.") };
}
