using System.Collections.Immutable;
using SilksongRandoLogicManager.Contracts;
using static Silksong_Rando_Logic_Manager.Services.AppliedRoomStatusCalculation;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Committed canonical completion inputs and reverse dependencies. Owned by
/// server publication admission. A transaction prepares a small old/new overlay;
/// rollback discards it, commit publishes it. Calculation still belongs exclusively
/// to AppliedRoomStatusCalculation, not this dependency selector.</summary>
public sealed class AppliedCompletionIndex
{
    public sealed record RoomFacts(StatusRoom Room, ImmutableArray<StatusSubroom> Subrooms,
        ImmutableArray<StatusTransition> Transitions, ImmutableArray<StatusConnection> Connections,
        ImmutableArray<StatusCheck> Checks);

    private readonly Dictionary<Guid, RoomFacts> owners = [];
    private readonly Dictionary<Guid, StatusTransition> transitions = [];
    private readonly Dictionary<string, HashSet<Guid>> references = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Guid>> games = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Guid>> incomingText = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<Guid>> incomingRoom = [];
    private readonly Dictionary<(Guid RoomId, string Alias), HashSet<Guid>> aliases = [];
    private readonly Dictionary<(Guid CandidateRoomId, Guid DestinationRoomId, Guid DestinationId), HashSet<Guid>> inverses = [];

    public AppliedCompletionIndex(IEnumerable<RoomFacts> rooms)
    {
        foreach (var room in rooms) Add(room);
    }

    // Use the same normalization as the calculator, never SQLite NOCASE.
    private static string Key(string value) => RoomPresentationRules.Key(value);
    private static bool Text(string? value) => RoomPresentationRules.Text(value);

    public IReadOnlyCollection<Guid> RoomIds => owners.Keys;

    /// <summary>Each replacement is a complete current owner; null is a deleted Room.
    /// Replaced includes old and new owners when a child has moved. Preparation does
    /// not mutate committed inputs and does not scan unrelated owners.</summary>
    public Pending Prepare(IReadOnlyDictionary<Guid, RoomFacts?> replacements) => new(this, replacements);

    private void Add(RoomFacts facts)
    {
        var room = facts.Room;
        owners.Add(room.Id, facts);
        if (Text(room.ReferenceId)) Add(references, Key(room.ReferenceId), room.Id);
        if (Text(room.InGameId)) Add(games, Key(room.InGameId!), room.Id);
        foreach (var row in facts.Transitions)
        {
            transitions.Add(row.Id, row);
            if (Text(row.Alias)) Add(aliases, (room.Id, Key(row.Alias)), row.Id);
            // Include archived/unresolved textual users in affected selection. Their
            // own status remains canonical Neutral/filtered when recalculated.
            if (Text(row.DestinationRoom)) Add(incomingText, Key(row.DestinationRoom!), row.Id);
            if (row.DestinationRoomId is { } destinationRoom) Add(incomingRoom, destinationRoom, row.Id);
            if (!row.Archived && row.DestinationRoomId is { } inverseRoom && row.DestinationId is { } inverseId)
                Add(inverses, (row.RoomId, inverseRoom, inverseId), row.Id);
        }
    }

    private void Remove(Guid id)
    {
        if (!owners.Remove(id, out var facts)) return;
        var room = facts.Room;
        if (Text(room.ReferenceId)) Remove(references, Key(room.ReferenceId), id);
        if (Text(room.InGameId)) Remove(games, Key(room.InGameId!), id);
        foreach (var row in facts.Transitions)
        {
            transitions.Remove(row.Id);
            if (Text(row.Alias)) Remove(aliases, (id, Key(row.Alias)), row.Id);
            if (Text(row.DestinationRoom)) Remove(incomingText, Key(row.DestinationRoom!), row.Id);
            if (row.DestinationRoomId is { } destinationRoom) Remove(incomingRoom, destinationRoom, row.Id);
            if (!row.Archived && row.DestinationRoomId is { } inverseRoom && row.DestinationId is { } inverseId)
                Remove(inverses, (row.RoomId, inverseRoom, inverseId), row.Id);
        }
    }

    private static void Add<TKey>(Dictionary<TKey, HashSet<Guid>> index, TKey key, Guid id) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var ids)) index.Add(key, ids = []);
        ids.Add(id);
    }

    private static void Remove<TKey>(Dictionary<TKey, HashSet<Guid>> index, TKey key, Guid id) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var ids)) return;
        ids.Remove(id);
        if (ids.Count == 0) index.Remove(key);
    }

    public sealed class Pending
    {
        private readonly AppliedCompletionIndex committed;
        private readonly AppliedCompletionIndex overlay;
        private readonly HashSet<Guid> replaced;
        private bool published;

        internal Pending(AppliedCompletionIndex committed, IReadOnlyDictionary<Guid, RoomFacts?> replacements)
        {
            this.committed = committed;
            replaced = replacements.Keys.ToHashSet();
            foreach (var (id, facts) in replacements)
                if (facts is not null && facts.Room.Id != id)
                    throw new ArgumentException("Completion replacement owner identity mismatch.", nameof(replacements));
            overlay = new(replacements.Values.OfType<RoomFacts>());
        }

        private RoomFacts? Owner(Guid id) => overlay.owners.GetValueOrDefault(id)
            ?? (replaced.Contains(id) ? null : committed.owners.GetValueOrDefault(id));

        private IEnumerable<Guid> RoomBucket<TKey>(TKey key,
            Dictionary<TKey, HashSet<Guid>> before, Dictionary<TKey, HashSet<Guid>> after) where TKey : notnull =>
            (before.GetValueOrDefault(key) ?? []).Where(id => !replaced.Contains(id))
                .Concat(after.GetValueOrDefault(key) ?? []);

        private IEnumerable<StatusTransition> TransitionBucket<TKey>(TKey key,
            Dictionary<TKey, HashSet<Guid>> before, Dictionary<TKey, HashSet<Guid>> after) where TKey : notnull =>
            (before.GetValueOrDefault(key) ?? []).Select(id => committed.transitions[id])
                .Where(row => !replaced.Contains(row.RoomId))
                .Concat((after.GetValueOrDefault(key) ?? []).Select(id => overlay.transitions[id]));

        /// <summary>Both old and new keys matter: removing a duplicate can repair its
        /// previous peer, and an unresolved incoming author can become valid.</summary>
        public IReadOnlyCollection<Guid> AffectedOwners()
        {
            var result = new HashSet<Guid>(replaced);
            foreach (var id in replaced)
            {
                var before = committed.owners.GetValueOrDefault(id);
                var after = overlay.owners.GetValueOrDefault(id);
                var referenceChanged = before is null || after is null
                    || before.Room.ReferenceId != after.Room.ReferenceId || before.Room.Archived != after.Room.Archived;
                var gameChanged = before is null || after is null
                    || before.Room.InGameId != after.Room.InGameId || before.Room.Archived != after.Room.Archived;
                AddRoomDependencies(before, referenceChanged, gameChanged);
                AddRoomDependencies(after, referenceChanged, gameChanged);
                if (referenceChanged)
                {
                    AddIncoming(committed.incomingRoom.GetValueOrDefault(id), committed);
                    AddIncoming(overlay.incomingRoom.GetValueOrDefault(id), overlay);
                }
                var oldRows = before?.Transitions.ToDictionary(x => x.Id) ?? [];
                var newRows = after?.Transitions.ToDictionary(x => x.Id) ?? [];
                foreach (var rowId in oldRows.Keys.Union(newRows.Keys))
                {
                    var oldRow = oldRows.GetValueOrDefault(rowId);
                    var newRow = newRows.GetValueOrDefault(rowId);
                    if (oldRow is not null && newRow is not null && SameExternalTransition(oldRow, newRow)) continue;
                    if (oldRow is null || newRow is null || oldRow.Alias != newRow.Alias || oldRow.Archived != newRow.Archived)
                    {
                        AddIncoming(committed.incomingRoom.GetValueOrDefault(id), committed);
                        AddIncoming(overlay.incomingRoom.GetValueOrDefault(id), overlay);
                    }
                    // This row can contribute to its old/new target's inverse count.
                    // Local authored names/requirements/geometry do not fan out here.
                    if (oldRow?.DestinationRoomId is { } oldTarget && oldRow.DestinationId is not null) result.Add(oldTarget);
                    if (newRow?.DestinationRoomId is { } newTarget && newRow.DestinationId is not null) result.Add(newTarget);
                }
            }
            return result;

            void AddIncoming(HashSet<Guid>? rows, AppliedCompletionIndex index)
            {
                if (rows is null) return;
                foreach (var row in rows) result.Add(index.transitions[row].RoomId);
            }

            void AddRoomDependencies(RoomFacts? facts, bool referenceChanged, bool gameChanged)
            {
                if (facts is null) return;
                if (referenceChanged && Text(facts.Room.ReferenceId))
                {
                    var key = Key(facts.Room.ReferenceId);
                    result.UnionWith(committed.references.GetValueOrDefault(key) ?? []);
                    result.UnionWith(overlay.references.GetValueOrDefault(key) ?? []);
                    AddIncoming(committed.incomingText.GetValueOrDefault(key), committed);
                    AddIncoming(overlay.incomingText.GetValueOrDefault(key), overlay);
                }
                if (gameChanged && Text(facts.Room.InGameId))
                {
                    var key = Key(facts.Room.InGameId!);
                    result.UnionWith(committed.games.GetValueOrDefault(key) ?? []);
                    result.UnionWith(overlay.games.GetValueOrDefault(key) ?? []);
                }
            }
        }

        /// <summary>Select only requested owners and their indexed lookup candidates.
        /// The canonical calculator builds its invocation-local indexes from this
        /// neighbourhood, not from a per-save world scan or a second rule engine.</summary>
        public IReadOnlyDictionary<Guid, AppliedRoomStatus> Calculate(IReadOnlyCollection<Guid> requested)
        {
            var rooms = new Dictionary<Guid, StatusRoom>();
            var subrooms = new List<StatusSubroom>();
            var rows = new Dictionary<Guid, StatusTransition>();
            var connections = new List<StatusConnection>();
            var checks = new List<StatusCheck>();
            foreach (var id in requested.Distinct())
            {
                var facts = Owner(id);
                if (facts is null) continue;
                var room = facts.Room;
                rooms[id] = room;
                if (room.Archived) continue;
                AddReferences(room.ReferenceId);
                if (Text(room.InGameId))
                    foreach (var candidate in RoomBucket(Key(room.InGameId!), committed.games, overlay.games)) AddRoom(candidate);
                subrooms.AddRange(facts.Subrooms);
                connections.AddRange(facts.Connections);
                checks.AddRange(facts.Checks);
                foreach (var row in facts.Transitions)
                {
                    rows[row.Id] = row;
                    if (row.Archived) continue;
                    AddReferences(row.DestinationRoom);
                    if (row.DestinationRoomId is not { } destination) continue;
                    if (Text(row.DestinationAlias))
                        foreach (var candidate in TransitionBucket((destination, Key(row.DestinationAlias!)), committed.aliases, overlay.aliases))
                            rows[candidate.Id] = candidate;
                    if (row.DestinationId is not null)
                        foreach (var candidate in TransitionBucket((destination, id, row.Id), committed.inverses, overlay.inverses))
                            rows[candidate.Id] = candidate;
                }
            }
            return AppliedRoomStatusCalculation.Calculate(rooms.Values.ToArray(), subrooms, rows.Values.ToArray(),
                connections, checks, requested);

            void AddRoom(Guid id)
            {
                if (Owner(id) is { } facts) rooms[id] = facts.Room;
            }
            void AddReferences(string? text)
            {
                if (!Text(text)) return;
                foreach (var id in RoomBucket(Key(text!), committed.references, overlay.references)) AddRoom(id);
            }
        }

        /// <summary>Only after the original source/status commit. The caller holds
        /// publication admission across preparation, commit and publication.</summary>
        public void PublishCommitted()
        {
            if (published) throw new InvalidOperationException("Completion inputs already published.");
            foreach (var id in replaced) committed.Remove(id);
            foreach (var facts in overlay.owners.Values) committed.Add(facts);
            published = true;
        }
    }
}
