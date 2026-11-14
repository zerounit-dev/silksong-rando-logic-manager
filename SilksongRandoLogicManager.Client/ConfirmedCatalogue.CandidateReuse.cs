using System.Collections.Immutable;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

public sealed partial class ConfirmedCatalogue
{
    // Identity ownership for the TWO globally projected families only. These are
    // validation facts, not full authoring rows or a resolver/matching index.
    private sealed record LookupIdentities(ImmutableDictionary<Guid, Guid> Transitions, ImmutableDictionary<Guid, Guid> Checks)
    {
        internal static readonly LookupIdentities Empty = new(ImmutableDictionary<Guid, Guid>.Empty, ImmutableDictionary<Guid, Guid>.Empty);
    }
    private LookupIdentities lookupIdentities = LookupIdentities.Empty;
    private static LookupIdentities PrepareLookupIdentities(LookupIdentities prior,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> before, ImmutableDictionary<Guid, ConfirmedRoomFacts> after,
        IReadOnlyCollection<Guid> changed) => LookupIdentitySteps(prior, before, after, changed).Last()!;

    private static async Task<LookupIdentities> PrepareLookupIdentitiesAsync(LookupIdentities prior,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> before, ImmutableDictionary<Guid, ConfirmedRoomFacts> after,
        IReadOnlyCollection<Guid> changed, Func<Task> yield)
    {
        var result = prior;
        foreach (var step in LookupIdentitySteps(prior, before, after, changed))
        {
            if (step is null) await yield();
            else result = step;
        }
        return result;
    }

    // One validation algorithm: selected own results drain it synchronously; global
    // background preparation yields at bounded real Room/child work checkpoints.
    // Null emissions are checkpoints, never partially prepared published results.
    private static IEnumerable<LookupIdentities?> LookupIdentitySteps(LookupIdentities prior,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> before, ImmutableDictionary<Guid, ConfirmedRoomFacts> after,
        IReadOnlyCollection<Guid> changed)
    {
        var transitions = prior.Transitions; var checks = prior.Checks;
        var work = 0;
        foreach (var id in changed)
        {
            if (++work % 128 == 0) yield return null;
            if (!before.TryGetValue(id, out var room)) continue;
            if (!after.TryGetValue(id, out var next) || room.Transitions != next.Transitions)
                foreach (var row in room.Transitions)
                {
                    transitions = transitions.Remove(row.Observation.EntityId);
                    if (++work % 128 == 0) yield return null;
                }
            if (next is null || room.Checks != next.Checks)
                foreach (var row in room.Checks)
                {
                    checks = checks.Remove(row.Observation.EntityId);
                    if (++work % 128 == 0) yield return null;
                }
        }
        foreach (var id in changed)
        {
            if (++work % 128 == 0) yield return null;
            if (!after.TryGetValue(id, out var room)) continue;
            if (!RoomEditingIdentity.ValidObservation(room.Observation, id) || room.Header.Observation.EntityId != id
                || room.Transitions.IsDefault || room.Checks.IsDefault || room.Requirements.IsDefault)
                throw new System.Text.Json.JsonException("Unusable global-purpose Room identity.");
            before.TryGetValue(id, out var old);
            if (old is null || room.Transitions != old.Transitions)
                foreach (var row in room.Transitions)
                {
                    transitions = Add(transitions, row.Observation.EntityId, id);
                    if (++work % 128 == 0) yield return null;
                }
            if (old is null || room.Checks != old.Checks)
                foreach (var row in room.Checks)
                {
                    checks = Add(checks, row.Observation.EntityId, id);
                    if (++work % 128 == 0) yield return null;
                }
        }
        yield return ReferenceEquals(transitions, prior.Transitions) && ReferenceEquals(checks, prior.Checks) ? prior : new(transitions, checks);
        static ImmutableDictionary<Guid, Guid> Add(ImmutableDictionary<Guid, Guid> owners, Guid id, Guid owner)
        {
            if (id == Guid.Empty || owners.ContainsKey(id)) throw new System.Text.Json.JsonException("Duplicate global-purpose child identity.");
            return owners.Add(id, owner);
        }
    }

    private static ImmutableDictionary<Guid, ImageState> PrepareImages(Candidate prior,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms, IReadOnlyCollection<Guid> changed)
        => ImageSteps(prior, rooms, changed).Last()!;

    private static async Task<ImmutableDictionary<Guid, ImageState>> PrepareImagesAsync(Candidate prior,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms, IReadOnlyCollection<Guid> changed, Func<Task> yield)
    {
        var result = prior.Images;
        foreach (var step in ImageSteps(prior, rooms, changed))
        {
            if (step is null) await yield();
            else result = step;
        }
        return result;
    }

    private static IEnumerable<ImmutableDictionary<Guid, ImageState>?> ImageSteps(Candidate prior,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms, IReadOnlyCollection<Guid> changed)
    {
        var images = prior.Images;
        var work = 0;
        foreach (var id in changed)
        {
            if (++work % 128 == 0) yield return null;
            if (!rooms.ContainsKey(id)) { images = images.Remove(id); continue; }
            if (!rooms.TryGetValue(id, out var room)) continue;
            if (prior.Rooms.TryGetValue(id, out var old) && ReferenceEquals(old, room)) continue;
            var state = ImageStateFor(room, room.Header.Metadata.ImageExists);
            if (!images.TryGetValue(id, out var before) || before != state) images = images.SetItem(id, state);
        }
        yield return images;
    }

    private static (Sidebar Sidebar, SidebarPresentation Presentation) PrepareSidebar(Candidate prior,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms, ImmutableArray<CatalogueGroup> groups,
        ImmutableArray<Guid> order, ImmutableDictionary<Guid, RoomStatus> statuses,
        ImmutableDictionary<Guid, ImageState> images)
        => SidebarSteps(prior, rooms, groups, order, statuses, images).Last()!.Value;

    private static async Task<(Sidebar Sidebar, SidebarPresentation Presentation)> PrepareSidebarAsync(Candidate prior,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms, ImmutableArray<CatalogueGroup> groups,
        ImmutableArray<Guid> order, ImmutableDictionary<Guid, RoomStatus> statuses,
        ImmutableDictionary<Guid, ImageState> images, Func<Task> yield)
    {
        foreach (var step in SidebarSteps(prior, rooms, groups, order, statuses, images))
        {
            if (step is { } result) return result;
            await yield();
        }
        throw new InvalidOperationException("Incomplete sidebar preparation.");
    }

    private static IEnumerable<(Sidebar Sidebar, SidebarPresentation Presentation)?> SidebarSteps(Candidate prior,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms, ImmutableArray<CatalogueGroup> groups,
        ImmutableArray<Guid> order, ImmutableDictionary<Guid, RoomStatus> statuses,
        ImmutableDictionary<Guid, ImageState> images)
    {
        var previous = prior.Initialized ? prior.Presentation : null;
        IReadOnlyList<Group>? nextGroups = null;
        foreach (var step in ProjectionSteps(groups, prior.Sidebar.Groups, group =>
        {
            var old = previous?.ByGroup.GetValueOrDefault(new(group.Id))?.Group;
            return old is not null && old.Id == group.Id && old.FriendlyName == group.FriendlyName && old.SortOrder == group.SortOrder
                ? old : new Group(group.Id, group.FriendlyName, group.SortOrder);
        }))
        {
            if (step is null) yield return null;
            else nextGroups = step;
        }
        IReadOnlyList<Room>? nextRooms = null;
        foreach (var step in ProjectionSteps(order, prior.Sidebar.Rooms, id =>
        {
            var room = rooms[id]; var header = room.Header;
            var old = previous?.ByRoom.GetValueOrDefault(id)?.Room;
            return old is not null && old.Id == id && old.GroupId == header.RoomGroupId
                && old.FriendlyName == header.Values.FriendlyName && old.InGameId == header.Values.InGameId
                && old.SortOrder == header.SortOrder && old.IsArchived == header.Observation.IsArchived
                && old.Status == statuses[id] && old.ImageState == images[id]
                ? old : new Room(id, header.RoomGroupId, header.Values.FriendlyName, header.Values.InGameId,
                    header.SortOrder, header.Observation.IsArchived, statuses[id], images[id]);
        }))
        {
            if (step is null) yield return null;
            else nextRooms = step;
        }
        if (previous is not null && ReferenceEquals(nextGroups, prior.Sidebar.Groups) && ReferenceEquals(nextRooms, prior.Sidebar.Rooms))
        { yield return (prior.Sidebar, previous); yield break; }
        var sidebar = new Sidebar(nextGroups!, nextRooms!);
        foreach (var step in SidebarPresentation.PreparationSteps(sidebar, previous))
            yield return step is null ? null : (sidebar, step);
    }

    // Allocate an array only at the first actual projected-value/order difference.
    // The projector compares scalars before allocating any replacement record.
    private static IEnumerable<IReadOnlyList<TView>?> ProjectionSteps<TInput, TView>(ImmutableArray<TInput> inputs,
        IReadOnlyList<TView> prior, Func<TInput, TView> project) where TView : class
    {
        ImmutableArray<TView>.Builder? next = inputs.Length == prior.Count ? null : ImmutableArray.CreateBuilder<TView>(inputs.Length);
        for (var i = 0; i < inputs.Length; i++)
        {
            var row = project(inputs[i]);
            // Presentation may already retain an older equal record than the
            // scalar Sidebar. Preserve the scalar array too when values match.
            var same = i < prior.Count && EqualityComparer<TView>.Default.Equals(row, prior[i]);
            if (same) row = prior[i];
            if (next is null && !same)
            {
                next = ImmutableArray.CreateBuilder<TView>(inputs.Length);
                for (var j = 0; j < i; j++)
                {
                    next.Add(prior[j]);
                    if ((j + 1) % 128 == 0) yield return null;
                }
            }
            next?.Add(row);
            if ((i + 1) % 128 == 0) yield return null;
        }
        yield return next is null ? prior : next.ToImmutable();
    }
}
