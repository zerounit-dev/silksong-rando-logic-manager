using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Silksong_Rando_Logic_Manager.Services;

internal static class RoomExportStructure
{
    // The caller owns the operation-local context and read transaction. Geometry
    // selects constants in place of graph-only authored payload and never reads catalogues.
    internal static async Task<Snapshot> LoadSnapshotAsync(LogicDbContext db, bool graphPayload, CancellationToken token)
    {
        var groups = await db.RoomGroups.AsNoTracking().Select(x => new GroupRow(x.Id, x.FriendlyName, x.IsVirtual, x.SortOrder)).ToListAsync(token);
        var rooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived)
            .Select(x => new RoomRow(x.Id, x.RoomGroupId, x.ReferenceId, x.FriendlyName, x.InGameId, graphPayload ? x.Contributors : null, graphPayload ? x.Comments : null, x.SortOrder, x.SceneMinX, x.SceneMinY, x.SceneMaxX, x.SceneMaxY,
                graphPayload ? null : x.SceneImagePixelsPerUnitX, graphPayload ? null : x.SceneImagePixelsPerUnitY)).ToListAsync(token);
        var roomIds = rooms.Select(x => x.Id).ToArray();
        var subrooms = await db.Subrooms.AsNoTracking().Where(x => !x.IsArchived && roomIds.Contains(x.RoomId))
            .Select(x => new SubroomRow(x.Id, x.RoomId, x.ReferenceId, x.FriendlyName, graphPayload ? x.Notes : null, x.SortOrder, x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth, x.SceneUnitHeight, x.EnableAnnotation)).ToListAsync(token);
        var transitions = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && roomIds.Contains(x.RoomId))
            .Select(x => new TransitionRow(x.Id, x.RoomId, x.Alias, x.FriendlyName, x.InGameId, x.SourceSubroomReferenceText,
                x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, graphPayload ? x.Requirements : "", graphPayload ? x.Notes : "", x.ResolvedSourceSubroomId,
                x.ResolvedDestinationRoomId, x.ResolvedDestinationTransitionId, x.SortOrder, graphPayload && x.IsTodo, graphPayload ? x.IsVerified : null, x.IsUnsafeArrival,
                x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.EnableAnnotation, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ)).ToListAsync(token);
        var connections = await db.SubroomConnections.AsNoTracking().Where(x => !x.IsArchived && roomIds.Contains(x.RoomId))
            .Select(x => new ConnectionRow(x.Id, x.RoomId, x.Alias, x.FriendlyName, x.SourceSubroomReferenceText,
                x.DestinationSubroomReferenceText, graphPayload ? x.Requirements : "", graphPayload ? x.Notes : "", x.ResolvedSourceSubroomId,
                x.ResolvedDestinationSubroomId, x.SortOrder, graphPayload && x.IsTodo, graphPayload ? x.IsVerified : null, x.SceneUnitX, x.SceneUnitY, x.EnableAnnotation)).ToListAsync(token);
        var checks = await db.CheckLocations.AsNoTracking().Where(x => !x.IsArchived && roomIds.Contains(x.RoomId))
            .Select(x => new CheckRow(x.Id, x.RoomId, x.FriendlyName, x.InGameId, x.SubroomReferenceText, graphPayload ? x.Requirements : "", graphPayload ? x.Notes : "",
                x.LocationType, x.ResolvedSubroomId, x.SortOrder, graphPayload && x.IsTodo, graphPayload ? x.IsVerified : null,
                x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.EnableAnnotation, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ)).ToListAsync(token);
        return new(groups, rooms, subrooms, transitions, connections, checks);
    }

    internal static Projection Project(Snapshot snapshot, List<RoomGraphExportFailure> globalFailures,
        PreparedGraphEvidence? prepared = null)
    {
        var evidence = prepared?.References;
        var roomFailures = new List<RoomGraphExportFailure>();
        var warnings = new List<RoomGraphExportWarning>();
        var identityOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        var groupStates = snapshot.Groups
            .Select(group => new GroupState(group, CanonicalValue(group.Name)))
            .OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Row.Id).ToArray();
        foreach (var state in groupStates)
        {
            if (state.Id is null)
            {
                var failure = new RoomGraphExportFailure($"room group '{state.Row.Name}' does not produce a nonempty canonical export ID");
                globalFailures.Add(failure);
                prepared?.Integrity.Add(new(PreparedIntegrityFamily.Identifiers, failure, $"Room group: {state.Row.Name}", "group", state.Row.Id));
            }
            else Register(state.Id, $"room group '{state.Row.Name}'", identityOwners, globalFailures, prepared: prepared, recordId: state.Row.Id);
        }
        var groupById = groupStates.ToDictionary(x => x.Row.Id);

        var roomStates = snapshot.Rooms.Select(room =>
        {
            groupById.TryGetValue(room.GroupId ?? Guid.Empty, out var group);
            var local = CanonicalValue(room.ReferenceId);
            var id = group?.Id is not null && local is not null ? $"{group.Id}/{local}" : null;
            return new RoomState(room, group, id);
        }).OrderBy(x => x.Group?.Row.SortOrder ?? int.MaxValue)
          .ThenBy(x => x.Group?.Id, StringComparer.Ordinal)
          .ThenBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Row.Id).ToArray();
        foreach (var state in roomStates)
        {
            if (state.Group is null) Fail(state, "active room is not assigned to a room group", family: PreparedIntegrityFamily.RoomGroup);
            if (string.IsNullOrWhiteSpace(state.Row.Name)) Fail(state, "room name is blank");
            if (CanonicalValue(state.Row.ReferenceId) is null) Fail(state, "room reference ID does not produce a nonempty export segment");
            if (state.Id is not null) Register(state.Id, $"room '{state.Row.Name}'", identityOwners, roomFailures, state, prepared: prepared, recordId: state.Row.Id);
        }

        var subroomsByRoom = snapshot.Subrooms.GroupBy(x => x.RoomId).ToDictionary(x => x.Key, x => x.ToArray());
        var transitionsByRoom = snapshot.Transitions.GroupBy(x => x.RoomId).ToDictionary(x => x.Key, x => x.ToArray());
        var connectionsByRoom = snapshot.Connections.GroupBy(x => x.RoomId).ToDictionary(x => x.Key, x => x.ToArray());
        var checksByRoom = snapshot.Checks.GroupBy(x => x.RoomId).ToDictionary(x => x.Key, x => x.ToArray());

        var subroomStates = new Dictionary<Guid, SubroomState>();
        var transitionStates = new Dictionary<Guid, TransitionState>();
        var locationStates = new Dictionary<Guid, LocationState>();
        foreach (var room in roomStates)
        {
            var subrooms = Get(subroomsByRoom, room.Row.Id);
            foreach (var row in subrooms.OrderBy(x => x.SortOrder).ThenBy(x => CanonicalValue(x.ReferenceId), StringComparer.Ordinal).ThenBy(x => x.Id))
            {
                var segment = CanonicalValue(row.ReferenceId);
                var id = room.Id is not null && segment is not null ? $"{room.Id}#{segment}" : null;
                var state = new SubroomState(row, room, id);
                subroomStates.Add(row.Id, state);
                if (string.IsNullOrWhiteSpace(row.Name)) Fail(room, "subroom name is blank", "subroom", Display(row.Name, row.ReferenceId), row.Id);
                if (segment is null) Fail(room, "subroom reference ID does not produce a nonempty export segment", "subroom", Display(row.Name, row.ReferenceId), row.Id);
                if (id is not null) Register(id, $"subroom '{Display(row.Name, row.ReferenceId)}'", identityOwners, roomFailures, room, "subroom", Display(row.Name, row.ReferenceId), prepared, row.Id);
            }
            foreach (var row in Get(transitionsByRoom, room.Row.Id).OrderBy(x => x.SortOrder).ThenBy(x => CanonicalValue(x.Alias), StringComparer.Ordinal).ThenBy(x => x.Id))
            {
            var aliasFailure = AliasFailure(row.Alias);
            var segment = aliasFailure is null ? CanonicalValue(row.Alias) : null;
                var id = room.Id is not null && segment is not null ? $"{room.Id}@{segment}" : null;
                var state = new TransitionState(row, room, id);
                transitionStates.Add(row.Id, state);
            if (aliasFailure is not null) Fail(room, $"transition alias {aliasFailure}", "transition", Display(row.Name, row.Alias), row.Id);
                else if (segment is null) Fail(room, "transition alias does not produce a nonempty canonical export segment", "transition", Display(row.Name, row.Alias), row.Id);
                if (string.IsNullOrWhiteSpace(row.Name)) Fail(room, "transition name is blank", "transition", Display(row.Name, row.Alias), row.Id);
                if (id is not null) Register(id, $"transition '{Display(row.Name, row.Alias)}'", identityOwners, roomFailures, room, "transition", Display(row.Name, row.Alias), prepared, row.Id);
            }
            foreach (var row in Get(checksByRoom, room.Row.Id).OrderBy(x => x.SortOrder).ThenBy(x => CanonicalValue(x.Name), StringComparer.Ordinal).ThenBy(x => x.Id))
            {
                var definition = row.LocationType is null ? null : CheckLocationTypeCatalogue.Definitions.FirstOrDefault(x => x.OutputValue == row.LocationType);
                var type = row.LocationType is null ? "unknown" : definition is null ? null : CanonicalValue(definition.OutputValue);
                var name = CanonicalValue(row.Name);
                var id = room.Id is not null && type is not null && name is not null ? $"location:{room.Id}::{type}/{name}" : null;
                var state = new LocationState(row, room, id, type, definition?.HasPersistedState);
                locationStates.Add(row.Id, state);
                if (string.IsNullOrWhiteSpace(row.Name)) Fail(room, "location name is blank", "location", Display(row.Name, row.InGameId), row.Id);
                if (name is null) Fail(room, "location name does not produce a nonempty export segment", "location", Display(row.Name, row.InGameId), row.Id);
                if (row.LocationType is not null && definition is null) Fail(room, $"location type '{row.LocationType}' is not recognized", "location", Display(row.Name, row.InGameId), row.Id);
                if (id is not null) Register(id, $"location '{Display(row.Name, row.InGameId)}'", identityOwners, roomFailures, room, "location", Display(row.Name, row.InGameId), prepared, row.Id);
            }
        }

        var projectedRooms = new List<ProjectedRoom>();
        var selectedNodes = evidence is null ? null : new List<SelectedNode>();
        var selectedPorts = evidence is null ? null : new List<SelectedPort>();
        var exactDestinationRooms = evidence is null ? null : new Dictionary<Guid, Guid>();
        foreach (var room in roomStates)
        {
            var roomSubrooms = Get(subroomsByRoom, room.Row.Id).Select(x => subroomStates[x.Id]).ToArray();
            var nodes = roomSubrooms.Length == 0
                ? new[] { new ProjectedNode(room.Id is null ? "#room" : $"{room.Id}#room", "room", null) }
                : roomSubrooms.Where(x => x.Id is not null).OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal)
                    .Select(x => new ProjectedNode(x.Id!, x.Row.Name, x.Row)).ToArray();
            if (roomSubrooms.Length == 0 && room.Id is not null) Register($"{room.Id}#room", $"synthetic node for room '{room.Row.Name}'", identityOwners, roomFailures, room, prepared: prepared, recordId: room.Row.Id);

            var projectedTransitions = new List<ProjectedTransition>();
            foreach (var transition in Get(transitionsByRoom, room.Row.Id).Select(x => transitionStates[x.Id]).OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                var child = Display(transition.Row.Name, transition.Row.Alias);
                var requested = $"{transition.Row.DestinationRoomText ?? "(blank)"} / {transition.Row.DestinationAliasText ?? "(blank)"}";
                SubroomState? sourceState = null;
                var sourceNode = roomSubrooms.Length == 0 ? nodes[0].Id : ResolveSubroomNode(roomFailures, room, transition.Row.SourceText, transition.Row.ResolvedSourceId, roomSubrooms, "transition", child, transition.Row.Id, "source subroom", evidence, out sourceState);
                if (roomSubrooms.Length == 0 || sourceState?.Row.Id == transition.Row.ResolvedSourceId)
                    ObserveNode(sourceNode, "transition", child, transition.Row.Id, "source subroom", transition.Row.SourceText);
                string? target = null;
                Guid? exactTargetRoom = null;
                var rowReasons = new List<string>();
                var roomBlank = string.IsNullOrWhiteSpace(transition.Row.DestinationRoomText);
                var aliasBlank = string.IsNullOrWhiteSpace(transition.Row.DestinationAliasText);
                // The room can be exact even if there is no valid entrance. Match once;
                // only the existing complete-pair branch reports room-resolution failures.
                var roomMatches = !roomBlank && (!aliasBlank || evidence is not null)
                    ? MatchReference(transition.Row.DestinationRoomText, roomStates, x => x.Row.ReferenceId) : [];
                if (roomMatches.Length == 1 && roomMatches[0].Row.Id == transition.Row.ResolvedDestinationRoomId)
                {
                    exactTargetRoom = roomMatches[0].Row.Id;
                    exactDestinationRooms?.Add(transition.Row.Id, exactTargetRoom.Value);
                }
                if (roomBlank && aliasBlank) rowReasons.Add("destination has not been mapped");
                else if (roomBlank != aliasBlank) PortFailure("transition destination room and alias must either both be blank or both be present", PreparedReferenceProblem.Incomplete);
                else
                {
                    var targetRoom = Resolve(roomFailures, transition.Row.DestinationRoomText, transition.Row.ResolvedDestinationRoomId, roomMatches,
                        x => x.Row.Id, room, "transition", child, transition.Row.Id, "destination room", evidence, PreparedReferenceKind.Port, requested);
                    if (targetRoom is not null)
                    {
                        var targets = Get(transitionsByRoom, targetRoom.Row.Id).Select(x => transitionStates[x.Id]).ToArray();
                        var targetMatches = MatchReference(transition.Row.DestinationAliasText, targets, x => x.Row.Alias);
                        var targetTransition = Resolve(roomFailures, transition.Row.DestinationAliasText, transition.Row.ResolvedDestinationTransitionId, targetMatches,
                            x => x.Row.Id, room, "transition", child, transition.Row.Id, "destination transition", evidence, PreparedReferenceKind.Port, requested, exactTargetRoom);
                        target = targetTransition?.Id;
                        if (target is not null && targetRoom.Row.Id == transition.Row.ResolvedDestinationRoomId &&
                            targetTransition!.Row.Id == transition.Row.ResolvedDestinationTransitionId)
                            selectedPorts?.Add(new(room.Row, child, transition.Row.Id, requested, targetTransition.Row.Id, targetRoom.Row.Id));
                        if (targetTransition is not null && target is null) PortFailure("resolved destination transition has no valid export identity", PreparedReferenceProblem.InvalidIdentity);
                        if (targetTransition is not null && !IsExactReciprocalTarget(transition, targetTransition, roomStates,
                            Get(transitionsByRoom, transition.Room.Row.Id).Select(x => transitionStates[x.Id]).ToArray()))
                            PortFailure($"selected destination transition '{Display(targetTransition.Row.Name, targetTransition.Row.Alias)}' does not resolve back to this exact source transition", PreparedReferenceProblem.NonReciprocal);
                    }
                }
                projectedTransitions.Add(new(transition.Id ?? "", transition.Row, sourceNode ?? "", target, rowReasons));

                void PortFailure(string cause, PreparedReferenceProblem problem) =>
                    ReferenceFailure(roomFailures, room, cause, "transition", child, transition.Row.Id, "destination entrance", requested, PreparedReferenceKind.Port, problem, evidence, exactTargetRoom);
            }

            var roomConnectionStates = new List<ConnectionState>();
            foreach (var row in Get(connectionsByRoom, room.Row.Id).OrderBy(x => x.SortOrder)
                .ThenBy(x => CanonicalValue(x.SourceText), StringComparer.Ordinal).ThenBy(x => CanonicalValue(x.DestinationText), StringComparer.Ordinal)
                .ThenBy(x => CanonicalValue(x.Alias), StringComparer.Ordinal).ThenBy(x => x.Id))
            {
                var child = Display(row.Name, row.Alias);
                var source = ResolveSubroomNode(roomFailures, room, row.SourceText, row.ResolvedSourceId, roomSubrooms, "connection", child, row.Id, "source subroom", evidence, out var sourceState);
                var target = ResolveSubroomNode(roomFailures, room, row.DestinationText, row.ResolvedDestinationId, roomSubrooms, "connection", child, row.Id, "destination subroom", evidence, out var targetState);
                if (sourceState?.Row.Id == row.ResolvedSourceId) ObserveNode(source, "connection", child, row.Id, "source subroom", row.SourceText);
                if (targetState?.Row.Id == row.ResolvedDestinationId) ObserveNode(target, "connection", child, row.Id, "destination subroom", row.DestinationText);
                if (roomSubrooms.Length == 0) Fail(room, "an active connection cannot exist in a room without active subrooms", "connection", Display(row.Name, row.Alias), row.Id, PreparedIntegrityFamily.Subroom);
                if (sourceState is not null && targetState is not null && sourceState.Row.Id == targetState.Row.Id) Fail(room, "connection source and destination form a self-loop", "connection", Display(row.Name, row.Alias), row.Id, PreparedIntegrityFamily.Connection);
            var aliasFailure = AliasFailure(row.Alias);
            var alias = aliasFailure is null ? CanonicalValue(row.Alias) : null;
            if (aliasFailure is not null) Fail(room, $"connection alias {aliasFailure}", "connection", Display(row.Name, row.Alias), row.Id);
                else if (alias is null) Fail(room, "connection alias does not produce a nonempty canonical export segment", "connection", Display(row.Name, row.Alias), row.Id);
                if (string.IsNullOrWhiteSpace(row.Name)) Fail(room, "connection name is blank", "connection", Display(row.Name, row.Alias), row.Id);
                var id = room.Id is not null && source is not null && target is not null && alias is not null
                    ? $"{room.Id}#{CanonicalValue(sourceState!.Row.ReferenceId)}>{CanonicalValue(targetState!.Row.ReferenceId)}@{alias}" : null;
                var state = new ConnectionState(row, room, id, sourceState, targetState);
                roomConnectionStates.Add(state);
                if (id is not null) Register(id, $"connection '{Display(row.Name, row.Alias)}'", identityOwners, roomFailures, room, "connection", Display(row.Name, row.Alias), prepared, row.Id);
            }
            ValidatePathways(roomFailures, room, roomConnectionStates, prepared);
            var projectedConnections = roomConnectionStates.OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal).Select(connection =>
            {
                return new ProjectedConnection(connection.Id ?? "", connection.Row, connection.Source?.Id ?? "", connection.Target?.Id ?? "");
            }).ToArray();

            var needsUnassignedNode = false;
            var projectedLocations = Get(checksByRoom, room.Row.Id).Select(x => locationStates[x.Id]).OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal).Select(location =>
            {
                var reasons = new List<string>();
                string? node;
                if (roomSubrooms.Length == 0) node = room.Id is null ? "#room" : $"{room.Id}#room";
                else
                {
                    var text = location.Row.SubroomText;
                    var matches = string.IsNullOrWhiteSpace(text) ? [] : roomSubrooms.Where(x =>
                        string.Equals(text.Trim(), x.Row.ReferenceId.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                    var cause = string.IsNullOrWhiteSpace(text) ? "subroom reference is blank"
                        : matches.Length == 0 ? $"subroom reference does not resolve to an active own-room subroom: {text}"
                        : matches.Length > 1 ? $"subroom reference is ambiguous: {text}"
                        : location.Row.ResolvedSubroomId != matches[0].Row.Id ? $"subroom resolver state is stale, mismatched, or wrong-room: {text}"
                        : null;
                    if (cause is not null)
                    {
                        needsUnassignedNode = true;
                        node = room.Id is null ? "#unassigned" : $"{room.Id}#unassigned";
                        reasons.Add(cause);
                        warnings.Add(new(cause, room.Id, room.Row.Name, "location", Display(location.Row.Name, location.Row.InGameId), room.Row.Id));
                        evidence?.Add(new(PreparedReferenceKind.Node, matches.Length switch
                        {
                            0 when string.IsNullOrWhiteSpace(text) => PreparedReferenceProblem.Blank,
                            0 => PreparedReferenceProblem.Missing,
                            > 1 => PreparedReferenceProblem.Ambiguous,
                            _ => PreparedReferenceProblem.Stale
                        }, room.Row.Name, "location", Display(location.Row.Name, location.Row.InGameId), location.Row.Id, "subroom", text, cause, room.Row.Id));
                    }
                    else
                    {
                        node = matches[0].Id;
                        if (node is null) ReferenceFailure(roomFailures, room, "resolved subroom has no valid export node identity", "location", Display(location.Row.Name, location.Row.InGameId), location.Row.Id, "subroom", text, PreparedReferenceKind.Node, PreparedReferenceProblem.InvalidIdentity, evidence);
                        else ObserveNode(node, "location", Display(location.Row.Name, location.Row.InGameId), location.Row.Id, "subroom", text);
                    }
                }
                return new ProjectedLocation(location.Id ?? "", location.Row, location.Type ?? "unknown", location.HasPersistedState, node ?? "", reasons);
            }).ToArray();

            if (needsUnassignedNode)
            {
                var unassignedId = room.Id is null ? "#unassigned" : $"{room.Id}#unassigned";
                if (room.Id is not null) Register(unassignedId, $"synthetic unassigned node for room '{room.Row.Name}'", identityOwners, roomFailures, room, prepared: prepared, recordId: room.Row.Id);
                nodes = [.. nodes, new ProjectedNode(unassignedId, "unassigned", null)];
            }

            projectedRooms.Add(new(room.Id ?? "", room.Row, room.Group?.Row.IsVirtual ?? false, nodes, projectedTransitions, projectedConnections, projectedLocations));

            void ObserveNode(string? id, string kind, string child, Guid recordId, string field, string? requested)
            {
                if (id is not null) selectedNodes?.Add(new(room.Row, kind, child, recordId, field, requested, id));
            }
        }

        if (prepared is not null)
        {
            var roomLabels = AssignEvidenceLabels(projectedRooms.Select(room =>
                (room.Row.Id, QualifiedLabel(room.Row.Name, room.Row.ReferenceId))), id => id);
            // Keep authored group identity even when its canonical ID (or the full export) fails.
            var authoredGroups = groupStates.ToDictionary(group => group.Row.Id,
                group => new PreparedGraphRoomGroup(group.Row.Id, group.Row.Name));
            prepared.RoomIdentities = roomStates.ToDictionary(room => room.Row.Id, room =>
                new PreparedGraphRoomIdentity(room.Row.Name, roomLabels[room.Row.Id],
                    room.Group is { } group ? authoredGroups[group.Row.Id] : null));
            var labels = prepared.RecordLabels = PreparedRecordLabels(projectedRooms, roomLabels, prepared);
            var portCollisions = projectedRooms.SelectMany(room => room.Transitions)
                .Where(x => x.Id.Length != 0).GroupBy(x => x.Id, StringComparer.Ordinal).Where(g => g.Skip(1).Any())
                .ToDictionary(g => g.Key, g => (
                    Labels: string.Join(", ", g.OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Row.Id).Select(x => labels[("transition", x.Row.Id)])),
                    Rooms: g.Select(x => x.Row.RoomId).Distinct().ToArray()), StringComparer.Ordinal);
            // The resolver can choose one authored alias exactly, but its canonical
            // destination ID can still denote more than one projected entrance.
            // Do not ascribe an unrelated collision to an unaffected exit.
            var exactPortSources = selectedPorts!.Select(port => port.RecordId).ToHashSet();
            foreach (var room in projectedRooms)
                foreach (var source in room.Transitions)
                    if (source.Target is { } target && portCollisions.TryGetValue(target, out var owners))
                        prepared.References.Add(new(PreparedReferenceKind.Port, PreparedReferenceProblem.Ambiguous, room.Row.Name,
                            "transition", $"{Display(source.Row.Name, source.Row.Alias)} [{source.Row.Alias}]", source.Row.Id,
                            "destination entrance", $"{source.Row.DestinationRoomText} / {source.Row.DestinationAliasText}",
                            $"Exact target '{target}' is shared by {owners.Labels}", room.Row.Id,
                            RelatedRooms: exactPortSources.Contains(source.Row.Id) ? owners.Rooms
                                : exactDestinationRooms!.TryGetValue(source.Row.Id, out var exactRoom) ? [exactRoom] : null));

            // These are the actual canonical node IDs (including synthetic nodes),
            // scoped by the resolved room record, not by a possibly colliding room ID.
            var collisions = new Dictionary<(Guid RoomId, string NodeId), string>();
            foreach (var room in projectedRooms)
            {
                var groups = room.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal).Where(group => group.Skip(1).Any()).ToArray();
                if (groups.Length == 0) continue;
                var nodeLabels = AssignEvidenceLabels(room.Nodes.Select(node => (node, node.Row is { } row
                    ? QualifiedLabel(row.Name, row.ReferenceId) : $"synthetic {node.Name}")), node => node.Row?.Id);
                foreach (var group in groups)
                    collisions.Add((room.Row.Id, group.Key), string.Join(", ", group.OrderBy(node => node.Row?.SortOrder)
                        .ThenBy(node => node.Row?.Id).Select(node => nodeLabels[node])));
            }
            var ambiguousPortNodes = new Dictionary<Guid, string>();
            foreach (var field in selectedNodes!)
                if (collisions.TryGetValue((field.Room.Id, field.NodeId), out var owners))
                {
                    var cause = $"Exact node '{field.NodeId}' in room '{roomLabels[field.Room.Id]}' is shared by {owners}";
                    prepared.References.Add(new(PreparedReferenceKind.Node, PreparedReferenceProblem.Ambiguous, field.Room.Name,
                        field.Kind, field.Record, field.RecordId, field.Field, field.Requested, cause, field.Room.Id));
                    if (field.Kind == "transition") ambiguousPortNodes.Add(field.RecordId, cause);
                }
            foreach (var port in selectedPorts!)
                if (ambiguousPortNodes.TryGetValue(port.TargetRecordId, out var cause))
                    prepared.References.Add(new(PreparedReferenceKind.Node, PreparedReferenceProblem.Ambiguous, port.Room.Name,
                        "transition", port.Record, port.RecordId, "destination entrance's node", port.Requested, cause,
                        port.Room.Id, RelatedRooms: [port.TargetRoomId]));
        }

        var roomOrder = roomStates.Select((room, index) => (room.Row.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var failures = globalFailures.Concat(roomFailures.OrderBy(x => x.RoomRecordId is { } id && roomOrder.TryGetValue(id, out var index) ? index : int.MaxValue)).ToArray();
        return new(groupStates, projectedRooms, failures, warnings);

        void Fail(RoomState room, string cause, string? kind = null, string? child = null, Guid? recordId = null,
            PreparedIntegrityFamily family = PreparedIntegrityFamily.Identifiers)
        {
            var failure = new RoomGraphExportFailure(cause, room.Id, room.Row.Name, kind, child, room.Row.Id);
            roomFailures.Add(failure);
            prepared?.Integrity.Add(new(family, failure, child ?? room.Row.Name, kind ?? "room", recordId ?? room.Row.Id, room.Row.Id));
        }
    }

    private static IReadOnlyDictionary<(string Kind, Guid Id), string> PreparedRecordLabels(IReadOnlyList<ProjectedRoom> rooms,
        IReadOnlyDictionary<Guid, string> roomLabels, PreparedGraphEvidence prepared)
    {
        var records = new List<((string Kind, Guid Id) Key, string Label)>();
        var titles = new Dictionary<(string Kind, Guid Id), string>();
        foreach (var room in rooms)
        {
            var roomLabel = roomLabels[room.Row.Id];
            foreach (var port in room.Transitions)
                Add("transition", port.Row.Id, QualifiedLabel(port.Row.Name, port.Row.Alias));
            foreach (var route in room.Connections)
            {
                var row = route.Row;
                var direction = $"{EvidenceEndpoint(row.SourceText)} → {EvidenceEndpoint(row.DestinationText)}";
                Add("connection", row.Id, $"{QualifiedLabel(row.Name, row.Alias)} [{direction}]");
            }
            foreach (var location in room.Locations)
            {
                var row = location.Row;
                var title = QualifiedLabel(row.Name, row.LocationType);
                // Keep the exact frozen evidence label; the checklist's title
                // is authored name/type only, even when it needs a fallback.
                Add("location", row.Id, QualifiedLabel(title, row.InGameId),
                    string.IsNullOrWhiteSpace(title) ? "Check location" : title);
            }

            void Add(string kind, Guid id, string title, string? checklistTitle = null)
            {
                var label = $"{roomLabel} / {kind} {title}";
                records.Add(((kind, id), label));
                titles.Add((kind, id), checklistTitle ?? title);
            }
        }

        prepared.RecordTitles = titles;
        return AssignEvidenceLabels(records, key => key.Id);
    }

    private static string EvidenceEndpoint(string? authored) => string.IsNullOrWhiteSpace(authored) ? "(blank)" : authored;

    private static string QualifiedLabel(string name, string? qualifier) =>
        string.IsNullOrWhiteSpace(qualifier) || string.Equals(name, qualifier, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(name)
            ? Display(name, qualifier) : $"{name} [{qualifier}]";

    private static Dictionary<TKey, string> AssignEvidenceLabels<TKey>(IEnumerable<(TKey Key, string Label)> labels,
        Func<TKey, Guid?> stableId) where TKey : notnull
    {
        var byBase = labels.ToLookup(x => x.Label, x => x.Key, StringComparer.Ordinal);
        // Reserve every rendered base (even duplicate ones) before issuing any ordinals.
        var used = byBase.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<TKey, string>();
        foreach (var group in byBase)
        {
            if (!group.Skip(1).Any())
                result.Add(group.First(), group.Key);
            else
            {
                var ordinal = 0;
                foreach (var key in group.OrderBy(stableId))
                {
                    string candidate;
                    do candidate = $"{group.Key} (#{++ordinal})";
                    while (!used.Add(candidate));
                    result.Add(key, candidate);
                }
            }
        }
        return result;
    }

    internal static Dictionary<string, RoomGraphLocationType> BuildTypeLegend(List<RoomGraphExportFailure> failures, PreparedGraphEvidence? prepared = null)
    {
        var result = new Dictionary<string, RoomGraphLocationType>(StringComparer.Ordinal)
        {
            ["unknown"] = new(null, "The location has not yet been classified.")
        };
        foreach (var definition in CheckLocationTypeCatalogue.Definitions.OrderBy(x => x.SortOrder)
            .ThenBy(x => CanonicalValue(x.OutputValue), StringComparer.Ordinal)
            .ThenBy(x => x.OutputValue, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            var key = CanonicalValue(definition.OutputValue);
            if (key is null) { Fail($"location type '{definition.Name}' has an empty canonical output key"); continue; }
            if (string.IsNullOrWhiteSpace(definition.Description)) Fail($"location type '{definition.Name}' has a blank description");
            if (!result.TryAdd(key, new(definition.HasPersistedState, definition.Description))) Fail($"location type '{definition.Name}' collides with the reserved or existing key '{key}'");
            void Fail(string cause)
            {
                var failure = new RoomGraphExportFailure(cause);
                failures.Add(failure);
                prepared?.Integrity.Add(new(PreparedIntegrityFamily.Identifiers, failure, $"Application location type: {definition.Name}", "application location type"));
            }
        }
        return result;
    }

    private static TState[] MatchReference<TState>(string? text, IReadOnlyList<TState> candidates, Func<TState, string> reference) =>
        string.IsNullOrWhiteSpace(text) ? [] : candidates.Where(x => string.Equals(text.Trim(), reference(x).Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

    private static TState? Resolve<TState>(List<RoomGraphExportFailure> failures, string? text, Guid? resolvedId, TState[] matches,
        Func<TState, Guid> id, RoomState room, string kind, string child, Guid recordId, string role,
        List<PreparedGraphReference>? evidence, PreparedReferenceKind referenceKind, string? requested, Guid? relatedRoom = null) where TState : class
    {
        if (matches.Length != 1 || resolvedId != id(matches[0]))
        {
            var cause = string.IsNullOrWhiteSpace(text) ? $"{role} reference is blank" : matches.Length == 0 ? $"{role} reference does not resolve to an active exported record: {text}" : matches.Length > 1 ? $"{role} reference is ambiguous: {text}" : $"{role} resolver state is stale or mismatched: {text}";
            ReferenceFailure(failures, room, cause, kind, child, recordId, role, requested, referenceKind, matches.Length switch
            {
                0 when string.IsNullOrWhiteSpace(text) => PreparedReferenceProblem.Blank,
                0 => PreparedReferenceProblem.Missing,
                > 1 => PreparedReferenceProblem.Ambiguous,
                _ => PreparedReferenceProblem.Stale
            }, evidence, relatedRoom);
            return matches.Length == 1 ? matches[0] : null;
        }
        return matches[0];
    }

    private static string? ResolveSubroomNode(List<RoomGraphExportFailure> failures, RoomState room, string? text, Guid? resolvedId, IReadOnlyList<SubroomState> subrooms, string kind, string child, Guid recordId, string role, List<PreparedGraphReference>? evidence, out SubroomState? state)
    {
        var matches = MatchReference(text, subrooms, x => x.Row.ReferenceId);
        state = Resolve(failures, text, resolvedId, matches, x => x.Row.Id, room, kind, child, recordId, role, evidence, PreparedReferenceKind.Node, text);
        if (state is not null && state.Id is null && (kind == "transition" || evidence is not null))
        {
            var cause = $"resolved {role} has no valid export node identity";
            if (kind == "transition") ReferenceFailure(failures, room, cause, kind, child, recordId, role, text, PreparedReferenceKind.Node, PreparedReferenceProblem.InvalidIdentity, evidence);
            else evidence?.Add(new(PreparedReferenceKind.Node, PreparedReferenceProblem.InvalidIdentity, room.Row.Name, kind, child, recordId, role, text, cause, room.Row.Id));
        }
        return state?.Id;
    }

    private static void ReferenceFailure(List<RoomGraphExportFailure> failures, RoomState room, string cause, string kind, string child, Guid recordId, string field,
        string? requested, PreparedReferenceKind referenceKind, PreparedReferenceProblem problem, List<PreparedGraphReference>? evidence, Guid? relatedRoom = null)
    {
        var failure = new RoomGraphExportFailure(cause, room.Id, room.Row.Name, kind, child, room.Row.Id);
        failures.Add(failure);
        evidence?.Add(new(referenceKind, problem, room.Row.Name, kind, child, recordId, field, requested, cause, room.Row.Id, failure,
            relatedRoom is { } id ? [id] : null));
    }

    private static void ValidatePathways(List<RoomGraphExportFailure> failures, RoomState room, IReadOnlyList<ConnectionState> connections, PreparedGraphEvidence? prepared)
    {
        foreach (var group in connections.GroupBy(x => x.Row.Alias.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0) continue;
            var rows = group.ToArray();
            var names = rows.Select(x => x.Row.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var pairs = rows.Where(x => x.Source is not null && x.Target is not null).Select(x => OrderedPair(x.Source!.Row.Id, x.Target!.Row.Id)).Distinct().Count();
            var invalid = names != 1 || pairs > 1 || rows.Length > 2 || rows.Length == 2 && rows[0].Source?.Row.Id == rows[1].Source?.Row.Id && rows[0].Target?.Row.Id == rows[1].Target?.Row.Id;
            if (invalid) Add("connection pathway has conflicting names/endpoints, excess rows, or duplicate direction", group.Key);
        }
        foreach (var group in connections.Where(x => !string.IsNullOrWhiteSpace(x.Row.Name)).GroupBy(x => x.Row.Name.Trim(), StringComparer.OrdinalIgnoreCase))
            if (group.Select(x => x.Row.Alias.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                Add("connection pathway name maps to multiple aliases", group.Key);
        void Add(string cause, string child)
        {
            var failure = new RoomGraphExportFailure(cause, room.Id, room.Row.Name, "connection pathway", child, room.Row.Id);
            failures.Add(failure);
            prepared?.Integrity.Add(new(PreparedIntegrityFamily.Connection, failure, $"Connection pathway: {child}", "connection pathway", RoomId: room.Row.Id));
        }
    }

    private static (Guid, Guid) OrderedPair(Guid a, Guid b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    private static bool IsExactReciprocalTarget(TransitionState source, TransitionState target, IReadOnlyList<RoomState> rooms, IReadOnlyList<TransitionState> sourceRoomTransitions)
    {
        var reverseRooms = string.IsNullOrWhiteSpace(target.Row.DestinationRoomText) ? [] : rooms
            .Where(x => string.Equals(target.Row.DestinationRoomText.Trim(), x.Row.ReferenceId.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var reverseTransitions = string.IsNullOrWhiteSpace(target.Row.DestinationAliasText) ? [] : sourceRoomTransitions
            .Where(x => string.Equals(target.Row.DestinationAliasText.Trim(), x.Row.Alias.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        return reverseRooms.Length == 1 && reverseRooms[0].Row.Id == source.Room.Row.Id &&
            reverseTransitions.Length == 1 && reverseTransitions[0].Row.Id == source.Row.Id &&
            target.Row.ResolvedDestinationRoomId == source.Room.Row.Id &&
            target.Row.ResolvedDestinationTransitionId == source.Row.Id;
    }
    private static void AddRoomFailure(List<RoomGraphExportFailure> failures, RoomState room, string cause, string? kind = null, string? child = null) => failures.Add(new(cause, room.Id, room.Row.Name, kind, child, room.Row.Id));

    private static void Register(string id, string owner, Dictionary<string, string> identities, List<RoomGraphExportFailure> failures, RoomState? room = null, string? kind = null, string? child = null,
        PreparedGraphEvidence? prepared = null, Guid? recordId = null)
    {
        if (identities.TryGetValue(id, out var existing))
        {
            var failure = new RoomGraphExportFailure($"export ID collision '{id}' between {existing} and {owner}", room?.Id, room?.Row.Name, kind, child, room?.Row.Id);
            failures.Add(failure);
            prepared?.Integrity.Add(new(PreparedIntegrityFamily.Identifiers, failure, owner, kind ?? (room is null ? "group" : "room"), recordId, room?.Row.Id));
        }
        else identities.Add(id, owner);
    }

    internal static string? CanonicalValue(string? value) => RoomPresentationRules.CanonicalSegment(value);

    private static string? AliasFailure(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.EnumerateRunes().All(rune => Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber))
            return "must contain only letters and decimal digits";
        return value.Length > SilksongRandoLogicManager.Contracts.RoomAuthoredAlias.MaxLength ? "must be at most three characters long" : null;
    }
    private static string Display(string? primary, string? secondary) => !string.IsNullOrWhiteSpace(primary) ? primary : !string.IsNullOrWhiteSpace(secondary) ? secondary : "unnamed row";
    private static IReadOnlyList<T> Get<T>(IReadOnlyDictionary<Guid, T[]> source, Guid roomId) => source.TryGetValue(roomId, out var values) ? values : [];

    internal static byte[] Serialize<T>(T document, int maxDepth = 0)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NewLine = "\n",
            MaxDepth = maxDepth
        };
        var json = JsonSerializer.Serialize(document, options) + "\n";
        return new UTF8Encoding(false).GetBytes(json);
    }

    internal sealed record Snapshot(IReadOnlyList<GroupRow> Groups, IReadOnlyList<RoomRow> Rooms, IReadOnlyList<SubroomRow> Subrooms, IReadOnlyList<TransitionRow> Transitions, IReadOnlyList<ConnectionRow> Connections, IReadOnlyList<CheckRow> Checks);
    internal sealed record GroupRow(Guid Id, string Name, bool IsVirtual, int SortOrder);
    internal sealed record RoomRow(Guid Id, Guid? GroupId, string ReferenceId, string Name, string? InGameId, string? Contributors, string? Comments, int SortOrder, double MinX, double MinY, double? MaxX, double? MaxY, double? PixelsPerUnitX, double? PixelsPerUnitY);
    internal sealed record SubroomRow(Guid Id, Guid RoomId, string ReferenceId, string Name, string? Notes, int SortOrder, double? X, double? Y, double? Width, double? Height, bool Enabled);
    internal sealed record TransitionRow(Guid Id, Guid RoomId, string Alias, string Name, string? InGameId, string? SourceText, string? DestinationRoomText, string? DestinationAliasText, string Requirements, string Notes, Guid? ResolvedSourceId, Guid? ResolvedDestinationRoomId, Guid? ResolvedDestinationTransitionId, int SortOrder, bool IsTodo, bool? IsVerified, bool IsUnsafeArrival, double? X, double? Y, bool Enabled, double? GameX, double? GameY, double? GameZ);
    internal sealed record ConnectionRow(Guid Id, Guid RoomId, string Alias, string Name, string SourceText, string DestinationText, string Requirements, string Notes, Guid? ResolvedSourceId, Guid? ResolvedDestinationId, int SortOrder, bool IsTodo, bool? IsVerified, double? X, double? Y, bool Enabled);
    internal sealed record CheckRow(Guid Id, Guid RoomId, string Name, string? InGameId, string? SubroomText, string Requirements, string Notes, string? LocationType, Guid? ResolvedSubroomId, int SortOrder, bool IsTodo, bool? IsVerified, double? X, double? Y, bool Enabled, double? GameX, double? GameY, double? GameZ);
    internal sealed record GroupState(GroupRow Row, string? Id);
    internal sealed record Projection(IReadOnlyList<GroupState> Groups, IReadOnlyList<ProjectedRoom> Rooms, IReadOnlyList<RoomGraphExportFailure> Failures, IReadOnlyList<RoomGraphExportWarning> Warnings);
    internal sealed record ProjectedRoom(string Id, RoomRow Row, bool IsMetadataOnly, IReadOnlyList<ProjectedNode> Nodes, IReadOnlyList<ProjectedTransition> Transitions, IReadOnlyList<ProjectedConnection> Connections, IReadOnlyList<ProjectedLocation> Locations);
    internal sealed record ProjectedNode(string Id, string Name, SubroomRow? Row);
    internal sealed record ProjectedTransition(string Id, TransitionRow Row, string SourceNodeId, string? Target, IReadOnlyList<string> Reasons);
    internal sealed record ProjectedConnection(string Id, ConnectionRow Row, string SourceNodeId, string TargetNodeId);
    internal sealed record ProjectedLocation(string Id, CheckRow Row, string Type, bool? HasPersistedState, string NodeId, IReadOnlyList<string> Reasons);
    private sealed record RoomState(RoomRow Row, GroupState? Group, string? Id);
    private sealed record SubroomState(SubroomRow Row, RoomState Room, string? Id);
    private sealed record TransitionState(TransitionRow Row, RoomState Room, string? Id);
    private sealed record LocationState(CheckRow Row, RoomState Room, string? Id, string? Type, bool? HasPersistedState);
    private sealed record ConnectionState(ConnectionRow Row, RoomState Room, string? Id, SubroomState? Source, SubroomState? Target);
    private sealed record SelectedNode(RoomRow Room, string Kind, string Record, Guid RecordId, string Field, string? Requested, string NodeId);
    private sealed record SelectedPort(RoomRow Room, string Record, Guid RecordId, string Requested, Guid TargetRecordId, Guid TargetRoomId);
}
