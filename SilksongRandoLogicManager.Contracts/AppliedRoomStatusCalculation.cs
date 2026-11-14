using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using SilksongRandoLogicManager.Contracts;
using MapperSupport = Silksong_Rando_Logic_Manager.Services.RoomPresentationRules;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>EF-free applied completion rules, shared by browser and retained server readers.</summary>
public static class AppliedRoomStatusCalculation
{
    public static IReadOnlyDictionary<Guid, AppliedRoomStatus> Calculate(
        IReadOnlyList<SilksongRandoLogicManager.Contracts.RoomScalarSnapshot> facts)
    {
        var rooms = facts.Select(ProjectRoom).ToArray();
        var subrooms = facts.SelectMany(room => room.Subrooms.Select(x => ProjectSubroom(room.Observation.RoomId, x))).ToArray();
        var transitions = facts.SelectMany(room => room.Transitions.Select(x => ProjectTransition(room.Observation.RoomId, x))).ToArray();
        var connections = facts.SelectMany(room => room.Connections.Select(x => ProjectConnection(room.Observation.RoomId, x))).ToArray();
        // Preserve the retained reader's exclusion of checks owned by archived rooms.
        var checks = facts.Where(room => !room.Header.Observation.IsArchived)
            .SelectMany(room => room.Checks.Select(x => ProjectCheck(room.Observation.RoomId, x))).ToArray();
        return Calculate(rooms, subrooms, transitions, connections, checks);
    }

    /// <summary>Invocation-local dependency selection before scalar row allocation.
    /// Dependency rows are lookup facts, not recursively evaluated owners.</summary>
    public static IReadOnlyDictionary<Guid, AppliedRoomStatus> Calculate(
        IReadOnlyList<RoomScalarSnapshot> facts, IReadOnlyCollection<Guid> requestedOwnerIds)
    {
        var requested = requestedOwnerIds.ToHashSet();
        var owners = facts.Where(room => requested.Contains(room.Observation.RoomId)
            && !room.Header.Observation.IsArchived).ToArray();
        var activeOwnerIds = owners.Select(room => room.Observation.RoomId).ToHashSet();
        var references = new HashSet<string>(StringComparer.Ordinal);
        var games = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<(Guid RoomId, string Alias)>();
        var inverses = new HashSet<(Guid CandidateRoomId, Guid DestinationRoomId, Guid DestinationId)>();
        foreach (var room in owners)
        {
            if (MapperSupport.Text(room.Header.Values.ReferenceId)) references.Add(MapperSupport.Key(room.Header.Values.ReferenceId));
            if (MapperSupport.Text(room.Header.Values.InGameId)) games.Add(MapperSupport.Key(room.Header.Values.InGameId));
            foreach (var row in room.Transitions.Where(x => !x.Observation.IsArchived))
            {
                if (MapperSupport.Text(row.Values.DestinationRoomReferenceText)) references.Add(MapperSupport.Key(row.Values.DestinationRoomReferenceText));
                if (row.ResolvedDestinationRoomId is not { } destinationRoomId) continue;
                if (MapperSupport.Text(row.Values.DestinationTransitionAliasText))
                    aliases.Add((destinationRoomId, MapperSupport.Key(row.Values.DestinationTransitionAliasText)));
                if (row.ResolvedDestinationTransitionId is not null)
                    inverses.Add((destinationRoomId, room.Observation.RoomId, row.Observation.EntityId));
            }
        }

        var rooms = facts.Where(room => requested.Contains(room.Observation.RoomId)
            || MapperSupport.Text(room.Header.Values.ReferenceId) && references.Contains(MapperSupport.Key(room.Header.Values.ReferenceId))
            || MapperSupport.Text(room.Header.Values.InGameId) && games.Contains(MapperSupport.Key(room.Header.Values.InGameId)))
            .Select(ProjectRoom).ToArray();
        var subrooms = owners.SelectMany(room => room.Subrooms.Select(x => ProjectSubroom(room.Observation.RoomId, x))).ToArray();
        var transitions = new List<StatusTransition>();
        var selectedTransitionIds = new HashSet<Guid>();
        foreach (var room in facts)
        {
            var roomId = room.Observation.RoomId;
            foreach (var row in room.Transitions)
            {
                // Do not filter by the candidate owning room's archive state:
                // the existing alias/inverse indexes do not apply that filter.
                if (!activeOwnerIds.Contains(roomId)
                    && !(MapperSupport.Text(row.Values.Alias) && aliases.Contains((roomId, MapperSupport.Key(row.Values.Alias))))
                    && !(!row.Observation.IsArchived && row.ResolvedDestinationRoomId is { } destinationRoomId
                        && row.ResolvedDestinationTransitionId is { } destinationId
                        && inverses.Contains((roomId, destinationRoomId, destinationId)))) continue;
                if (selectedTransitionIds.Add(row.Observation.EntityId)) transitions.Add(ProjectTransition(roomId, row));
            }
        }
        var connections = owners.SelectMany(room => room.Connections.Select(x => ProjectConnection(room.Observation.RoomId, x))).ToArray();
        var checks = owners.SelectMany(room => room.Checks.Select(x => ProjectCheck(room.Observation.RoomId, x))).ToArray();
        return Calculate(rooms, subrooms, transitions, connections, checks, requestedOwnerIds);
    }

    private static StatusRoom ProjectRoom(RoomScalarSnapshot x) => new(x.Observation.RoomId, x.Header.Values.FriendlyName,
        x.Header.Values.ReferenceId, x.Header.Values.InGameId, x.Header.Observation.IsArchived);
    private static StatusSubroom ProjectSubroom(Guid roomId, RoomSubroomSnapshot x) => new(
        x.Observation.EntityId, roomId, x.Values.FriendlyName, x.Values.ReferenceId,
        x.Geometry.X, x.Geometry.Y, x.Geometry.Width, x.Geometry.Height, x.Observation.IsArchived);
    private static StatusTransition ProjectTransition(Guid roomId, RoomTransitionSnapshot x) => new(
        x.Observation.EntityId, roomId, x.Values.Alias, x.Values.FriendlyName, x.Metadata.InGameId,
        x.Values.SourceSubroomReferenceText, x.Values.DestinationRoomReferenceText, x.Values.DestinationTransitionAliasText,
        x.Values.Requirements, x.ResolvedSourceSubroomId, x.ResolvedDestinationRoomId, x.ResolvedDestinationTransitionId,
        x.Values.IsTodo, x.Values.IsVerified, x.Observation.IsArchived, x.RequirementsParseSucceeded);
    private static StatusConnection ProjectConnection(Guid roomId, RoomConnectionSnapshot x) => new(
        x.Observation.EntityId, roomId, x.Values.Alias, x.Values.FriendlyName,
        x.Values.SourceSubroomReferenceText, x.Values.DestinationSubroomReferenceText, x.Values.Requirements,
        x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId, x.Values.IsTodo, x.Values.IsVerified,
        x.Observation.IsArchived, x.RequirementsParseSucceeded);
    private static StatusCheck ProjectCheck(Guid roomId, RoomCheckSnapshot x) => new(
        x.Observation.EntityId, roomId, x.Values.FriendlyName, x.Values.SubroomReferenceText,
        x.Values.Requirements, x.Values.LocationType, x.Metadata.AnnotationSceneUnitX, x.Metadata.AnnotationSceneUnitY,
        x.ResolvedSubroomId, x.Values.IsTodo, x.Values.IsVerified, x.Observation.IsArchived, x.RequirementsParseSucceeded);

    /// <summary>Compare canonical inputs for one replaced, structurally validated
    /// room. Scratch belongs only to this invocation; authored order is untouched.</summary>
    public static (bool InputsEqual, bool ExternalEqual) CompareInputs(RoomScalarSnapshot prior, RoomScalarSnapshot next)
    {
        var oldRoom = ProjectRoom(prior);
        var room = ProjectRoom(next);
        // Mask only reviewed owner-local fields. A new scalar field remains
        // externally significant until its consumers have been reviewed.
        if (oldRoom != room with { FriendlyName = oldRoom.FriendlyName }) return (false, false);
        var roomId = room.Id;
        var transitions = CompareRows(prior.Transitions, next.Transitions,
            x => ProjectTransition(roomId, x), x => x.Id, SameExternalTransition);
        if (!transitions.ExternalEqual) return (false, false);
        var equal = oldRoom == room && transitions.InputsEqual
            && CompareRows(prior.Subrooms, next.Subrooms, x => ProjectSubroom(roomId, x), x => x.Id).InputsEqual
            && CompareRows(prior.Connections, next.Connections, x => ProjectConnection(roomId, x), x => x.Id).InputsEqual
            && CompareRows(prior.Checks, next.Checks, x => ProjectCheck(roomId, x), x => x.Id).InputsEqual;
        return (equal, true);
    }

    internal static bool SameExternalTransition(StatusTransition prior, StatusTransition next) => prior == next with
    {
        FriendlyName = prior.FriendlyName, InGameId = prior.InGameId, Source = prior.Source,
        DestinationRoom = prior.DestinationRoom, DestinationAlias = prior.DestinationAlias,
        Requirements = prior.Requirements, SourceId = prior.SourceId, Todo = prior.Todo,
        Verified = prior.Verified, ParseSucceeded = prior.ParseSucceeded
    };

    private static (bool InputsEqual, bool ExternalEqual) CompareRows<T, TScalar>(ImmutableArray<T> prior,
        ImmutableArray<T> next, Func<T, TScalar> project, Func<TScalar, Guid> id,
        Func<TScalar, TScalar, bool>? sameExternal = null) where TScalar : notnull
    {
        if (prior.IsDefault || next.IsDefault) return (false, false);
        if (prior == next) return (true, true);
        if (prior.Length != next.Length) return (false, sameExternal is null);
        var old = new Dictionary<Guid, TScalar>(prior.Length);
        foreach (var row in prior)
        {
            var scalar = project(row);
            if (!old.TryAdd(id(scalar), scalar)) return (false, false);
        }
        var equal = true;
        foreach (var row in next)
        {
            var scalar = project(row);
            if (!old.Remove(id(scalar), out var before)) return (false, sameExternal is null);
            if (sameExternal is not null && !sameExternal(before, scalar)) return (false, false);
            equal &= EqualityComparer<TScalar>.Default.Equals(before, scalar);
        }
        return (equal, true);
    }

    public static IReadOnlyDictionary<Guid, AppliedRoomStatus> Calculate(
        IReadOnlyList<StatusRoom> rooms, IReadOnlyList<StatusSubroom> subrooms,
        IReadOnlyList<StatusTransition> transitions, IReadOnlyList<StatusConnection> connections,
        IReadOnlyList<StatusCheck> checks, IReadOnlyCollection<Guid>? requested = null)
    {
        var selectedIds = requested is null ? rooms.Select(x => x.Id).ToArray() : requested.ToArray();
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
            if (MapperSupport.Text(row.InGameId) && indexes.TransitionGameIdCount(room.Id, row.InGameId!) > 1) Add(V2Severity.Danger);
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
        private readonly Dictionary<Guid, Dictionary<string, int>> transitionGameIds;
        private readonly Dictionary<(Guid RoomId, string Key), int> checkNames;
        private readonly Dictionary<Guid, V2Severity> pathwaySeverities;

        public StatusIndexes(IReadOnlyList<StatusRoom> rooms, IReadOnlyList<StatusSubroom> subrooms,
            IReadOnlyList<StatusTransition> transitions, IReadOnlyList<StatusConnection> connections,
            IReadOnlyList<StatusCheck> checks)
        {
            activeRoomReferences = []; activeRoomGames = []; subroomNames = []; subroomReferences = [];
            transitionAliases = []; transitionNames = []; inverses = []; transitionGameIds = []; checkNames = [];
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
                if (MapperSupport.Text(row.InGameId))
                {
                    if (!transitionGameIds.TryGetValue(row.RoomId, out var gameIds))
                        transitionGameIds[row.RoomId] = gameIds = new(StringComparer.OrdinalIgnoreCase);
                    Increment(gameIds, row.InGameId!.Trim());
                }
            }
            foreach (var row in checks.Where(x => !x.Archived))
            {
                if (MapperSupport.Text(row.FriendlyName)) Increment(checkNames, (row.RoomId,
                    RoomPresentationRules.CanonicalSegment(row.FriendlyName) ?? MapperSupport.Key(row.FriendlyName)));
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
        public int TransitionGameIdCount(Guid roomId, string value) => transitionGameIds.GetValueOrDefault(roomId)?.GetValueOrDefault(value.Trim()) ?? 0;
        public int CheckNameCount(Guid roomId, string value) => checkNames.GetValueOrDefault((roomId,
            RoomPresentationRules.CanonicalSegment(value) ?? MapperSupport.Key(value)));
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

    public sealed record StatusRoom(Guid Id, string FriendlyName, string ReferenceId, string? InGameId, bool Archived);
    public sealed record StatusSubroom(Guid Id, Guid RoomId, string FriendlyName, string ReferenceId, double? X, double? Y, double? Width, double? Height, bool Archived);
    public sealed record StatusTransition(Guid Id, Guid RoomId, string Alias, string FriendlyName, string? InGameId, string? Source,
        string? DestinationRoom, string? DestinationAlias, string Requirements, Guid? SourceId, Guid? DestinationRoomId,
        Guid? DestinationId, bool Todo, bool? Verified, bool Archived, bool? ParseSucceeded);
    public sealed record StatusConnection(Guid Id, Guid RoomId, string Alias, string FriendlyName, string Source, string Destination,
        string Requirements, Guid? SourceId, Guid? DestinationId, bool Todo, bool? Verified, bool Archived, bool? ParseSucceeded);
    public sealed record StatusCheck(Guid Id, Guid RoomId, string FriendlyName, string? Subroom,
        string Requirements, string? LocationType, double? AnnotationX, double? AnnotationY, Guid? SubroomId,
        bool Todo, bool? Verified, bool Archived, bool? ParseSucceeded);
}
