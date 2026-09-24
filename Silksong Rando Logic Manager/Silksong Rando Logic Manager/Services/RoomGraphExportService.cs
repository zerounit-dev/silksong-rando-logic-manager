using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class RoomGraphExportService(IDbContextFactory<LogicDbContext> contexts) : IRoomGraphExportService
{
    public async Task<RoomGraphExportResult> GenerateAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadSnapshotAsync(cancellationToken);
        return Project(snapshot);
    }

    private async Task<Snapshot> LoadSnapshotAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);

        var groups = await db.RoomGroups.AsNoTracking().Select(x => new GroupRow(x.Id, x.FriendlyName, x.IsVirtual, x.SortOrder)).ToListAsync(token);
        var rooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived)
            .Select(x => new RoomRow(x.Id, x.RoomGroupId, x.ReferenceId, x.FriendlyName, x.InGameId, x.Contributors, x.Comments, x.SortOrder)).ToListAsync(token);
        var roomIds = rooms.Select(x => x.Id).ToArray();
        var subrooms = await db.Subrooms.AsNoTracking().Where(x => !x.IsArchived && roomIds.Contains(x.RoomId))
            .Select(x => new SubroomRow(x.Id, x.RoomId, x.ReferenceId, x.FriendlyName, x.Notes, x.SortOrder)).ToListAsync(token);
        var transitions = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && roomIds.Contains(x.RoomId))
            .Select(x => new TransitionRow(x.Id, x.RoomId, x.Alias, x.FriendlyName, x.InGameId, x.SourceSubroomReferenceText,
                x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.ResolvedSourceSubroomId,
                x.ResolvedDestinationRoomId, x.ResolvedDestinationTransitionId, x.SortOrder, x.IsTodo, x.IsVerified)).ToListAsync(token);
        var connections = await db.SubroomConnections.AsNoTracking().Where(x => !x.IsArchived && roomIds.Contains(x.RoomId))
            .Select(x => new ConnectionRow(x.Id, x.RoomId, x.Alias, x.FriendlyName, x.SourceSubroomReferenceText,
                x.DestinationSubroomReferenceText, x.Requirements, x.Notes, x.ResolvedSourceSubroomId,
                x.ResolvedDestinationSubroomId, x.SortOrder, x.IsTodo, x.IsVerified)).ToListAsync(token);
        var checks = await db.CheckLocations.AsNoTracking().Where(x => !x.IsArchived && roomIds.Contains(x.RoomId))
            .Select(x => new CheckRow(x.Id, x.RoomId, x.FriendlyName, x.InGameId, x.SubroomReferenceText, x.Requirements, x.Notes,
                x.LocationType, x.ResolvedSubroomId, x.SortOrder, x.IsTodo, x.IsVerified)).ToListAsync(token);
        var predicates = await db.RequirementPredicates.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new PredicateRow(x.Id, x.Name, x.Category, x.InputSyntax, x.OutputSyntax, x.Aliases, x.Notes, x.SortOrder)).ToListAsync(token);
        var items = await db.RequirementItems.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new ItemRow(x.Id, x.Name, x.Category, x.OutputValue, x.Aliases, x.Notes, x.SortOrder)).ToListAsync(token);

        await transaction.CommitAsync(token);
        return new(groups, rooms, subrooms, transitions, connections, checks, predicates, items);
    }

    private static RoomGraphExportResult Project(Snapshot snapshot)
    {
        var globalFailures = new List<RoomGraphExportFailure>();
        var roomFailures = new List<RoomGraphExportFailure>();
        var identityOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var typeLegend = BuildTypeLegend(globalFailures);
        var catalogue = BuildCatalogue(snapshot, globalFailures);

        var groupStates = snapshot.Groups
            .Select(group => new GroupState(group, CanonicalValue(group.Name)))
            .OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal).ThenBy(x => x.Row.Id).ToArray();
        foreach (var state in groupStates)
        {
            if (state.Id is null) globalFailures.Add(new($"room group '{state.Row.Name}' does not produce a nonempty canonical export ID"));
            else Register(state.Id, $"room group '{state.Row.Name}'", identityOwners, globalFailures);
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
            if (state.Group is null) Fail(state, "active room is not assigned to a room group");
            if (string.IsNullOrWhiteSpace(state.Row.Name)) Fail(state, "room name is blank");
            if (CanonicalValue(state.Row.ReferenceId) is null) Fail(state, "room reference ID does not produce a nonempty export segment");
            if (state.Id is not null) Register(state.Id, $"room '{state.Row.Name}'", identityOwners, roomFailures, state);
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
                if (string.IsNullOrWhiteSpace(row.Name)) Fail(room, "subroom name is blank", "subroom", Display(row.Name, row.ReferenceId));
                if (segment is null) Fail(room, "subroom reference ID does not produce a nonempty export segment", "subroom", Display(row.Name, row.ReferenceId));
                if (id is not null) Register(id, $"subroom '{Display(row.Name, row.ReferenceId)}'", identityOwners, roomFailures, room, "subroom", Display(row.Name, row.ReferenceId));
            }
            foreach (var row in Get(transitionsByRoom, room.Row.Id).OrderBy(x => x.SortOrder).ThenBy(x => CanonicalValue(x.Alias), StringComparer.Ordinal).ThenBy(x => x.Id))
            {
                var segment = ValidAlias(row.Alias) ? CanonicalValue(row.Alias) : null;
                var id = room.Id is not null && segment is not null ? $"{room.Id}@{segment}" : null;
                var state = new TransitionState(row, room, id);
                transitionStates.Add(row.Id, state);
                if (!ValidAlias(row.Alias)) Fail(room, "transition alias must contain only letters and decimal digits", "transition", Display(row.Name, row.Alias));
                else if (segment is null) Fail(room, "transition alias does not produce a nonempty canonical export segment", "transition", Display(row.Name, row.Alias));
                if (string.IsNullOrWhiteSpace(row.Name)) Fail(room, "transition name is blank", "transition", Display(row.Name, row.Alias));
                if (id is not null) Register(id, $"transition '{Display(row.Name, row.Alias)}'", identityOwners, roomFailures, room, "transition", Display(row.Name, row.Alias));
            }
            foreach (var row in Get(checksByRoom, room.Row.Id).OrderBy(x => x.SortOrder).ThenBy(x => CanonicalValue(x.Name), StringComparer.Ordinal).ThenBy(x => x.Id))
            {
                var definition = row.LocationType is null ? null : CheckLocationTypeCatalogue.Definitions.FirstOrDefault(x => x.OutputValue == row.LocationType);
                var type = row.LocationType is null ? "unknown" : definition is null ? null : CanonicalValue(definition.OutputValue);
                var name = CanonicalValue(row.Name);
                var id = room.Id is not null && type is not null && name is not null ? $"location:{room.Id}::{type}/{name}" : null;
                var state = new LocationState(row, room, id, type, definition?.HasPersistedState);
                locationStates.Add(row.Id, state);
                if (string.IsNullOrWhiteSpace(row.Name)) Fail(room, "location name is blank", "location", Display(row.Name, row.InGameId));
                if (name is null) Fail(room, "location name does not produce a nonempty export segment", "location", Display(row.Name, row.InGameId));
                if (row.LocationType is not null && definition is null) Fail(room, $"location type '{row.LocationType}' is not recognized", "location", Display(row.Name, row.InGameId));
                if (id is not null) Register(id, $"location '{Display(row.Name, row.InGameId)}'", identityOwners, roomFailures, room, "location", Display(row.Name, row.InGameId));
            }
        }

        var translationContext = catalogue is null ? null : new RoomGraphTranslationContext(
            catalogue.Predicates,
            catalogue.Items,
            roomStates.Select(x => new RoomGraphRequirementRoom(x.Row.Id, x.Row.ReferenceId)).ToArray(),
            locationStates.Values.Select(x => new RoomGraphRequirementCheck(x.Row.Id, x.Row.RoomId, x.Row.Name, x.Id)).ToArray());

        var projectedRooms = new List<RoomGraphRoom>();
        foreach (var room in roomStates)
        {
            var roomSubrooms = Get(subroomsByRoom, room.Row.Id).Select(x => subroomStates[x.Id]).ToArray();
            var nodes = roomSubrooms.Length == 0
                ? new[] { new RoomGraphNode(room.Id is null ? "#room" : $"{room.Id}#room", "room", "") }
                : roomSubrooms.Where(x => x.Id is not null).OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal)
                    .Select(x => new RoomGraphNode(x.Id!, x.Row.Name, x.Row.Notes ?? "")).ToArray();
            if (roomSubrooms.Length == 0 && room.Id is not null) Register($"{room.Id}#room", $"synthetic node for room '{room.Row.Name}'", identityOwners, roomFailures, room);

            var projectedTransitions = new List<RoomGraphTransition>();
            foreach (var transition in Get(transitionsByRoom, room.Row.Id).Select(x => transitionStates[x.Id]).OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                var sourceNode = roomSubrooms.Length == 0 ? room.Id is null ? "#room" : $"{room.Id}#room" : ResolveSubroomNode(roomFailures, room, transition.Row.SourceText, transition.Row.ResolvedSourceId, roomSubrooms, "transition", Display(transition.Row.Name, transition.Row.Alias), "source subroom");
                string? target = null;
                var rowReasons = new List<string>();
                var roomBlank = string.IsNullOrWhiteSpace(transition.Row.DestinationRoomText);
                var aliasBlank = string.IsNullOrWhiteSpace(transition.Row.DestinationAliasText);
                if (roomBlank && aliasBlank) rowReasons.Add("destination has not been mapped");
                else if (roomBlank != aliasBlank) Fail(room, "transition destination room and alias must either both be blank or both be present", "transition", Display(transition.Row.Name, transition.Row.Alias));
                else
                {
                    var targetRoom = Resolve(roomFailures, transition.Row.DestinationRoomText, transition.Row.ResolvedDestinationRoomId, roomStates,
                        x => x.Row.ReferenceId, x => x.Row.Id, room, "transition", Display(transition.Row.Name, transition.Row.Alias), "destination room");
                    if (targetRoom is not null)
                    {
                        var targets = Get(transitionsByRoom, targetRoom.Row.Id).Select(x => transitionStates[x.Id]).ToArray();
                        var targetTransition = Resolve(roomFailures, transition.Row.DestinationAliasText, transition.Row.ResolvedDestinationTransitionId, targets,
                            x => x.Row.Alias, x => x.Row.Id, room, "transition", Display(transition.Row.Name, transition.Row.Alias), "destination transition");
                        target = targetTransition?.Id;
                        if (targetTransition is not null && target is null) Fail(room, "resolved destination transition has no valid export identity", "transition", Display(transition.Row.Name, transition.Row.Alias));
                        if (targetTransition is not null && !IsExactReciprocalTarget(transition, targetTransition, roomStates,
                            Get(transitionsByRoom, transition.Room.Row.Id).Select(x => transitionStates[x.Id]).ToArray()))
                            Fail(room, $"selected destination transition '{Display(targetTransition.Row.Name, targetTransition.Row.Alias)}' does not resolve back to this exact source transition", "transition", Display(transition.Row.Name, transition.Row.Alias));
                    }
                }
                var requirement = Translate(transition.Row.Requirements, room.Row.Id, translationContext);
                if (requirement.Mode == "unresolved") rowReasons.Add("requirement is unresolved");
                projectedTransitions.Add(new(transition.Id ?? "", transition.Row.Alias, transition.Row.Name, transition.Row.InGameId,
                    sourceNode ?? "", target, requirement, transition.Row.Notes, transition.Row.IsTodo, transition.Row.IsVerified,
                    rowReasons.Count == 0 ? "active" : "unresolved", rowReasons));
            }

            var roomConnectionStates = new List<ConnectionState>();
            foreach (var row in Get(connectionsByRoom, room.Row.Id).OrderBy(x => x.SortOrder)
                .ThenBy(x => CanonicalValue(x.SourceText), StringComparer.Ordinal).ThenBy(x => CanonicalValue(x.DestinationText), StringComparer.Ordinal)
                .ThenBy(x => CanonicalValue(x.Alias), StringComparer.Ordinal).ThenBy(x => x.Id))
            {
                var source = ResolveSubroom(roomFailures, room, row.SourceText, row.ResolvedSourceId, roomSubrooms, "connection", Display(row.Name, row.Alias), "source subroom");
                var target = ResolveSubroom(roomFailures, room, row.DestinationText, row.ResolvedDestinationId, roomSubrooms, "connection", Display(row.Name, row.Alias), "destination subroom");
                if (roomSubrooms.Length == 0) Fail(room, "an active connection cannot exist in a room without active subrooms", "connection", Display(row.Name, row.Alias));
                if (source is not null && target is not null && source.Row.Id == target.Row.Id) Fail(room, "connection source and destination form a self-loop", "connection", Display(row.Name, row.Alias));
                var alias = ValidAlias(row.Alias) ? CanonicalValue(row.Alias) : null;
                if (!ValidAlias(row.Alias)) Fail(room, "connection alias must contain only letters and decimal digits", "connection", Display(row.Name, row.Alias));
                else if (alias is null) Fail(room, "connection alias does not produce a nonempty canonical export segment", "connection", Display(row.Name, row.Alias));
                if (string.IsNullOrWhiteSpace(row.Name)) Fail(room, "connection name is blank", "connection", Display(row.Name, row.Alias));
                var id = room.Id is not null && source?.Id is not null && target?.Id is not null && alias is not null
                    ? $"{room.Id}#{CanonicalValue(source.Row.ReferenceId)}>{CanonicalValue(target.Row.ReferenceId)}@{alias}" : null;
                var state = new ConnectionState(row, room, id, source, target);
                roomConnectionStates.Add(state);
                if (id is not null) Register(id, $"connection '{Display(row.Name, row.Alias)}'", identityOwners, roomFailures, room, "connection", Display(row.Name, row.Alias));
            }
            ValidatePathways(roomFailures, room, roomConnectionStates);
            var projectedConnections = roomConnectionStates.OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal).Select(connection =>
            {
                var requirement = Translate(connection.Row.Requirements, room.Row.Id, translationContext);
                var reasons = requirement.Mode == "unresolved" ? new[] { "requirement is unresolved" } : [];
                return new RoomGraphConnection(connection.Id ?? "", connection.Row.Alias, connection.Row.Name, connection.Source?.Id ?? "", connection.Target?.Id ?? "",
                    requirement, connection.Row.Notes, connection.Row.IsTodo, connection.Row.IsVerified, reasons.Length == 0 ? "active" : "unresolved", reasons);
            }).ToArray();

            var projectedLocations = Get(checksByRoom, room.Row.Id).Select(x => locationStates[x.Id]).OrderBy(x => x.Row.SortOrder).ThenBy(x => x.Id, StringComparer.Ordinal).Select(location =>
            {
                var node = roomSubrooms.Length == 0 ? room.Id is null ? "#room" : $"{room.Id}#room" : ResolveSubroomNode(roomFailures, room, location.Row.SubroomText, location.Row.ResolvedSubroomId, roomSubrooms, "location", Display(location.Row.Name, location.Row.InGameId), "subroom");
                var requirement = Translate(location.Row.Requirements, room.Row.Id, translationContext);
                var reasons = requirement.Mode == "unresolved" ? new[] { "requirement is unresolved" } : [];
                return new RoomGraphLocation(location.Id ?? "", location.Row.Name, location.Row.InGameId, location.Type ?? "unknown", location.HasPersistedState,
                    node ?? "", requirement, location.Row.Notes, location.Row.IsTodo, location.Row.IsVerified, reasons.Length == 0 ? "active" : "unresolved", reasons);
            }).ToArray();

            projectedRooms.Add(new(room.Id ?? "", room.Row.Id.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant(), room.Group?.Id ?? "", room.Row.Name,
                room.Row.InGameId, room.Row.Contributors ?? "", room.Row.Comments ?? "", nodes, projectedTransitions, projectedConnections, projectedLocations));
        }

        var roomOrder = roomStates.Select((room, index) => (room.Row.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var failures = globalFailures.Concat(roomFailures.OrderBy(x => x.RoomRecordId is { } id && roomOrder.TryGetValue(id, out var index) ? index : int.MaxValue)).ToArray();
        if (failures.Length != 0) return RoomGraphExportResult.Failed(failures);

        var areaIds = groupStates.Select(x => x.Id!).ToArray();
        var document = new RoomGraphDocument(3, new("silksong-logic-manager-export", projectedRooms.Count), areaIds,
            groupStates.Where(x => !x.Row.IsVirtual).Select(x => x.Id!).ToArray(),
            groupStates.Where(x => x.Row.IsVirtual).Select(x => x.Id!).ToArray(),
            snapshot.Predicates.Select(x => new RoomGraphTranslationPredicate(x.Name, x.Category, x.InputSyntax, x.OutputSyntax, RequirementCatalogueLanguage.ParseAliases(x.Aliases), x.Notes)).ToArray(),
            snapshot.Items.Select(x => new RoomGraphTranslationItem(x.Name, x.Category, RequirementCatalogueLanguage.ParseAliases(x.Aliases), x.OutputValue, x.Notes)).ToArray(),
            typeLegend, projectedRooms);
        return RoomGraphExportResult.Success(Serialize(document));

        void Fail(RoomState room, string cause, string? kind = null, string? child = null) => roomFailures.Add(new(cause, room.Id, room.Row.Name, kind, child, room.Row.Id));
    }

    private static Catalogue? BuildCatalogue(Snapshot snapshot, List<RoomGraphExportFailure> failures)
    {
        var initialFailureCount = failures.Count;
        var predicateInputs = new List<RequirementCataloguePredicateValidation>();
        var predicates = new List<RoomGraphPredicateDefinition>();
        foreach (var row in snapshot.Predicates)
        {
            if (!RequirementCatalogueLanguage.TryParsePersistedValue(row.InputSyntax, out var syntax)) syntax = (RequirementInputSyntax)(-1);
            predicateInputs.Add(new(row.Id, row.Name, row.Category, syntax, row.OutputSyntax, row.Notes, row.Aliases));
            if (Enum.IsDefined(syntax) && RequirementCatalogueLanguage.TryCanonicalizeAliases(row.Aliases, out var canonical, out var aliases, out _) && canonical == row.Aliases)
                predicates.Add(new(row.Id, row.Name, syntax, row.OutputSyntax, aliases));
        }
        var itemInputs = snapshot.Items.Select(row => new RequirementCatalogueItemValidation(row.Id, row.Name, row.Category, row.OutputValue, row.Notes, row.Aliases)).ToArray();
        var items = snapshot.Items.Where(row => RequirementCatalogueLanguage.TryCanonicalizeAliases(row.Aliases, out var canonical, out _, out _) && canonical == row.Aliases)
            .Select(row => new RoomGraphItemDefinition(row.Id, row.Name, row.OutputValue, RequirementCatalogueLanguage.ParseAliases(row.Aliases))).ToArray();
        foreach (var issue in RequirementCatalogueValidator.Validate(predicateInputs, itemInputs))
            failures.Add(new($"requirement catalogue definition '{issue.DefinitionName ?? issue.DefinitionId.ToString()}' is invalid: {issue.Message}"));
        foreach (var row in snapshot.Predicates)
            if (RequirementCatalogueLanguage.TryCanonicalizeAliases(row.Aliases, out var canonical, out _, out _) && canonical != row.Aliases)
                failures.Add(new($"requirement predicate '{row.Name}' has noncanonical aliases; apply the definition again before export"));
        foreach (var row in snapshot.Items)
            if (RequirementCatalogueLanguage.TryCanonicalizeAliases(row.Aliases, out var canonical, out _, out _) && canonical != row.Aliases)
                failures.Add(new($"requirement item '{row.Name}' has noncanonical aliases; apply the definition again before export"));

        var invalidCheckOutputs = predicates.Where(x => x.Syntax == RequirementInputSyntax.PredicateThenCheck).Where(predicate =>
        {
            try
            {
                const string marker = "location:room::type/name";
                return RequirementAtomFormatter.Format(predicate.Syntax, predicate.OutputSyntax, new(CheckGraphId: marker)) != $"dependency:{marker}";
            }
            catch { return true; }
        }).Select(x => x.Name).ToArray();
        if (invalidCheckOutputs.Length != 0)
            failures.Add(new($"check-consuming requirement predicate output must emit exactly 'dependency:{{check}}'; correct: {string.Join(", ", invalidCheckOutputs)}"));
        return failures.Count == initialFailureCount ? new(predicates, items) : null;
    }

    private static Dictionary<string, RoomGraphLocationType> BuildTypeLegend(List<RoomGraphExportFailure> failures)
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
            if (key is null) { failures.Add(new($"location type '{definition.Name}' has an empty canonical output key")); continue; }
            if (string.IsNullOrWhiteSpace(definition.Description)) failures.Add(new($"location type '{definition.Name}' has a blank description"));
            if (!result.TryAdd(key, new(definition.HasPersistedState, definition.Description))) failures.Add(new($"location type '{definition.Name}' collides with the reserved or existing key '{key}'"));
        }
        return result;
    }

    private static RoomGraphRequirement Translate(string raw, Guid roomId, RoomGraphTranslationContext? context) => context is null
        ? new(raw, "unresolved", [], ["requirement translation is unavailable because the shared catalogue configuration is invalid"])
        : RoomGraphRequirementTranslator.Translate(raw, roomId, context);

    private static TState? Resolve<TState>(List<RoomGraphExportFailure> failures, string? text, Guid? resolvedId, IReadOnlyList<TState> candidates, Func<TState, string> reference,
        Func<TState, Guid> id, RoomState room, string kind, string child, string role) where TState : class
    {
        TState[] matches = string.IsNullOrWhiteSpace(text) ? [] : candidates.Where(x => string.Equals(text.Trim(), reference(x).Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || resolvedId != id(matches[0]))
        {
            var cause = string.IsNullOrWhiteSpace(text) ? $"{role} reference is blank" : matches.Length == 0 ? $"{role} reference does not resolve to an active exported record: {text}" : matches.Length > 1 ? $"{role} reference is ambiguous: {text}" : $"{role} resolver state is stale or mismatched: {text}";
            AddRoomFailure(failures, room, cause, kind, child);
            return matches.Length == 1 ? matches[0] : null;
        }
        return matches[0];
    }

    private static SubroomState? ResolveSubroom(List<RoomGraphExportFailure> failures, RoomState room, string? text, Guid? resolvedId, IReadOnlyList<SubroomState> subrooms, string kind, string child, string role) =>
        Resolve(failures, text, resolvedId, subrooms, x => x.Row.ReferenceId, x => x.Row.Id, room, kind, child, role);
    private static string? ResolveSubroomNode(List<RoomGraphExportFailure> failures, RoomState room, string? text, Guid? resolvedId, IReadOnlyList<SubroomState> subrooms, string kind, string child, string role)
    {
        var state = ResolveSubroom(failures, room, text, resolvedId, subrooms, kind, child, role);
        if (state is not null && state.Id is null) AddRoomFailure(failures, room, $"resolved {role} has no valid export node identity", kind, child);
        return state?.Id;
    }

    private static void ValidatePathways(List<RoomGraphExportFailure> failures, RoomState room, IReadOnlyList<ConnectionState> connections)
    {
        foreach (var group in connections.GroupBy(x => x.Row.Alias.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0) continue;
            var rows = group.ToArray();
            var names = rows.Select(x => x.Row.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var pairs = rows.Where(x => x.Source is not null && x.Target is not null).Select(x => OrderedPair(x.Source!.Row.Id, x.Target!.Row.Id)).Distinct().Count();
            var invalid = names != 1 || pairs > 1 || rows.Length > 2 || rows.Length == 2 && rows[0].Source?.Row.Id == rows[1].Source?.Row.Id && rows[0].Target?.Row.Id == rows[1].Target?.Row.Id;
            if (invalid) AddRoomFailure(failures, room, "connection pathway has conflicting names/endpoints, excess rows, or duplicate direction", "connection pathway", group.Key);
        }
        foreach (var group in connections.Where(x => !string.IsNullOrWhiteSpace(x.Row.Name)).GroupBy(x => x.Row.Name.Trim(), StringComparer.OrdinalIgnoreCase))
            if (group.Select(x => x.Row.Alias.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                AddRoomFailure(failures, room, "connection pathway name maps to multiple aliases", "connection pathway", group.Key);
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

    private static void Register(string id, string owner, Dictionary<string, string> identities, List<RoomGraphExportFailure> failures, RoomState? room = null, string? kind = null, string? child = null)
    {
        if (identities.TryGetValue(id, out var existing)) failures.Add(new($"export ID collision '{id}' between {existing} and {owner}", room?.Id, room?.Row.Name, kind, child, room?.Row.Id));
        else identities.Add(id, owner);
    }

    internal static string? CanonicalValue(string? value)
    {
        if (value is null) return null;
        var output = new StringBuilder();
        var pendingHyphen = false;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            var lowered = Rune.ToLowerInvariant(rune);
            if (lowered.IsAscii && (lowered.Value is >= 'a' and <= 'z' or >= '0' and <= '9'))
            {
                if (pendingHyphen && output.Length != 0) output.Append('-');
                output.Append((char)lowered.Value);
                pendingHyphen = false;
            }
            else pendingHyphen = output.Length != 0;
        }
        return output.Length == 0 ? null : output.ToString();
    }

    private static bool ValidAlias(string? value) => !string.IsNullOrWhiteSpace(value) && value.EnumerateRunes().All(rune => Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber);
    private static string Display(string? primary, string? secondary) => !string.IsNullOrWhiteSpace(primary) ? primary : !string.IsNullOrWhiteSpace(secondary) ? secondary : "unnamed row";
    private static IReadOnlyList<T> Get<T>(IReadOnlyDictionary<Guid, T[]> source, Guid roomId) => source.TryGetValue(roomId, out var values) ? values : [];

    private static byte[] Serialize(RoomGraphDocument document)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NewLine = "\n"
        };
        var json = JsonSerializer.Serialize(document, options) + "\n";
        return new UTF8Encoding(false).GetBytes(json);
    }

    private sealed record Catalogue(IReadOnlyList<RoomGraphPredicateDefinition> Predicates, IReadOnlyList<RoomGraphItemDefinition> Items);
    private sealed record Snapshot(IReadOnlyList<GroupRow> Groups, IReadOnlyList<RoomRow> Rooms, IReadOnlyList<SubroomRow> Subrooms, IReadOnlyList<TransitionRow> Transitions, IReadOnlyList<ConnectionRow> Connections, IReadOnlyList<CheckRow> Checks, IReadOnlyList<PredicateRow> Predicates, IReadOnlyList<ItemRow> Items);
    private sealed record GroupRow(Guid Id, string Name, bool IsVirtual, int SortOrder);
    private sealed record RoomRow(Guid Id, Guid? GroupId, string ReferenceId, string Name, string? InGameId, string? Contributors, string? Comments, int SortOrder);
    private sealed record SubroomRow(Guid Id, Guid RoomId, string ReferenceId, string Name, string? Notes, int SortOrder);
    private sealed record TransitionRow(Guid Id, Guid RoomId, string Alias, string Name, string? InGameId, string? SourceText, string? DestinationRoomText, string? DestinationAliasText, string Requirements, string Notes, Guid? ResolvedSourceId, Guid? ResolvedDestinationRoomId, Guid? ResolvedDestinationTransitionId, int SortOrder, bool IsTodo, bool? IsVerified);
    private sealed record ConnectionRow(Guid Id, Guid RoomId, string Alias, string Name, string SourceText, string DestinationText, string Requirements, string Notes, Guid? ResolvedSourceId, Guid? ResolvedDestinationId, int SortOrder, bool IsTodo, bool? IsVerified);
    private sealed record CheckRow(Guid Id, Guid RoomId, string Name, string? InGameId, string? SubroomText, string Requirements, string Notes, string? LocationType, Guid? ResolvedSubroomId, int SortOrder, bool IsTodo, bool? IsVerified);
    private sealed record PredicateRow(Guid Id, string Name, string? Category, string InputSyntax, string OutputSyntax, string Aliases, string Notes, int SortOrder);
    private sealed record ItemRow(Guid Id, string Name, string? Category, string OutputValue, string Aliases, string Notes, int SortOrder);
    private sealed record GroupState(GroupRow Row, string? Id);
    private sealed record RoomState(RoomRow Row, GroupState? Group, string? Id);
    private sealed record SubroomState(SubroomRow Row, RoomState Room, string? Id);
    private sealed record TransitionState(TransitionRow Row, RoomState Room, string? Id);
    private sealed record LocationState(CheckRow Row, RoomState Room, string? Id, string? Type, bool? HasPersistedState);
    private sealed record ConnectionState(ConnectionRow Row, RoomState Room, string? Id, SubroomState? Source, SubroomState? Target);
}
