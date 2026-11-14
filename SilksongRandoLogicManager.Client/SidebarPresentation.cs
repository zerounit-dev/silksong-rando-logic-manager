using System.Collections.Immutable;
using System.Text;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

// Pure, browser-owned views of admitted DTOs. No transport or freshness owner.
public readonly record struct SidebarGroupKey(Guid? Id);
public sealed record SidebarRow(Room Room, int ActiveIndex, string Name, string Game,
    string Text, string Label, string Href, string Css);

public sealed class SidebarGroupIndex(Group? group, int position, RoomStatus status,
    ImmutableArray<SidebarRow> rows, ImmutableArray<Guid> activeIds)
{
    public Group? Group { get; } = group;
    public SidebarGroupKey Key { get; } = new(group?.Id);
    public int Position { get; } = position;
    public RoomStatus Status { get; } = status;
    public string Name { get; } = group?.FriendlyName ?? "Ungrouped";
    public string NormalizedName { get; } = SidebarPresentation.Normalize(group?.FriendlyName ?? "Ungrouped");
    public ImmutableArray<SidebarRow> Rows { get; } = rows;
    public ImmutableArray<Guid> ActiveIds { get; } = activeIds;
}
public sealed record SidebarGroupView(SidebarGroupIndex Index,
    ImmutableArray<SidebarRow> Active, ImmutableArray<SidebarRow> Archived);
public sealed record SidebarTerms(string Name, string Location, string Requirement)
{
    public bool Searching => Name.Length > 0 || Location.Length > 0 || Requirement.Length > 0;
    public static SidebarTerms Prepare(string name, string location, string requirement) => new(
        SidebarPresentation.Normalize(name), SidebarPresentation.Normalize(location), SidebarPresentation.Normalize(requirement));
}

public sealed class SidebarPresentation
{
    public ImmutableArray<SidebarGroupIndex> Groups { get; }
    public ImmutableDictionary<SidebarGroupKey, SidebarGroupIndex> ByGroup { get; }
    public ImmutableDictionary<Guid, SidebarRow> ByRoom { get; }
    public ImmutableArray<Guid> GroupIds { get; }
    private SidebarPresentation(ImmutableArray<SidebarGroupIndex> groups,
        ImmutableDictionary<SidebarGroupKey, SidebarGroupIndex> byGroup,
        ImmutableDictionary<Guid, SidebarRow> byRoom, ImmutableArray<Guid> groupIds)
    {
        Groups = groups;
        ByGroup = byGroup;
        ByRoom = byRoom;
        GroupIds = groupIds;
    }
    public static SidebarPresentation Prepare(Sidebar sidebar, SidebarPresentation? previous)
        => PreparationSteps(sidebar, previous).Last()!;

    // Null is an internal preparation checkpoint, never a published presentation.
    internal static IEnumerable<SidebarPresentation?> PreparationSteps(Sidebar sidebar, SidebarPresentation? previous)
    {
        var work = 0;
        var buckets = new Dictionary<SidebarGroupKey, List<Room>> { [new(null)] = [] };
        foreach (var group in sidebar.Groups)
        {
            buckets.Add(new(group.Id), []);
            if (++work % 128 == 0) yield return null;
        }
        foreach (var room in sidebar.Rooms)
        {
            buckets[new(room.GroupId)].Add(room);
            if (++work % 128 == 0) yield return null;
        }
        var groups = ImmutableArray.CreateBuilder<SidebarGroupIndex>();
        foreach (var checkpoint in Add(null, -1)) yield return null;
        for (var i = 0; i < sidebar.Groups.Count; i++)
        {
            foreach (var checkpoint in Add(sidebar.Groups[i], i)) yield return null;
            if (++work % 128 == 0) yield return null;
        }
        var prepared = groups.ToImmutable();
        // Scalar/order comparison above and below, never a timestamp/checksum.
        // The new wire snapshot is still published by App; equal derived inputs
        // need neither a new presentation identity nor another filter pass.
        if (previous is not null && previous.Groups.SequenceEqual(prepared)) { yield return previous; yield break; }
        var byGroup = ImmutableDictionary.CreateBuilder<SidebarGroupKey, SidebarGroupIndex>();
        var byRoom = ImmutableDictionary.CreateBuilder<Guid, SidebarRow>();
        var groupIds = ImmutableArray.CreateBuilder<Guid>();
        foreach (var group in prepared)
        {
            byGroup.Add(group.Key, group);
            if (group.Group is { } actual) groupIds.Add(actual.Id);
            if (++work % 128 == 0) yield return null;
            foreach (var row in group.Rows)
            {
                byRoom.Add(row.Room.Id, row);
                if (++work % 128 == 0) yield return null;
            }
        }
        yield return new(prepared, byGroup.ToImmutable(), byRoom.ToImmutable(), groupIds.ToImmutable());

        IEnumerable<bool> Add(Group? group, int position)
        {
            var key = new SidebarGroupKey(group?.Id);
            SidebarGroupIndex? oldGroup = null;
            previous?.ByGroup.TryGetValue(key, out oldGroup);
            var rows = ImmutableArray.CreateBuilder<SidebarRow>();
            var ids = ImmutableArray.CreateBuilder<Guid>();
            var danger = false; var warning = false; var allSuccess = true;
            foreach (var room in buckets[key])
            {
                if (++work % 128 == 0) yield return true;
                var index = room.IsArchived ? -1 : ids.Count;
                if (!room.IsArchived)
                {
                    ids.Add(room.Id);
                    danger |= room.Status == RoomStatus.Danger;
                    warning |= room.Status == RoomStatus.Warning;
                    allSuccess &= room.Status == RoomStatus.Success;
                }
                if (previous is not null && previous.ByRoom.TryGetValue(room.Id, out var old)
                    && SameRoom(old.Room, room) && old.ActiveIndex == index
                    && oldGroup?.Group?.FriendlyName == group?.FriendlyName)
                {
                    rows.Add(old);
                    continue;
                }
                var text = group is not null && !string.IsNullOrWhiteSpace(group.FriendlyName)
                    && !string.Equals(room.FriendlyName, group.FriendlyName, StringComparison.OrdinalIgnoreCase)
                    && room.FriendlyName.StartsWith(group.FriendlyName, StringComparison.OrdinalIgnoreCase)
                    ? room.FriendlyName[group.FriendlyName.Length..].TrimStart() : room.FriendlyName;
                var label = App.ImageReason(room.ImageState) is { } reason ? $"{room.FriendlyName}: {reason}" : room.FriendlyName;
                var row = new SidebarRow(room, index, Normalize(room.FriendlyName), Normalize(room.InGameId ?? ""),
                    text, label, $"/rooms/{room.Id}", "room-link " + (room.IsArchived ? "archived-room-link" : StatusClass(room.Status)));
                rows.Add(row);
            }
            var status = danger ? RoomStatus.Danger : warning ? RoomStatus.Warning
                : ids.Count > 0 && allSuccess ? RoomStatus.Success : RoomStatus.Neutral;
            var preparedRows = rows.ToImmutable();
            var preparedIds = ids.ToImmutable();
            var next = oldGroup is not null && oldGroup.Group == group
                && oldGroup.Position == position && oldGroup.Status == status
                && oldGroup.Rows.SequenceEqual(preparedRows) && oldGroup.ActiveIds.SequenceEqual(preparedIds)
                ? oldGroup : new SidebarGroupIndex(group, position, status, preparedRows, preparedIds);
            groups.Add(next);
        }
    }
    private static bool SameRoom(Room left, Room right) => left.Id == right.Id && left.GroupId == right.GroupId
        && left.FriendlyName == right.FriendlyName && left.InGameId == right.InGameId && left.SortOrder == right.SortOrder
        && left.IsArchived == right.IsArchived && left.Status == right.Status && left.ImageState == right.ImageState;
    public ImmutableArray<SidebarGroupView> Filter(SidebarSearchIndex? search, SidebarTerms terms,
        bool archive, ImmutableArray<SidebarGroupView> previous)
    {
        var (name, location, requirement) = terms;
        var advanced = location.Length > 0 || requirement.Length > 0;
        var old = previous.ToDictionary(x => x.Index.Key);
        var result = ImmutableArray.CreateBuilder<SidebarGroupView>();
        foreach (var group in Groups)
        {
            var groupMatch = Contains(group.NormalizedName, name);
            var active = ImmutableArray.CreateBuilder<SidebarRow>();
            var archived = ImmutableArray.CreateBuilder<SidebarRow>();
            foreach (var row in group.Rows)
            {
                if (row.Room.IsArchived && !archive) continue;
                if (!(group.Group is not null && groupMatch || Contains(row.Name, name) || Contains(row.Game, name))) continue;
                if (location.Length > 0 && search?.MatchesLocation(row.Room.Id, location) != true) continue;
                if (requirement.Length > 0 && search?.MatchesRequirement(row.Room.Id, requirement) != true) continue;
                (row.Room.IsArchived ? archived : active).Add(row);
            }
            // U1: the null pseudo-group is exceptional, including in unfiltered mode.
            if (active.Count + archived.Count == 0 && (group.Group is null || advanced || !groupMatch)) continue;
            var view = new SidebarGroupView(group, active.ToImmutable(), archived.ToImmutable());
            if (old.TryGetValue(group.Key, out var prior) && ReferenceEquals(prior.Index, group)
                && prior.Active.SequenceEqual(view.Active) && prior.Archived.SequenceEqual(view.Archived)) view = prior;
            result.Add(view);
        }
        return result.ToImmutable();
    }
    public static bool Contains(string source, string term) => source.Contains(term, StringComparison.OrdinalIgnoreCase);
    public static string StatusClass(RoomStatus status) => "sidebar-status-" + status.ToString().ToLowerInvariant();
    public static string Normalize(string value)
    {
        var result = new StringBuilder(); var space = false;
        foreach (var c in value) { if (char.IsWhiteSpace(c)) { space = result.Length > 0; continue; } if (space) result.Append(' '); space = false; result.Append(c); }
        return result.ToString();
    }
}
public sealed class SidebarSearchIndex
{
    private readonly ImmutableDictionary<Guid, ImmutableArray<string>> locations, requirements;
    private SidebarSearchIndex(ImmutableDictionary<Guid, ImmutableArray<string>> locations,
        ImmutableDictionary<Guid, ImmutableArray<string>> requirements)
    {
        this.locations = locations;
        this.requirements = requirements;
    }
    public static SidebarSearchIndex Prepare(ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> priorRooms, SidebarSearchIndex? previous, IReadOnlyCollection<Guid> changed)
        => PreparationSteps(rooms, priorRooms, previous, changed).Last()!;

    internal static async Task<SidebarSearchIndex> PrepareAsync(ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> priorRooms, SidebarSearchIndex? previous,
        IReadOnlyCollection<Guid> changed, Func<Task> yield)
    {
        SidebarSearchIndex? result = previous;
        foreach (var step in PreparationSteps(rooms, priorRooms, previous, changed))
        {
            if (step is null) await yield();
            else result = step;
        }
        return result!;
    }

    // Null marks real work checkpoints; the final result does not request a yield.
    private static IEnumerable<SidebarSearchIndex?> PreparationSteps(ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> priorRooms, SidebarSearchIndex? previous, IReadOnlyCollection<Guid> changed)
    {
        var locations = previous?.locations ?? ImmutableDictionary<Guid, ImmutableArray<string>>.Empty;
        var requirements = previous?.requirements ?? ImmutableDictionary<Guid, ImmutableArray<string>>.Empty;
        var work = 0;
        bool Checkpoint() => ++work % 128 == 0;
        foreach (var id in changed)
        {
            if (++work % 128 == 0) yield return null;
            if (rooms.ContainsKey(id)) continue;
            locations = locations.Remove(id);
            requirements = requirements.Remove(id);
        }
        foreach (var id in changed)
        {
            if (++work % 128 == 0) yield return null;
            if (!rooms.TryGetValue(id, out var room)) continue;
            priorRooms.TryGetValue(id, out var old);
            // Membership/reference inspection is global; child text inspection is not.
            if (previous is not null && ReferenceEquals(old, room)) continue;
            // Each immutable family is its own consumed-input proof, even when
            // metadata or the durable Room observation changed the wrapper.
            if (previous is null || old is null || old.Checks != room.Checks)
            {
                foreach (var step in UpdateSteps(locations, id, LocationText(room),
                    previous is not null && old is not null ? LocationText(old) : null, Checkpoint))
                {
                    locations = step.Index;
                    if (step.Checkpoint) yield return null;
                }
            }
            if (previous is null || old is null || old.Requirements != room.Requirements)
            {
                foreach (var step in UpdateSteps(requirements, id, RequirementText(room),
                    previous is not null && old is not null ? RequirementText(old) : null, Checkpoint))
                {
                    requirements = step.Index;
                    if (step.Checkpoint) yield return null;
                }
            }
        }
        yield return previous is not null && ReferenceEquals(locations, previous.locations)
            && ReferenceEquals(requirements, previous.requirements) ? previous : new(locations, requirements);
    }
    private static IEnumerable<(ImmutableDictionary<Guid, ImmutableArray<string>> Index, bool Checkpoint)> UpdateSteps(
        ImmutableDictionary<Guid, ImmutableArray<string>> index, Guid id,
        IEnumerable<string> authored, IEnumerable<string>? priorAuthored, Func<bool> checkpoint)
    {
        // Independent eligible authored sequences ignore unrelated row/header state.
        if (priorAuthored is not null)
        {
            using var left = authored.GetEnumerator(); using var right = priorAuthored.GetEnumerator();
            var same = true;
            while (true)
            {
                var hasLeft = left.MoveNext(); var hasRight = right.MoveNext();
                if (!hasLeft || !hasRight) { same = hasLeft == hasRight; break; }
                if (!StringComparer.Ordinal.Equals(left.Current, right.Current)) { same = false; break; }
                if (checkpoint()) yield return (index, true);
            }
            if (same) { yield return (index, false); yield break; }
        }
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var value in authored)
        {
            builder.Add(SidebarPresentation.Normalize(value));
            if (checkpoint()) yield return (index, true);
        }
        var values = builder.ToImmutable();
        var old = index.GetValueOrDefault(id, ImmutableArray<string>.Empty);
        // Ordered normalized equality preserves blanks, duplicates and case.
        var equal = values.Length == old.Length;
        for (var i = 0; equal && i < values.Length; i++)
        {
            equal = StringComparer.Ordinal.Equals(values[i], old[i]);
            if (checkpoint()) yield return (index, true);
        }
        yield return (equal ? index : values.IsEmpty ? index.Remove(id) : index.SetItem(id, values), false);
    }
    private static IEnumerable<string> LocationText(ConfirmedRoomFacts room) =>
        room.Checks.Where(x => !x.Observation.IsArchived).Select(x => x.Values.FriendlyName);
    private static IEnumerable<string> RequirementText(ConfirmedRoomFacts room) => room.Requirements;
    public bool MatchesLocation(Guid room, string term) => locations.TryGetValue(room, out var values) && values.Any(x => SidebarPresentation.Contains(x, term));
    public bool MatchesRequirement(Guid room, string term) => requirements.TryGetValue(room, out var values) && values.Any(x => SidebarPresentation.Contains(x, term));
}
