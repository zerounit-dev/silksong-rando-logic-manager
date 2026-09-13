using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Shared non-persisted aggregate of a room's applied validation and logic state.</summary>
public enum AppliedRoomStatus { Neutral, Success, Warning, Danger }

public sealed record AppliedRoomStatusLoadTrace(
    TimeSpan Elapsed, int SqlQueryCount, int RoomFacts, int SubroomFacts,
    int TransitionFacts, int ConnectionFacts, int CheckFacts);

public interface IAppliedRoomStatusService
{
    Task<IReadOnlyDictionary<Guid, AppliedRoomStatus>> LoadAsync(
        IEnumerable<Guid>? roomIds = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Exact uncached applied-status projection. Five set-based scalar readers supply
/// only status facts; no editor table view or EF child collection is constructed.
/// </summary>
public sealed class AppliedRoomStatusService(IDbContextFactory<LogicDbContext> contexts) : IAppliedRoomStatusService
{
    public AppliedRoomStatusLoadTrace? LastTrace { get; private set; }

    public async Task<IReadOnlyDictionary<Guid, AppliedRoomStatus>> LoadAsync(
        IEnumerable<Guid>? roomIds = null, CancellationToken cancellationToken = default)
    {
        var requested = roomIds?.Distinct().ToArray();
        if (requested is { Length: 0 }) return new Dictionary<Guid, AppliedRoomStatus>();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await LoadAsync(db, requested, cancellationToken);
    }

    internal async Task<IReadOnlyDictionary<Guid, AppliedRoomStatus>> LoadAsync(
        LogicDbContext db, IReadOnlyCollection<Guid>? requested, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var rooms = await db.Rooms.AsNoTracking()
            .Select(x => new StatusRoom(x.Id, x.FriendlyName, x.ReferenceId, x.InGameId, x.IsArchived))
            .ToListAsync(token);
        var selectedIds = requested is null ? rooms.Select(x => x.Id).ToArray() : requested.ToArray();

        var subroomQuery = db.Subrooms.AsNoTracking();
        if (requested is not null) subroomQuery = subroomQuery.Where(x => selectedIds.Contains(x.RoomId));
        var subrooms = await subroomQuery.Select(x => new StatusSubroom(
            x.Id, x.RoomId, x.FriendlyName, x.ReferenceId, x.SceneUnitX, x.SceneUnitY,
            x.SceneUnitWidth, x.SceneUnitHeight, x.IsArchived)).ToListAsync(token);

        // Destination aliases and inverse facts can live in another room, so this
        // remains one compact catalogue-wide transition fact read.
        var transitions = await db.RoomTransitions.AsNoTracking().Select(x => new StatusTransition(
            x.Id, x.RoomId, x.Alias, x.FriendlyName, x.InGameId,
            x.SourceSubroomReferenceText, x.DestinationRoomReferenceText,
            x.DestinationTransitionAliasText, x.Requirements,
            x.ResolvedSourceSubroomId, x.ResolvedDestinationRoomId,
            x.ResolvedDestinationTransitionId, x.IsTodo, x.IsVerified,
            x.IsArchived, x.RequirementsParseSucceeded)).ToListAsync(token);

        var connectionQuery = db.SubroomConnections.AsNoTracking();
        if (requested is not null) connectionQuery = connectionQuery.Where(x => selectedIds.Contains(x.RoomId));
        var connections = await connectionQuery
            .Select(x => new StatusConnection(x.Id, x.RoomId, x.Alias, x.FriendlyName,
                x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText,
                x.Requirements, x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId,
                x.IsTodo, x.IsVerified, x.IsArchived, x.RequirementsParseSucceeded))
            .ToListAsync(token);

        // Cross-room check-name information is globally relevant. The compact
        // read also supplies same-room transition/check game-ID conflicts.
        var checkQuery = db.CheckLocations.AsNoTracking().Where(x => !x.Room.IsArchived);
        if (requested is not null) checkQuery = checkQuery.Where(x => selectedIds.Contains(x.RoomId));
        var checks = await checkQuery
            .Select(x => new StatusCheck(x.Id, x.RoomId, x.FriendlyName, x.InGameId,
                x.SubroomReferenceText, x.Requirements, x.LocationType,
                x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.ResolvedSubroomId,
                x.IsTodo, x.IsVerified, x.IsArchived, x.RequirementsParseSucceeded))
            .ToListAsync(token);

        var roomById = rooms.ToDictionary(x => x.Id);
        var subroomsByRoom = subrooms.ToLookup(x => x.RoomId);
        var transitionsByRoom = transitions.ToLookup(x => x.RoomId);
        var connectionsByRoom = connections.ToLookup(x => x.RoomId);
        var checksByRoom = checks.ToLookup(x => x.RoomId);
        var indexes = new StatusIndexes(rooms, subrooms, transitions, connections, checks);

        var result = new Dictionary<Guid, AppliedRoomStatus>(selectedIds.Length);
        foreach (var roomId in selectedIds)
        {
            if (!roomById.TryGetValue(roomId, out var room)) continue;
            result[roomId] = room.Archived ? AppliedRoomStatus.Neutral : Calculate(
                room, subroomsByRoom[roomId].ToArray(),
                transitionsByRoom[roomId].ToArray(), connectionsByRoom[roomId].ToArray(),
                checksByRoom[roomId].ToArray(), indexes);
        }

        LastTrace = new(Stopwatch.GetElapsedTime(started), 5, rooms.Count, subrooms.Count,
            transitions.Count, connections.Count, checks.Count);
        return result;
    }

    private static AppliedRoomStatus Calculate(StatusRoom room, IReadOnlyList<StatusSubroom> subrooms, IReadOnlyList<StatusTransition> transitions,
        IReadOnlyList<StatusConnection> connections, IReadOnlyList<StatusCheck> checks, StatusIndexes indexes)
    {
        var worst = V2Severity.Neutral;
        void Add(V2Severity severity) => worst = MapperSupport.Max(worst, severity);

        Add(MapperSupport.Required(room.FriendlyName));
        Add(!MapperSupport.Text(room.ReferenceId) ? V2Severity.Warning : indexes.ActiveRoomReferenceCount(room.ReferenceId) > 1 ? V2Severity.Danger : V2Severity.Neutral);
        if (MapperSupport.Text(room.InGameId)) Add(indexes.ActiveRoomGameCount(room.InGameId!) > 1 ? V2Severity.Danger : V2Severity.Neutral);
        if (worst == V2Severity.Danger) return AppliedRoomStatus.Danger;

        var activeSubrooms = subrooms.Where(x => !x.Archived).ToArray();
        var usedSubroomIds = transitions.Where(x => !x.Archived).Select(x => x.SourceId)
            .Concat(connections.Where(x => !x.Archived).SelectMany(x => new[] { x.SourceId, x.DestinationId }))
            .Concat(checks.Where(x => !x.Archived).Select(x => x.SubroomId))
            .OfType<Guid>().ToHashSet();
        foreach (var row in activeSubrooms)
        {
            Add(!MapperSupport.Text(row.FriendlyName) ? V2Severity.Warning : indexes.SubroomNameCount(room.Id, row.FriendlyName) > 1 ? V2Severity.Danger : V2Severity.Neutral);
            Add(!MapperSupport.Text(row.ReferenceId) ? V2Severity.Warning : indexes.SubroomReferenceCount(room.Id, row.ReferenceId) > 1 ? V2Severity.Danger : V2Severity.Neutral);
            Add(MapperSupport.Rectangle(row.X, row.Y, row.Width, row.Height));
            if (!usedSubroomIds.Contains(row.Id)) Add(V2Severity.Warning);
            if (worst == V2Severity.Danger) return AppliedRoomStatus.Danger;
        }

        var activeRoomTransitions = transitions.Where(x => !x.Archived).ToArray();
        foreach (var row in activeRoomTransitions)
        {
            Add(!MapperSupport.Text(row.Alias) ? V2Severity.Warning : row.Alias.Length > 3 || indexes.TransitionAliasCount(room.Id, row.Alias) > 1 ? V2Severity.Danger : V2Severity.Neutral);
            Add(!MapperSupport.Text(row.FriendlyName) ? V2Severity.Warning : indexes.TransitionNameCount(room.Id, row.FriendlyName) > 1 ? V2Severity.Danger : V2Severity.Neutral);
            Add(MapperSupport.ReferenceSeverity(MapperSupport.Presence(row.Source, activeSubrooms.Length > 0), indexes.SubroomReferenceState(room.Id, row.Source, row.SourceId)));
            var roomState = indexes.RoomReferenceState(row.DestinationRoom, row.DestinationRoomId);
            Add(MapperSupport.ReferenceSeverity(MapperSupport.Required(row.DestinationRoom), roomState));
            var duplicateEndpoint = MapperSupport.Text(row.DestinationRoom) && MapperSupport.Text(row.DestinationAlias) && indexes.TransitionEndpointCount(row) > 1;
            var aliasNormal = MapperSupport.Max(MapperSupport.Alias(row.DestinationAlias, []), duplicateEndpoint ? V2Severity.Danger : V2Severity.Neutral);
            Add(MapperSupport.ReferenceSeverity(aliasNormal, indexes.DestinationAliasState(row, roomState)));
            Add(MapperSupport.Requirements(row.Requirements, row.ParseSucceeded));
            if (MapperSupport.Text(row.InGameId) && indexes.RoomGameIdCount(room.Id, row.InGameId!) > 1) Add(V2Severity.Danger);
            if (row.DestinationRoomId is not null && row.DestinationId is not null)
            {
                var inverseCount = indexes.InverseCount(row.DestinationRoomId.Value, room.Id, row.Id);
                Add(inverseCount == 0 ? V2Severity.Warning : inverseCount > 1 ? V2Severity.Danger : V2Severity.Neutral);
            }
            if (worst == V2Severity.Danger || row.Verified == false) return AppliedRoomStatus.Danger;
        }

        var activeConnections = connections.Where(x => !x.Archived).ToArray();
        foreach (var row in activeConnections)
        {
            Add(MapperSupport.Alias(row.Alias, []));
            Add(MapperSupport.Required(row.FriendlyName));
            Add(MapperSupport.ReferenceSeverity(MapperSupport.Presence(row.Source, activeSubrooms.Length > 0), indexes.SubroomReferenceState(room.Id, row.Source, row.SourceId)));
            Add(MapperSupport.ReferenceSeverity(MapperSupport.Presence(row.Destination, activeSubrooms.Length > 0), indexes.SubroomReferenceState(room.Id, row.Destination, row.DestinationId)));
            if (row.SourceId is not null && row.SourceId == row.DestinationId) Add(V2Severity.Danger);
            Add(MapperSupport.Requirements(row.Requirements, row.ParseSucceeded));
            Add(indexes.PathwaySeverity(row.Id));
            if (activeSubrooms.Length == 0) Add(V2Severity.Danger);
            if (worst == V2Severity.Danger || row.Verified == false) return AppliedRoomStatus.Danger;
        }

        foreach (var row in checks.Where(x => !x.Archived))
        {
            if (!MapperSupport.Text(row.FriendlyName)) Add(V2Severity.Warning);
            else if (indexes.CheckNameCount(room.Id, row.FriendlyName) > 1) Add(V2Severity.Danger);
            // A name match only in another room is deliberately informational.
            Add(MapperSupport.ReferenceSeverity(MapperSupport.Presence(row.Subroom, activeSubrooms.Length > 0), indexes.SubroomReferenceState(room.Id, row.Subroom, row.SubroomId)));
            Add(MapperSupport.Requirements(row.Requirements, row.ParseSucceeded));
            if (MapperSupport.Text(row.InGameId) && indexes.RoomGameIdCount(room.Id, row.InGameId!) > 1) Add(V2Severity.Danger);
            Add(MapperSupport.Pair(row.AnnotationX, row.AnnotationY));
            if (!CheckLocationTypeCatalogue.IsRecognized(row.LocationType)) Add(V2Severity.Danger);
            if (worst == V2Severity.Danger || row.Verified == false) return AppliedRoomStatus.Danger;
        }

        var logicRows = activeRoomTransitions.Select(x => (x.Todo, x.Verified))
            .Concat(activeConnections.Select(x => (x.Todo, x.Verified)))
            .Concat(checks.Where(x => !x.Archived).Select(x => (x.Todo, x.Verified))).ToArray();
        if (worst == V2Severity.Danger || logicRows.Any(x => x.Verified == false)) return AppliedRoomStatus.Danger;
        if (worst == V2Severity.Warning || logicRows.Any(x => x.Todo || x.Verified is null)) return AppliedRoomStatus.Warning;
        return logicRows.Length == 0 ? AppliedRoomStatus.Neutral : AppliedRoomStatus.Success;
    }

    private sealed class StatusIndexes
    {
        private readonly Dictionary<string, int> activeRoomReferences;
        private readonly Dictionary<string, int> activeRoomGames;
        private readonly Dictionary<string, List<StatusRoom>> roomsByReference = [];
        private readonly Dictionary<(Guid RoomId, string Key), int> subroomNames;
        private readonly Dictionary<(Guid RoomId, string Key), int> subroomReferences;
        private readonly Dictionary<(Guid RoomId, string Key), List<StatusSubroom>> subroomsByReference = [];
        private readonly Dictionary<(Guid RoomId, string Key), int> transitionAliases;
        private readonly Dictionary<(Guid RoomId, string Key), int> transitionNames;
        private readonly TransitionEndpointIndex transitionEndpoints = new();
        private readonly Dictionary<(Guid RoomId, string Alias), List<StatusTransition>> destinationAliases = [];
        private readonly Dictionary<(Guid CandidateRoomId, Guid DestinationRoomId, Guid DestinationId), int> inverses;
        private readonly Dictionary<(Guid RoomId, string Key), int> roomGameIds;
        private readonly Dictionary<(Guid RoomId, string Key), int> checkNames;
        private readonly Dictionary<Guid, V2Severity> pathwaySeverities;

        public StatusIndexes(IReadOnlyList<StatusRoom> rooms, IReadOnlyList<StatusSubroom> subrooms,
            IReadOnlyList<StatusTransition> transitions, IReadOnlyList<StatusConnection> connections,
            IReadOnlyList<StatusCheck> checks)
        {
            activeRoomReferences = []; activeRoomGames = []; subroomNames = []; subroomReferences = [];
            transitionAliases = []; transitionNames = []; inverses = []; roomGameIds = []; checkNames = [];
            foreach (var row in rooms)
            {
                if (MapperSupport.Text(row.ReferenceId)) { var key = MapperSupport.Key(row.ReferenceId); Add(roomsByReference, key, row); if (!row.Archived) Increment(activeRoomReferences, key); }
                if (!row.Archived && MapperSupport.Text(row.InGameId)) Increment(activeRoomGames, MapperSupport.Key(row.InGameId));
            }
            foreach (var row in subrooms)
            {
                if (MapperSupport.Text(row.ReferenceId)) Add(subroomsByReference, (row.RoomId, MapperSupport.Key(row.ReferenceId)), row);
                if (row.Archived) continue;
                if (MapperSupport.Text(row.FriendlyName)) Increment(subroomNames, (row.RoomId, MapperSupport.Key(row.FriendlyName)));
                if (MapperSupport.Text(row.ReferenceId)) Increment(subroomReferences, (row.RoomId, MapperSupport.Key(row.ReferenceId)));
            }
            foreach (var row in transitions)
            {
                if (MapperSupport.Text(row.Alias)) Add(destinationAliases, (row.RoomId, MapperSupport.Key(row.Alias)), row);
                if (row.Archived) continue;
                transitionEndpoints.Add(row);
                if (MapperSupport.Text(row.Alias)) Increment(transitionAliases, (row.RoomId, MapperSupport.Key(row.Alias)));
                if (MapperSupport.Text(row.FriendlyName)) Increment(transitionNames, (row.RoomId, MapperSupport.Key(row.FriendlyName)));
                if (row.DestinationRoomId is { } destinationRoom && row.DestinationId is { } destinationId) Increment(inverses, (row.RoomId, destinationRoom, destinationId));
                if (MapperSupport.Text(row.InGameId)) Increment(roomGameIds, (row.RoomId, MapperSupport.Key(row.InGameId)));
            }
            foreach (var row in checks.Where(x => !x.Archived))
            {
                if (MapperSupport.Text(row.FriendlyName)) Increment(checkNames, (row.RoomId, MapperSupport.Key(row.FriendlyName)));
                if (MapperSupport.Text(row.InGameId)) Increment(roomGameIds, (row.RoomId, MapperSupport.Key(row.InGameId)));
            }
            var activeConnections = connections.Where(x => !x.Archived).ToArray();
            var groups = new Dictionary<(Guid RoomId, string Key), List<StatusConnection>>();
            var names = new Dictionary<(Guid RoomId, string Key), List<StatusConnection>>();
            foreach (var row in activeConnections)
            {
                if (MapperSupport.Text(row.Alias)) Add(groups, (row.RoomId, MapperSupport.Key(row.Alias)), row);
                if (MapperSupport.Text(row.FriendlyName)) Add(names, (row.RoomId, MapperSupport.Key(row.FriendlyName)), row);
            }
            var badAliasGroups = groups.Where(pair => InvalidPathwayGroup(pair.Value)).Select(pair => pair.Key).ToHashSet();
            var badNameGroups = names.Where(pair => !MapperSupport.Text(pair.Value[0].Alias) || pair.Value.Any(candidate => !MapperSupport.Same(candidate.Alias, pair.Value[0].Alias))).Select(pair => pair.Key).ToHashSet();
            pathwaySeverities = activeConnections.ToDictionary(x => x.Id, x =>
                MapperSupport.Text(x.Alias) && badAliasGroups.Contains((x.RoomId, MapperSupport.Key(x.Alias))) ||
                MapperSupport.Text(x.FriendlyName) && badNameGroups.Contains((x.RoomId, MapperSupport.Key(x.FriendlyName)))
                    ? V2Severity.Danger : V2Severity.Neutral);
        }

        public int ActiveRoomReferenceCount(string value) => activeRoomReferences.GetValueOrDefault(MapperSupport.Key(value));
        public int ActiveRoomGameCount(string value) => activeRoomGames.GetValueOrDefault(MapperSupport.Key(value));
        public int SubroomNameCount(Guid roomId, string value) => subroomNames.GetValueOrDefault((roomId, MapperSupport.Key(value)));
        public int SubroomReferenceCount(Guid roomId, string value) => subroomReferences.GetValueOrDefault((roomId, MapperSupport.Key(value)));
        public int TransitionAliasCount(Guid roomId, string value) => transitionAliases.GetValueOrDefault((roomId, MapperSupport.Key(value)));
        public int TransitionNameCount(Guid roomId, string value) => transitionNames.GetValueOrDefault((roomId, MapperSupport.Key(value)));
        public int TransitionEndpointCount(StatusTransition row) => transitionEndpoints.Count(row);
        public int InverseCount(Guid candidateRoomId, Guid destinationRoomId, Guid destinationId) => inverses.GetValueOrDefault((candidateRoomId, destinationRoomId, destinationId));
        public int RoomGameIdCount(Guid roomId, string value) => roomGameIds.GetValueOrDefault((roomId, MapperSupport.Key(value)));
        public int CheckNameCount(Guid roomId, string value) => checkNames.GetValueOrDefault((roomId, MapperSupport.Key(value)));
        public V2Severity PathwaySeverity(Guid id) => pathwaySeverities.GetValueOrDefault(id);

        public V2ReferenceState RoomReferenceState(string? text, Guid? id)
        {
            if (!MapperSupport.Text(text)) return V2ReferenceState.None;
            var matches = roomsByReference.GetValueOrDefault(MapperSupport.Key(text)) ?? [];
            return ReferenceState(id, matches.Select(x => (x.Id, x.Archived)));
        }

        public V2ReferenceState SubroomReferenceState(Guid roomId, string? text, Guid? id)
        {
            if (!MapperSupport.Text(text)) return V2ReferenceState.None;
            var matches = subroomsByReference.GetValueOrDefault((roomId, MapperSupport.Key(text))) ?? [];
            return ReferenceState(id, matches.Select(x => (x.Id, x.Archived)));
        }

        public V2ReferenceState DestinationAliasState(StatusTransition row, V2ReferenceState roomState)
        {
            if (!MapperSupport.Text(row.DestinationAlias)) return V2ReferenceState.None;
            if (roomState == V2ReferenceState.TargetArchived) return V2ReferenceState.TargetArchived;
            if (roomState != V2ReferenceState.Resolved || row.DestinationRoomId is null)
                return row.DestinationId is null ? V2ReferenceState.Unresolved : V2ReferenceState.OutOfSync;
            var matches = destinationAliases.GetValueOrDefault((row.DestinationRoomId.Value, MapperSupport.Key(row.DestinationAlias))) ?? [];
            var active = matches.Where(x => !x.Archived).ToArray();
            if (active.Length == 0) return matches.Any(x => x.Archived) ? V2ReferenceState.TargetArchived : row.DestinationId is null ? V2ReferenceState.Unresolved : V2ReferenceState.OutOfSync;
            return active.Length > 1 ? V2ReferenceState.Ambiguous : active[0].Id == row.DestinationId ? V2ReferenceState.Resolved : V2ReferenceState.OutOfSync;
        }

        private static V2ReferenceState ReferenceState(Guid? id, IEnumerable<(Guid Id, bool Archived)> source)
        {
            var matches = source.ToArray(); var active = matches.Where(x => !x.Archived).ToArray();
            if (active.Length == 1) return active[0].Id == id ? V2ReferenceState.Resolved : V2ReferenceState.OutOfSync;
            if (active.Length > 1) return V2ReferenceState.Ambiguous;
            if (matches.Any(x => x.Archived)) return V2ReferenceState.TargetArchived;
            return id is null ? V2ReferenceState.Unresolved : V2ReferenceState.OutOfSync;
        }

        private static bool InvalidPathwayGroup(IReadOnlyList<StatusConnection> group)
        {
            if (group.Count > 2) return true;
            var first = group[0];
            if (!MapperSupport.Same(first.FriendlyName, first.FriendlyName) || !SamePair(first, first)) return true;
            if (group.Count == 1) return false;
            var second = group[1];
            return !MapperSupport.Same(first.FriendlyName, second.FriendlyName) || !SamePair(first, second) ||
                !SamePair(second, second) || SameDirection(first, second);
        }
        private static bool SameEndpoint(string? left, Guid? leftId, string? right, Guid? rightId) => leftId is not null && rightId is not null ? leftId == rightId : MapperSupport.Same(left, right);
        private static bool SameDirection(StatusConnection left, StatusConnection right) => SameEndpoint(left.Source, left.SourceId, right.Source, right.SourceId) && SameEndpoint(left.Destination, left.DestinationId, right.Destination, right.DestinationId);
        private static bool SamePair(StatusConnection left, StatusConnection right) => SameDirection(left, right) || SameEndpoint(left.Source, left.SourceId, right.Destination, right.DestinationId) && SameEndpoint(left.Destination, left.DestinationId, right.Source, right.SourceId);
        private static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull => counts[key] = counts.GetValueOrDefault(key) + 1;
        private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> values, TKey key, TValue value) where TKey : notnull
        {
            if (!values.TryGetValue(key, out var list)) values[key] = list = [];
            list.Add(value);
        }

        private sealed class TransitionEndpointIndex
        {
            private readonly Dictionary<(Guid, Guid, Guid), int> bothIds = [];
            private readonly Dictionary<(Guid, Guid, string), int> roomIdAliasNull = [];
            private readonly Dictionary<(Guid, string, Guid), int> roomNullAliasId = [];
            private readonly Dictionary<(Guid, string, string), int> bothNull = [];
            private readonly Dictionary<(Guid, Guid, string), int> roomIdAliasText = [];
            private readonly Dictionary<(Guid, string, string), int> roomNullAliasText = [];
            private readonly Dictionary<(Guid, string, Guid), int> roomTextAliasId = [];
            private readonly Dictionary<(Guid, string, string), int> roomTextAliasNull = [];
            private readonly Dictionary<(Guid, string, string), int> allText = [];

            public void Add(StatusTransition row)
            {
                if (!MapperSupport.Text(row.DestinationRoom) || !MapperSupport.Text(row.DestinationAlias)) return;
                var roomText = MapperSupport.Key(row.DestinationRoom); var aliasText = MapperSupport.Key(row.DestinationAlias);
                Increment(allText, (row.RoomId, roomText, aliasText));
                if (row.DestinationRoomId is { } roomId) Increment(roomIdAliasText, (row.RoomId, roomId, aliasText));
                else Increment(roomNullAliasText, (row.RoomId, roomText, aliasText));
                if (row.DestinationId is { } aliasId) Increment(roomTextAliasId, (row.RoomId, roomText, aliasId));
                else Increment(roomTextAliasNull, (row.RoomId, roomText, aliasText));
                if (row.DestinationRoomId is { } bothRoom && row.DestinationId is { } bothAlias) Increment(bothIds, (row.RoomId, bothRoom, bothAlias));
                else if (row.DestinationRoomId is { } onlyRoom) Increment(roomIdAliasNull, (row.RoomId, onlyRoom, aliasText));
                else if (row.DestinationId is { } onlyAlias) Increment(roomNullAliasId, (row.RoomId, roomText, onlyAlias));
                else Increment(bothNull, (row.RoomId, roomText, aliasText));
            }

            public int Count(StatusTransition row)
            {
                if (!MapperSupport.Text(row.DestinationRoom) || !MapperSupport.Text(row.DestinationAlias)) return 0;
                var roomText = MapperSupport.Key(row.DestinationRoom); var aliasText = MapperSupport.Key(row.DestinationAlias);
                if (row.DestinationRoomId is { } roomId && row.DestinationId is { } aliasId)
                    return bothIds.GetValueOrDefault((row.RoomId, roomId, aliasId)) + roomIdAliasNull.GetValueOrDefault((row.RoomId, roomId, aliasText)) +
                        roomNullAliasId.GetValueOrDefault((row.RoomId, roomText, aliasId)) + bothNull.GetValueOrDefault((row.RoomId, roomText, aliasText));
                if (row.DestinationRoomId is { } onlyRoom)
                    return roomIdAliasText.GetValueOrDefault((row.RoomId, onlyRoom, aliasText)) + roomNullAliasText.GetValueOrDefault((row.RoomId, roomText, aliasText));
                if (row.DestinationId is { } onlyAlias)
                    return roomTextAliasId.GetValueOrDefault((row.RoomId, roomText, onlyAlias)) + roomTextAliasNull.GetValueOrDefault((row.RoomId, roomText, aliasText));
                return allText.GetValueOrDefault((row.RoomId, roomText, aliasText));
            }
        }
    }

    private sealed record StatusRoom(Guid Id, string FriendlyName, string ReferenceId, string? InGameId, bool Archived);
    private sealed record StatusSubroom(Guid Id, Guid RoomId, string FriendlyName, string ReferenceId, double? X, double? Y, double? Width, double? Height, bool Archived);
    private sealed record StatusTransition(Guid Id, Guid RoomId, string Alias, string FriendlyName, string? InGameId, string? Source,
        string? DestinationRoom, string? DestinationAlias, string Requirements, Guid? SourceId, Guid? DestinationRoomId,
        Guid? DestinationId, bool Todo, bool? Verified, bool Archived, bool? ParseSucceeded);
    private sealed record StatusConnection(Guid Id, Guid RoomId, string Alias, string FriendlyName, string Source, string Destination,
        string Requirements, Guid? SourceId, Guid? DestinationId, bool Todo, bool? Verified, bool Archived, bool? ParseSucceeded);
    private sealed record StatusCheck(Guid Id, Guid RoomId, string FriendlyName, string? InGameId, string? Subroom,
        string Requirements, string? LocationType, double? AnnotationX, double? AnnotationY, Guid? SubroomId,
        bool Todo, bool? Verified, bool Archived, bool? ParseSucceeded);
}
