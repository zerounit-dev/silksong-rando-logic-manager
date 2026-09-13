using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class LogicReferenceResolver(LogicDbContext dbContext, ScopedResolverTrace? trace = null)
{
    public async Task ResolveScopedAsync(AuditedEntity changedEntity, IReadOnlySet<string> changedProperties, IReadOnlyDictionary<string, string?> originalTextValues, CancellationToken cancellationToken = default)
    {
        switch (changedEntity)
        {
            case Room room when changedProperties.Contains(nameof(Room.ReferenceId)):
                await ResolveRoomReferenceChangeAsync(room, originalTextValues[nameof(Room.ReferenceId)], cancellationToken);
                break;
            case Subroom subroom when changedProperties.Contains(nameof(Subroom.ReferenceId)):
                await ResolveSubroomReferenceChangeAsync(subroom, originalTextValues[nameof(Subroom.ReferenceId)], cancellationToken);
                break;
            case RoomTransition transition:
                await ResolveTransitionAsync(transition, cancellationToken);
                if (changedProperties.Contains(nameof(RoomTransition.Alias))) await ResolveInboundTransitionAliasesAsync(transition, originalTextValues[nameof(RoomTransition.Alias)], cancellationToken);
                break;
            case SubroomConnection connection:
                await ResolveConnectionAsync(connection, cancellationToken);
                break;
            case CheckLocation check:
                await ResolveCheckAsync(check, cancellationToken);
                break;
            case RoomGroup group when changedProperties.Contains(nameof(RoomGroup.ZoneReferenceText)):
                var zones = await dbContext.MapZones.ToListAsync(cancellationToken);
                group.ResolvedMapZoneId = ResolveMapZone(group.ZoneReferenceText, group.ResolvedMapZoneId, zones).ResolvedId;
                break;
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            if (trace is null) await dbContext.SaveChangesAsync(cancellationToken);
            else await trace.MeasureAsync("resolver SaveChanges", () => dbContext.SaveChangesAsync(cancellationToken));
        }
    }

    public async Task ResolveCreatedAsync(AuditedEntity createdEntity, CancellationToken cancellationToken = default)
    {
        switch (createdEntity)
        {
            case Room room:
                await ResolveRoomReferenceChangeAsync(room, null, cancellationToken);
                break;
            case Subroom subroom:
                await ResolveSubroomReferenceChangeAsync(subroom, null, cancellationToken);
                break;
            case RoomTransition transition:
                await ResolveTransitionAsync(transition, cancellationToken);
                await ResolveInboundTransitionAliasesAsync(transition, null, cancellationToken);
                break;
            case SubroomConnection connection:
                await ResolveConnectionAsync(connection, cancellationToken);
                break;
            case CheckLocation check:
                await ResolveCheckAsync(check, cancellationToken);
                break;
        }

        if (dbContext.ChangeTracker.HasChanges()) await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<LogicResolutionReport> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var rooms = await dbContext.Rooms.ToListAsync(cancellationToken);
        var subrooms = await dbContext.Subrooms.ToListAsync(cancellationToken);
        var transitions = await dbContext.RoomTransitions.ToListAsync(cancellationToken);
        var connections = await dbContext.SubroomConnections.ToListAsync(cancellationToken);
        var checks = await dbContext.CheckLocations.ToListAsync(cancellationToken);
        var mapScenes = await dbContext.MapScenes.ToListAsync(cancellationToken);
        var roomGroups = await dbContext.RoomGroups.ToListAsync(cancellationToken);
        var mapZones = await dbContext.MapZones.ToListAsync(cancellationToken);

        foreach (var transition in transitions)
        {
            transition.ResolvedSourceSubroomId = Resolve(transition.SourceSubroomReferenceText, transition.ResolvedSourceSubroomId, subrooms.Where(x => x.RoomId == transition.RoomId)).ResolvedId;
            var destinationRoom = Resolve(transition.DestinationRoomReferenceText, transition.ResolvedDestinationRoomId, rooms);
            transition.ResolvedDestinationRoomId = destinationRoom.ResolvedId;
            transition.ResolvedDestinationTransitionId = Resolve(transition.DestinationTransitionAliasText, transition.ResolvedDestinationTransitionId, transitions.Where(x => x.RoomId == destinationRoom.ResolvedId)).ResolvedId;
        }

        foreach (var connection in connections)
        {
            var roomSubrooms = subrooms.Where(x => x.RoomId == connection.RoomId);
            connection.ResolvedSourceSubroomId = Resolve(connection.SourceSubroomReferenceText, connection.ResolvedSourceSubroomId, roomSubrooms).ResolvedId;
            connection.ResolvedDestinationSubroomId = Resolve(connection.DestinationSubroomReferenceText, connection.ResolvedDestinationSubroomId, roomSubrooms).ResolvedId;
        }

        foreach (var check in checks)
        {
            check.ResolvedSubroomId = Resolve(check.SubroomReferenceText, check.ResolvedSubroomId, subrooms.Where(x => x.RoomId == check.RoomId)).ResolvedId;
        }

        foreach (var mapScene in mapScenes)
        {
            mapScene.ResolvedRoomId = Resolve(mapScene.RoomReferenceText, mapScene.ResolvedRoomId, rooms).ResolvedId;
        }

        foreach (var roomGroup in roomGroups)
        {
            roomGroup.ResolvedMapZoneId = ResolveMapZone(roomGroup.ZoneReferenceText, roomGroup.ResolvedMapZoneId, mapZones).ResolvedId;
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return CreateReport(rooms, subrooms, transitions, connections, checks, mapScenes, roomGroups, mapZones);
    }

    private async Task ResolveRoomReferenceChangeAsync(Room room, string? oldReferenceId, CancellationToken cancellationToken)
    {
        var rooms = await dbContext.Rooms.ToListAsync(cancellationToken);
        var transitions = await dbContext.RoomTransitions.Where(item => item.DestinationRoomReferenceText != null).ToListAsync(cancellationToken);
        var mapScenes = await dbContext.MapScenes.Where(item => item.RoomReferenceText != null).ToListAsync(cancellationToken);
        foreach (var transition in transitions.Where(item => MatchesEither(item.DestinationRoomReferenceText, oldReferenceId, room.ReferenceId))) await ResolveTransitionDestinationAsync(transition, rooms, cancellationToken);
        foreach (var scene in mapScenes.Where(item => MatchesEither(item.RoomReferenceText, oldReferenceId, room.ReferenceId))) scene.ResolvedRoomId = Resolve(scene.RoomReferenceText, scene.ResolvedRoomId, rooms).ResolvedId;
    }

    private async Task ResolveSubroomReferenceChangeAsync(Subroom subroom, string? oldReferenceId, CancellationToken cancellationToken)
    {
        var subrooms = trace is null ? await dbContext.Subrooms.Where(item => item.RoomId == subroom.RoomId).ToListAsync(cancellationToken) : await trace.MeasureAsync("target subroom query", () => dbContext.Subrooms.Where(item => item.RoomId == subroom.RoomId).ToListAsync(cancellationToken));
        var transitions = trace is null ? await dbContext.RoomTransitions.Where(item => item.RoomId == subroom.RoomId && item.SourceSubroomReferenceText != null).ToListAsync(cancellationToken) : await trace.MeasureAsync("transition source-reference query", () => dbContext.RoomTransitions.Where(item => item.RoomId == subroom.RoomId && item.SourceSubroomReferenceText != null).ToListAsync(cancellationToken));
        var connections = trace is null ? await dbContext.SubroomConnections.Where(item => item.RoomId == subroom.RoomId && (item.SourceSubroomReferenceText != null || item.DestinationSubroomReferenceText != null)).ToListAsync(cancellationToken) : await trace.MeasureAsync("connection endpoint-reference query", () => dbContext.SubroomConnections.Where(item => item.RoomId == subroom.RoomId && (item.SourceSubroomReferenceText != null || item.DestinationSubroomReferenceText != null)).ToListAsync(cancellationToken));
        var checks = trace is null ? await dbContext.CheckLocations.Where(item => item.RoomId == subroom.RoomId && item.SubroomReferenceText != null).ToListAsync(cancellationToken) : await trace.MeasureAsync("check subroom-reference query", () => dbContext.CheckLocations.Where(item => item.RoomId == subroom.RoomId && item.SubroomReferenceText != null).ToListAsync(cancellationToken));

        void AssignResolvedIds()
        {
        foreach (var transition in transitions.Where(item => MatchesEither(item.SourceSubroomReferenceText, oldReferenceId, subroom.ReferenceId))) transition.ResolvedSourceSubroomId = Resolve(transition.SourceSubroomReferenceText, transition.ResolvedSourceSubroomId, subrooms).ResolvedId;
        foreach (var connection in connections)
        {
            if (MatchesEither(connection.SourceSubroomReferenceText, oldReferenceId, subroom.ReferenceId)) connection.ResolvedSourceSubroomId = Resolve(connection.SourceSubroomReferenceText, connection.ResolvedSourceSubroomId, subrooms).ResolvedId;
            if (MatchesEither(connection.DestinationSubroomReferenceText, oldReferenceId, subroom.ReferenceId)) connection.ResolvedDestinationSubroomId = Resolve(connection.DestinationSubroomReferenceText, connection.ResolvedDestinationSubroomId, subrooms).ResolvedId;
        }
        foreach (var check in checks.Where(item => MatchesEither(item.SubroomReferenceText, oldReferenceId, subroom.ReferenceId))) check.ResolvedSubroomId = Resolve(check.SubroomReferenceText, check.ResolvedSubroomId, subrooms).ResolvedId;
        }

        if (trace is null) AssignResolvedIds();
        else trace.Measure("in-memory matching/resolver-ID assignment", AssignResolvedIds);
    }

    private async Task ResolveTransitionAsync(RoomTransition transition, CancellationToken cancellationToken)
    {
        var subrooms = await dbContext.Subrooms.Where(item => item.RoomId == transition.RoomId).ToListAsync(cancellationToken);
        transition.ResolvedSourceSubroomId = Resolve(transition.SourceSubroomReferenceText, transition.ResolvedSourceSubroomId, subrooms).ResolvedId;
        var rooms = await dbContext.Rooms.ToListAsync(cancellationToken);
        await ResolveTransitionDestinationAsync(transition, rooms, cancellationToken);
    }

    private async Task ResolveInboundTransitionAliasesAsync(RoomTransition transition, string? oldAlias, CancellationToken cancellationToken)
    {
        var room = await dbContext.Rooms.SingleAsync(item => item.Id == transition.RoomId, cancellationToken);
        var candidates = await dbContext.RoomTransitions.Where(item => item.ResolvedDestinationRoomId == transition.RoomId || item.DestinationRoomReferenceText != null).ToListAsync(cancellationToken);
        var targets = await dbContext.RoomTransitions.Where(item => item.RoomId == transition.RoomId).ToListAsync(cancellationToken);
        foreach (var candidate in candidates.Where(item => MatchesRoom(item.DestinationRoomReferenceText, room) && MatchesEither(item.DestinationTransitionAliasText, oldAlias, transition.Alias)))
        {
            candidate.ResolvedDestinationTransitionId = Resolve(candidate.DestinationTransitionAliasText, candidate.ResolvedDestinationTransitionId, targets).ResolvedId;
        }
    }

    private async Task ResolveConnectionAsync(SubroomConnection connection, CancellationToken cancellationToken)
    {
        var subrooms = await dbContext.Subrooms.Where(item => item.RoomId == connection.RoomId).ToListAsync(cancellationToken);
        connection.ResolvedSourceSubroomId = Resolve(connection.SourceSubroomReferenceText, connection.ResolvedSourceSubroomId, subrooms).ResolvedId;
        connection.ResolvedDestinationSubroomId = Resolve(connection.DestinationSubroomReferenceText, connection.ResolvedDestinationSubroomId, subrooms).ResolvedId;
    }

    private async Task ResolveCheckAsync(CheckLocation check, CancellationToken cancellationToken)
    {
        var subrooms = await dbContext.Subrooms.Where(item => item.RoomId == check.RoomId).ToListAsync(cancellationToken);
        check.ResolvedSubroomId = Resolve(check.SubroomReferenceText, check.ResolvedSubroomId, subrooms).ResolvedId;
    }

    private async Task ResolveTransitionDestinationAsync(RoomTransition transition, IReadOnlyCollection<Room> rooms, CancellationToken cancellationToken)
    {
        var destinationRoom = Resolve(transition.DestinationRoomReferenceText, transition.ResolvedDestinationRoomId, rooms);
        transition.ResolvedDestinationRoomId = destinationRoom.ResolvedId;
        var targets = destinationRoom.ResolvedId is { } roomId
            ? await dbContext.RoomTransitions.Where(item => item.RoomId == roomId).ToListAsync(cancellationToken)
            : [];
        transition.ResolvedDestinationTransitionId = Resolve(transition.DestinationTransitionAliasText, transition.ResolvedDestinationTransitionId, targets).ResolvedId;
    }

    private static bool MatchesEither(string? value, string? oldValue, string newValue) => TextMatches(value, oldValue ?? string.Empty) || TextMatches(value, newValue);
    private static bool MatchesRoom(string? referenceText, Room room) => TextMatches(referenceText, room.ReferenceId);

    public async Task<LogicResolutionReport> GetResolutionReportAsync(CancellationToken cancellationToken = default)
    {
        var rooms = await dbContext.Rooms.AsNoTracking().ToListAsync(cancellationToken);
        var subrooms = await dbContext.Subrooms.AsNoTracking().ToListAsync(cancellationToken);
        var transitions = await dbContext.RoomTransitions.AsNoTracking().ToListAsync(cancellationToken);
        var connections = await dbContext.SubroomConnections.AsNoTracking().ToListAsync(cancellationToken);
        var checks = await dbContext.CheckLocations.AsNoTracking().ToListAsync(cancellationToken);
        var mapScenes = await dbContext.MapScenes.AsNoTracking().ToListAsync(cancellationToken);
        var roomGroups = await dbContext.RoomGroups.AsNoTracking().ToListAsync(cancellationToken);
        var mapZones = await dbContext.MapZones.AsNoTracking().ToListAsync(cancellationToken);

        return CreateReport(rooms, subrooms, transitions, connections, checks, mapScenes, roomGroups, mapZones);
    }

    private static LogicResolutionReport CreateReport(
        IReadOnlyCollection<Room> rooms,
        IReadOnlyCollection<Subroom> subrooms,
        IReadOnlyCollection<RoomTransition> transitions,
        IReadOnlyCollection<SubroomConnection> connections,
        IReadOnlyCollection<CheckLocation> checks,
        IReadOnlyCollection<MapScene> mapScenes,
        IReadOnlyCollection<RoomGroup> roomGroups,
        IReadOnlyCollection<MapZone> mapZones)
    {
        var references = new List<ReferenceResolution>();

        foreach (var transition in transitions)
        {
            AddIfAuthored(references, Create("RoomTransition", transition.Id, nameof(transition.SourceSubroomReferenceText), transition.SourceSubroomReferenceText, transition.ResolvedSourceSubroomId, subrooms.Where(x => x.RoomId == transition.RoomId)), transition.SourceSubroomReferenceText);
            var destinationRoom = Resolve(transition.DestinationRoomReferenceText, transition.ResolvedDestinationRoomId, rooms);
            AddIfAuthored(references, new ReferenceResolution("RoomTransition", transition.Id, nameof(transition.DestinationRoomReferenceText), destinationRoom.Status, transition.ResolvedDestinationRoomId), transition.DestinationRoomReferenceText);
            AddIfAuthored(references, Create("RoomTransition", transition.Id, nameof(transition.DestinationTransitionAliasText), transition.DestinationTransitionAliasText, transition.ResolvedDestinationTransitionId, transitions.Where(x => x.RoomId == destinationRoom.ResolvedId)), transition.DestinationTransitionAliasText);
        }

        foreach (var connection in connections)
        {
            var roomSubrooms = subrooms.Where(x => x.RoomId == connection.RoomId);
            AddIfAuthored(references, Create("SubroomConnection", connection.Id, nameof(connection.SourceSubroomReferenceText), connection.SourceSubroomReferenceText, connection.ResolvedSourceSubroomId, roomSubrooms), connection.SourceSubroomReferenceText);
            AddIfAuthored(references, Create("SubroomConnection", connection.Id, nameof(connection.DestinationSubroomReferenceText), connection.DestinationSubroomReferenceText, connection.ResolvedDestinationSubroomId, roomSubrooms), connection.DestinationSubroomReferenceText);
        }

        foreach (var check in checks)
        {
            AddIfAuthored(references, Create("CheckLocation", check.Id, nameof(check.SubroomReferenceText), check.SubroomReferenceText, check.ResolvedSubroomId, subrooms.Where(x => x.RoomId == check.RoomId)), check.SubroomReferenceText);
        }

        foreach (var mapScene in mapScenes)
        {
            var resolution = Resolve(mapScene.RoomReferenceText, mapScene.ResolvedRoomId, rooms);
            AddIfAuthored(references, new ReferenceResolution(nameof(MapScene), mapScene.Id, nameof(MapScene.RoomReferenceText), resolution.Status, mapScene.ResolvedRoomId), mapScene.RoomReferenceText);
        }

        foreach (var roomGroup in roomGroups)
        {
            var resolution = ResolveMapZone(roomGroup.ZoneReferenceText, roomGroup.ResolvedMapZoneId, mapZones);
            AddIfAuthored(references, new ReferenceResolution(nameof(RoomGroup), roomGroup.Id, nameof(RoomGroup.ZoneReferenceText), resolution.Status, roomGroup.ResolvedMapZoneId), roomGroup.ZoneReferenceText);
        }

        return new LogicResolutionReport(references);
    }

    private static void AddIfAuthored(ICollection<ReferenceResolution> references, ReferenceResolution resolution, string? referenceText)
    {
        if (!string.IsNullOrWhiteSpace(referenceText))
        {
            references.Add(resolution);
        }
    }

    private static ReferenceResolution Create<T>(string entityType, Guid entityId, string fieldName, string? referenceText, Guid? resolvedId, IEnumerable<T> targets) where T : AuditedEntity
    {
        var resolution = Resolve(referenceText, resolvedId, targets.Select(x => (x.Id, GetReferenceText(x), IsArchived(x))));
        return new ReferenceResolution(entityType, entityId, fieldName, resolution.Status, resolvedId);
    }

    private static (ReferenceResolutionStatus Status, Guid? ResolvedId) Resolve<T>(string? referenceText, Guid? resolvedId, IEnumerable<T> targets) where T : AuditedEntity
        => Resolve(referenceText, resolvedId, targets.Select(x => (x.Id, GetReferenceText(x), IsArchived(x))));

    internal static ReferenceResolutionStatus GetMapZoneReferenceStatus(string? referenceText, Guid? resolvedId, IEnumerable<MapZoneReferenceTarget> targets)
        => Resolve(referenceText, resolvedId, targets.Select(x => (x.Id, x.InGameId, false))).Status;

    private static (ReferenceResolutionStatus Status, Guid? ResolvedId) ResolveMapZone(string? referenceText, Guid? resolvedId, IEnumerable<MapZone> targets)
        => Resolve(referenceText, resolvedId, targets.Select(x => (x.Id, x.InGameId, false)));

    private static (ReferenceResolutionStatus Status, Guid? ResolvedId) Resolve(string? referenceText, Guid? resolvedId, IEnumerable<(Guid Id, string ReferenceText, bool IsArchived)> targets)
    {
        var matches = targets.Where(x => TextMatches(referenceText, x.ReferenceText)).ToList();
        var activeMatches = matches.Where(x => !x.IsArchived).ToList();

        if (activeMatches.Count > 1)
        {
            return (ReferenceResolutionStatus.Ambiguous, null);
        }

        if (activeMatches.Count == 1)
        {
            return activeMatches[0].Id == resolvedId
                ? (ReferenceResolutionStatus.Resolved, activeMatches[0].Id)
                : (ReferenceResolutionStatus.OutOfSync, activeMatches[0].Id);
        }

        return matches.Count > 0
            ? (ReferenceResolutionStatus.TargetArchived, null)
            : (ReferenceResolutionStatus.Unresolved, null);
    }

    private static string GetReferenceText(AuditedEntity entity) => entity switch
    {
        Room room => room.ReferenceId,
        Subroom subroom => subroom.ReferenceId,
        RoomTransition transition => transition.Alias,
        _ => throw new ArgumentException("Unsupported reference target.", nameof(entity))
    };

    private static bool IsArchived(AuditedEntity entity) => entity is ArchivableEntity { IsArchived: true };

    private static bool TextMatches(string? left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
