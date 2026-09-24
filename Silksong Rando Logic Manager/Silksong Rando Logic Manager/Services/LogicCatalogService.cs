using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Diagnostics;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed class LogicCatalogService(IDbContextFactory<LogicDbContext> dbContextFactory,
    RequirementValidationService requirementValidation, SceneImageFileService? sceneImages = null,
    ScopedResolverTrace? scopedResolverTrace = null, AppliedRoomStatusService? appliedStatuses = null)
{
    private static readonly LogicValidationService Validation = new();
    private static readonly ConnectionAnnotationService ConnectionAnnotations = new();
    private static readonly SceneImageTransformService SceneImageTransforms = new();
    private readonly RequirementValidationService requirementStatus = requirementValidation;
    private readonly AppliedRoomStatusService statusService = appliedStatuses ?? new(dbContextFactory);

    public async Task<SidebarWorkspaceSnapshot> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var groups = await db.RoomGroups
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.FriendlyName)
            .Select(x => new SidebarRoomGroup(x.Id, x.FriendlyName, x.SortOrder))
            .ToListAsync(cancellationToken);
        var statuses = await statusService.LoadAsync(db, null, cancellationToken);
        var rooms = await db.Rooms
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.FriendlyName)
            .Select(x => new { x.Id, x.RoomGroupId, x.FriendlyName, x.SortOrder, x.IsArchived })
            .ToListAsync(cancellationToken);

        return new SidebarWorkspaceSnapshot(groups, rooms.Select(x => new SidebarRoom(x.Id, x.RoomGroupId, x.FriendlyName, x.SortOrder, x.IsArchived, statuses.GetValueOrDefault(x.Id))).ToList());
    }

    public async Task<GroupEditorData?> GetGroupEditorDataAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.RoomGroups.AsNoTracking()
            .Where(x => x.Id == groupId)
            .Select(x => new { x.Id, x.FriendlyName, x.ZoneReferenceText, x.IsVirtual, x.ResolvedMapZoneId, x.UpdatedUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (group is null)
        {
            return null;
        }

        var zones = await db.MapZones.AsNoTracking()
            .OrderBy(x => x.InGameId)
            .Select(x => new MapZoneReferenceTarget(x.Id, x.InGameId))
            .ToListAsync(cancellationToken);
        ReferenceResolutionStatus? status = string.IsNullOrWhiteSpace(group.ZoneReferenceText)
            ? null
            : LogicReferenceResolver.GetMapZoneReferenceStatus(group.ZoneReferenceText, group.ResolvedMapZoneId, zones);
        var view = new GroupEditorView(group.Id, group.FriendlyName, group.ZoneReferenceText, group.IsVirtual, group.UpdatedUtc);
        var baseline = new RoomGroupEditorBaseline(group.Id, group.FriendlyName, group.ZoneReferenceText, group.IsVirtual, group.UpdatedUtc);
        var draft = new RoomGroupEditorDraft(group.Id, group.FriendlyName, group.ZoneReferenceText, group.IsVirtual);
        return new GroupEditorData(view, baseline, draft, zones.Select(x => x.InGameId).ToList(), status);
    }

    public async Task<RoomDocument?> GetRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var room = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId, cancellationToken);
        if (room is null)
        {
            return null;
        }

        var subrooms = await db.Subrooms.AsNoTracking().Where(x => x.RoomId == roomId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var transitions = await db.RoomTransitions.AsNoTracking().Where(x => x.RoomId == roomId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var connections = await db.SubroomConnections.AsNoTracking().Where(x => x.RoomId == roomId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var checks = await db.CheckLocations.AsNoTracking().Where(x => x.RoomId == roomId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        var allRooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived).OrderBy(x => x.ReferenceId).ToListAsync(cancellationToken);
        var allTransitions = await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && db.Rooms.Any(room => room.Id == x.RoomId && !room.IsArchived)).OrderBy(x => x.Alias).ToListAsync(cancellationToken);
        var allChecks = await db.CheckLocations.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken);

        var resolutionReport = await new LogicReferenceResolver(db).GetResolutionReportAsync(cancellationToken);
        return new RoomDocument(room, subrooms, transitions, connections, checks, allRooms, allTransitions, allChecks, resolutionReport);
    }

    public async Task<RoomGroup> CreateRoomGroupAsync(string friendlyName, CancellationToken cancellationToken = default)
        => (await CreateRoomGroupWithOutcomeAsync(friendlyName, cancellationToken)).Entity;

    public async Task<CatalogCreateOutcome<RoomGroup>> CreateRoomGroupWithOutcomeAsync(string friendlyName, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var group = new RoomGroup
        {
            FriendlyName = friendlyName.Trim(),
            IsVirtual = false,
            SortOrder = await NextSortOrderAsync(db.RoomGroups, cancellationToken)
        };
        db.RoomGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);
        return new(group, false, true, false, TimeSpan.Zero);
    }

    public async Task<CatalogSaveOutcome> SaveRoomGroupAsync(RoomGroupEditorSaveCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Baseline.Id != command.Draft.Id)
        {
            throw new InvalidOperationException("The room-group draft does not match its durable baseline.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var group = await db.RoomGroups.SingleOrDefaultAsync(x => x.Id == command.Baseline.Id, cancellationToken)
            ?? throw new InvalidOperationException($"RoomGroup {command.Baseline.Id} no longer exists.");
        if (group.UpdatedUtc != command.Baseline.UpdatedUtc)
        {
            throw new InvalidOperationException($"RoomGroup {group.Id} changed before this save completed. Reload the document and retry the edit.");
        }

        var changedProperties = new HashSet<string>(StringComparer.Ordinal);
        if (group.FriendlyName != command.Draft.FriendlyName) changedProperties.Add(nameof(RoomGroup.FriendlyName));
        if (group.ZoneReferenceText != command.Draft.ZoneReferenceText) changedProperties.Add(nameof(RoomGroup.ZoneReferenceText));
        if (group.IsVirtual != command.Draft.IsVirtual) changedProperties.Add(nameof(RoomGroup.IsVirtual));
        if (changedProperties.Count == 0) return CatalogSaveOutcome.Unchanged;

        var originalZoneReference = group.ZoneReferenceText;
        group.FriendlyName = command.Draft.FriendlyName;
        group.ZoneReferenceText = command.Draft.ZoneReferenceText;
        group.IsVirtual = command.Draft.IsVirtual;

        var requiresResolution = changedProperties.Contains(nameof(RoomGroup.ZoneReferenceText));
        await using var transaction = requiresResolution ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await db.SaveChangesAsync(cancellationToken);
        var resolverElapsed = TimeSpan.Zero;
        if (requiresResolution)
        {
            var resolverStopwatch = Stopwatch.StartNew();
            await new LogicReferenceResolver(db, scopedResolverTrace).ResolveScopedAsync(
                group,
                changedProperties,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [nameof(RoomGroup.ZoneReferenceText)] = originalZoneReference
                },
                cancellationToken);
            resolverElapsed = resolverStopwatch.Elapsed;
            await transaction!.CommitAsync(cancellationToken);
        }

        return new CatalogSaveOutcome(true, requiresResolution, requiresResolution, true, false, resolverElapsed);
    }

    public async Task DeleteRoomGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Rooms.Where(x => x.RoomGroupId == groupId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RoomGroupId, (Guid?)null), cancellationToken);
        var group = await db.RoomGroups.SingleAsync(x => x.Id == groupId, cancellationToken);
        db.RoomGroups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SidebarRoomGroup>> MoveRoomGroupAsync(Guid groupId, int targetIndex, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var groups = await db.RoomGroups
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.FriendlyName)
            .ToListAsync(cancellationToken);
        var group = groups.SingleOrDefault(x => x.Id == groupId)
            ?? throw new InvalidOperationException($"Room group {groupId} no longer exists.");
        var originalIndex = groups.IndexOf(group);
        groups.RemoveAt(originalIndex);
        if (originalIndex < targetIndex)
        {
            targetIndex--;
        }

        groups.Insert(Math.Clamp(targetIndex, 0, groups.Count), group);
        for (var index = 0; index < groups.Count; index++)
        {
            groups[index].SortOrder = index;
        }

        await db.SaveChangesAsync(cancellationToken);
        return groups.Select(x => new SidebarRoomGroup(x.Id, x.FriendlyName, x.SortOrder)).ToList();
    }

    public async Task<int> AutoMatchUngroupedRoomsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await new LogicReferenceResolver(db).ResolveAsync(cancellationToken);

        var groups = await db.RoomGroups
            .Where(group => group.ResolvedMapZoneId != null)
            .Select(group => new RoomGroupZoneMatch(group.Id, group.ResolvedMapZoneId!.Value))
            .ToListAsync(cancellationToken);
        var mapScenes = await db.MapScenes
            .Select(scene => new MapSceneZoneMatch(scene.MapZoneId, scene.InGameId))
            .ToListAsync(cancellationToken);
        var rooms = await db.Rooms
            .Where(room => !room.IsArchived && room.RoomGroupId == null && room.InGameId != null)
            .ToListAsync(cancellationToken);

        var groupedRooms = 0;
        foreach (var room in rooms)
        {
            if (TryAssignRoomGroupFromMapZone(room, groups, mapScenes)) groupedRooms++;
        }

        if (groupedRooms > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return groupedRooms;
    }

    internal static async Task<bool> TryAutoAssignRoomGroupFromMapZoneAsync(LogicDbContext db, Room room, CancellationToken cancellationToken = default)
    {
        if (room.IsArchived || room.RoomGroupId is not null || string.IsNullOrWhiteSpace(room.InGameId)) return false;
        var groups = await db.RoomGroups
            .Where(group => group.ResolvedMapZoneId != null)
            .Select(group => new RoomGroupZoneMatch(group.Id, group.ResolvedMapZoneId!.Value))
            .ToListAsync(cancellationToken);
        var mapScenes = await db.MapScenes
            .Select(scene => new MapSceneZoneMatch(scene.MapZoneId, scene.InGameId))
            .ToListAsync(cancellationToken);
        return TryAssignRoomGroupFromMapZone(room, groups, mapScenes);
    }

    public async Task<Room> CreateRoomAsync(string friendlyName, Guid? roomGroupId, CancellationToken cancellationToken = default)
        => (await CreateRoomWithOutcomeAsync(friendlyName, roomGroupId, cancellationToken)).Entity;

    public async Task<CatalogCreateOutcome<Room>> CreateRoomWithOutcomeAsync(string friendlyName, Guid? roomGroupId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var referenceId = await CreateUniqueRoomReferenceIdAsync(db, friendlyName, cancellationToken);
        var room = new Room
        {
            FriendlyName = friendlyName.Trim(),
            ReferenceId = referenceId,
            RoomGroupId = roomGroupId,
            SortOrder = await NextSortOrderAsync(db.Rooms.Where(x => !x.IsArchived && x.RoomGroupId == roomGroupId), cancellationToken)
        };
        db.Rooms.Add(room);
        await db.SaveChangesAsync(cancellationToken);
        var resolverStopwatch = Stopwatch.StartNew();
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(room, cancellationToken);
        resolverStopwatch.Stop();
        await transaction.CommitAsync(cancellationToken);
        return new(room, false, true, false, resolverStopwatch.Elapsed);
    }

    public async Task<Subroom> CreateSubroomAsync(Guid roomId, string friendlyName, CancellationToken cancellationToken = default)
        => await CreateSubroomAsync(roomId, new Subroom { FriendlyName = friendlyName, ReferenceId = CreateReferenceId(friendlyName) }, cancellationToken);

    public async Task<Subroom> CreateSubroomAsync(Guid roomId, Subroom draft, CancellationToken cancellationToken = default)
        => (await CreateSubroomWithOutcomeAsync(roomId, draft, cancellationToken)).Entity;

    public async Task<CatalogCreateOutcome<Subroom>> CreateSubroomWithOutcomeAsync(Guid roomId, Subroom draft, CancellationToken cancellationToken = default)
    {
        draft = new Subroom { FriendlyName = draft.FriendlyName, ReferenceId = draft.ReferenceId, Notes = draft.Notes };
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var subroom = new Subroom
        {
            RoomId = roomId,
            FriendlyName = draft.FriendlyName,
            ReferenceId = draft.ReferenceId,
            Notes = draft.Notes,
            SortOrder = await NextSortOrderAsync(db.Subrooms.Where(x => x.RoomId == roomId && !x.IsArchived), cancellationToken)
        };
        db.Subrooms.Add(subroom);
        await db.SaveChangesAsync(cancellationToken);
        var resolverStopwatch = Stopwatch.StartNew();
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(subroom, cancellationToken);
        resolverStopwatch.Stop();
        await transaction.CommitAsync(cancellationToken);
        return new(subroom, true, false, false, resolverStopwatch.Elapsed);
    }

    public async Task<RoomTransition> CreateTransitionAsync(Guid roomId, string alias, string friendlyName, CancellationToken cancellationToken = default)
        => await CreateTransitionAsync(roomId, new RoomTransition { Alias = alias, FriendlyName = friendlyName }, cancellationToken);

    public async Task<RoomTransition> CreateTransitionAsync(Guid roomId, RoomTransition draft, CancellationToken cancellationToken = default)
        => (await CreateTransitionWithOutcomeAsync(roomId, draft, cancellationToken)).Entity;

    public async Task<CatalogCreateOutcome<RoomTransition>> CreateTransitionWithOutcomeAsync(Guid roomId, RoomTransition draft, CancellationToken cancellationToken = default)
    {
        draft = new RoomTransition { Alias = draft.Alias, FriendlyName = draft.FriendlyName, InGameId = draft.InGameId, InGamePositionX = draft.InGamePositionX, InGamePositionY = draft.InGamePositionY, InGamePositionZ = draft.InGamePositionZ, LocalPositionX = draft.LocalPositionX, LocalPositionY = draft.LocalPositionY, LocalPositionZ = draft.LocalPositionZ, AnnotationSceneUnitX = draft.AnnotationSceneUnitX, AnnotationSceneUnitY = draft.AnnotationSceneUnitY, SourceSubroomReferenceText = draft.SourceSubroomReferenceText, DestinationRoomReferenceText = draft.DestinationRoomReferenceText, DestinationTransitionAliasText = draft.DestinationTransitionAliasText, Requirements = draft.Requirements, Notes = draft.Notes, IsTodo = draft.IsTodo, IsVerified = draft.IsVerified };
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var transition = new RoomTransition
        {
            RoomId = roomId,
            FriendlyName = draft.FriendlyName,
            Alias = draft.Alias,
            InGameId = draft.InGameId,
            InGamePositionX = draft.InGamePositionX, InGamePositionY = draft.InGamePositionY, InGamePositionZ = draft.InGamePositionZ,
            LocalPositionX = draft.LocalPositionX, LocalPositionY = draft.LocalPositionY, LocalPositionZ = draft.LocalPositionZ,
            AnnotationSceneUnitX = draft.AnnotationSceneUnitX, AnnotationSceneUnitY = draft.AnnotationSceneUnitY,
            SourceSubroomReferenceText = draft.SourceSubroomReferenceText,
            DestinationRoomReferenceText = draft.DestinationRoomReferenceText,
            DestinationTransitionAliasText = draft.DestinationTransitionAliasText,
            Requirements = draft.Requirements,
            Notes = draft.Notes,
            IsTodo = draft.IsTodo,
            IsVerified = draft.IsVerified,
            SortOrder = await NextSortOrderAsync(db.RoomTransitions.Where(x => x.RoomId == roomId && !x.IsArchived), cancellationToken)
        };
        db.RoomTransitions.Add(transition);
        await db.SaveChangesAsync(cancellationToken);
        var resolverStopwatch = Stopwatch.StartNew();
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(transition, cancellationToken);
        resolverStopwatch.Stop();
        await db.Entry(transition).ReloadAsync(cancellationToken);
        await ValidateRequirementsAsync(db, transition, RequirementBearingRowKind.Transition, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(transition, true, transition.IsTodo, false, resolverStopwatch.Elapsed);
    }

    public async Task<SubroomConnection> CreateConnectionAsync(Guid roomId, string alias, string friendlyName, CancellationToken cancellationToken = default)
        => await CreateConnectionAsync(roomId, new SubroomConnection { Alias = alias, FriendlyName = friendlyName }, cancellationToken);

    public async Task<SubroomConnection> CreateConnectionAsync(Guid roomId, SubroomConnection draft, CancellationToken cancellationToken = default)
        => (await CreateConnectionWithOutcomeAsync(roomId, draft, cancellationToken)).Entity;

    public async Task<CatalogCreateOutcome<SubroomConnection>> CreateConnectionWithOutcomeAsync(Guid roomId, SubroomConnection draft, CancellationToken cancellationToken = default)
    {
        draft = new SubroomConnection { Alias = draft.Alias, FriendlyName = draft.FriendlyName, SourceSubroomReferenceText = draft.SourceSubroomReferenceText, DestinationSubroomReferenceText = draft.DestinationSubroomReferenceText, Requirements = draft.Requirements, Notes = draft.Notes, IsTodo = draft.IsTodo, IsVerified = draft.IsVerified };
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = new SubroomConnection
        {
            RoomId = roomId,
            FriendlyName = draft.FriendlyName,
            Alias = draft.Alias,
            SourceSubroomReferenceText = draft.SourceSubroomReferenceText,
            DestinationSubroomReferenceText = draft.DestinationSubroomReferenceText,
            Requirements = draft.Requirements,
            Notes = draft.Notes,
            IsTodo = draft.IsTodo,
            IsVerified = draft.IsVerified,
            SortOrder = await NextSortOrderAsync(db.SubroomConnections.Where(x => x.RoomId == roomId && !x.IsArchived), cancellationToken)
        };
        db.SubroomConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);
        var resolverStopwatch = Stopwatch.StartNew();
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(connection, cancellationToken);
        resolverStopwatch.Stop();
        await ValidateRequirementsAsync(db, connection, RequirementBearingRowKind.Connection, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(connection, true, connection.IsTodo, false, resolverStopwatch.Elapsed);
    }

    public async Task<CheckLocation> CreateCheckAsync(Guid roomId, string friendlyName, CancellationToken cancellationToken = default)
        => await CreateCheckAsync(roomId, new CheckLocation { FriendlyName = friendlyName }, cancellationToken);

    public async Task<CheckLocation> CreateCheckAsync(Guid roomId, CheckLocation draft, CancellationToken cancellationToken = default)
        => (await CreateCheckWithOutcomeAsync(roomId, draft, cancellationToken)).Entity;

    public async Task<CatalogCreateOutcome<CheckLocation>> CreateCheckWithOutcomeAsync(Guid roomId, CheckLocation draft, CancellationToken cancellationToken = default)
    {
        draft = new CheckLocation { FriendlyName = draft.FriendlyName, SubroomReferenceText = draft.SubroomReferenceText, Requirements = draft.Requirements, Notes = draft.Notes, LocationType = draft.LocationType, IsTodo = draft.IsTodo, IsVerified = draft.IsVerified };
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var check = new CheckLocation
        {
            RoomId = roomId,
            FriendlyName = draft.FriendlyName,
            SubroomReferenceText = draft.SubroomReferenceText,
            Requirements = draft.Requirements,
            Notes = draft.Notes,
            LocationType = draft.LocationType,
            IsTodo = draft.IsTodo,
            IsVerified = draft.IsVerified,
            SortOrder = await NextSortOrderAsync(db.CheckLocations.Where(x => x.RoomId == roomId && !x.IsArchived), cancellationToken)
        };
        db.CheckLocations.Add(check);
        await db.SaveChangesAsync(cancellationToken);
        var resolverStopwatch = Stopwatch.StartNew();
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(check, cancellationToken);
        resolverStopwatch.Stop();
        await ValidateRequirementsAsync(db, check, RequirementBearingRowKind.Check, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(check, true, check.IsTodo, false, resolverStopwatch.Elapsed);
    }

    public async Task<SubroomConnection> ScaffoldInverseConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
        => (await ScaffoldInverseConnectionWithOutcomeAsync(connectionId, cancellationToken)).Entity;

    public async Task<CatalogCreateOutcome<SubroomConnection>> ScaffoldInverseConnectionWithOutcomeAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var source = await db.SubroomConnections.SingleAsync(x => x.Id == connectionId, cancellationToken);
        var roomConnections = await db.SubroomConnections.Where(x => x.RoomId == source.RoomId).ToListAsync(cancellationToken);
        if (!Validation.CanScaffoldInverse(source, roomConnections))
        {
            throw new InvalidOperationException("This connection is not eligible for inverse scaffolding.");
        }

        var inverse = new SubroomConnection
        {
            RoomId = source.RoomId,
            Alias = source.Alias,
            FriendlyName = source.FriendlyName,
            SourceSubroomReferenceText = source.DestinationSubroomReferenceText,
            DestinationSubroomReferenceText = source.SourceSubroomReferenceText,
            Requirements = string.Empty,
            Notes = string.Empty,
            IsTodo = false,
            IsVerified = null,
            SortOrder = await NextSortOrderAsync(db.SubroomConnections.Where(x => x.RoomId == source.RoomId && !x.IsArchived), cancellationToken)
        };
        db.SubroomConnections.Add(inverse);
        await db.SaveChangesAsync(cancellationToken);
        var resolverStopwatch = Stopwatch.StartNew();
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(inverse, cancellationToken);
        resolverStopwatch.Stop();
        await ValidateRequirementsAsync(db, inverse, RequirementBearingRowKind.Connection, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(inverse, true, false, false, resolverStopwatch.Elapsed);
    }

    public async Task<IReadOnlyList<SubroomConnection>> AddConnectionAnnotationAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = await db.SubroomConnections.SingleAsync(item => item.Id == connectionId, cancellationToken);
        var connections = await db.SubroomConnections.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var subrooms = await db.Subrooms.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var group = ConnectionAnnotations.FindEligibleGroup(connection, connections, subrooms) ?? throw new InvalidOperationException("This connection is not eligible for a scene annotation.");
        foreach (var row in group.Rows) row.EnableAnnotation = true;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return group.Rows.Select(CloneConnection).ToList();
    }

    /// <summary>Shows retained, usable group geometry without fabricating a position.</summary>
    public async Task<IReadOnlyList<SubroomConnection>> ShowConnectionAnnotationAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = await db.SubroomConnections.SingleAsync(item => item.Id == connectionId, cancellationToken);
        var connections = await db.SubroomConnections.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var subrooms = await db.Subrooms.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var group = ConnectionAnnotations.FindEligibleGroup(connection, connections, subrooms) ?? throw new InvalidOperationException("This connection is not eligible for a scene annotation.");
        if (group.Rows.All(row => row.EnableAnnotation)) throw new InvalidOperationException("This connection annotation is already enabled.");
        if (group.Rows.Any(row => row.EnableAnnotation) || !group.Rows.All(row => row.SceneUnitX is double x && double.IsFinite(x) && row.SceneUnitY is double y && double.IsFinite(y)))
            throw new InvalidOperationException("This connection annotation has no complete retained position.");
        foreach (var row in group.Rows) row.EnableAnnotation = true;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return group.Rows.Select(CloneConnection).ToList();
    }

    public async Task<IReadOnlyList<SubroomConnection>> RemoveConnectionAnnotationAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = await db.SubroomConnections.SingleAsync(item => item.Id == connectionId, cancellationToken);
        var connections = await db.SubroomConnections.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var subrooms = await db.Subrooms.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var group = ConnectionAnnotations.FindEligibleGroup(connection, connections, subrooms) ?? throw new InvalidOperationException("This connection is not eligible for a scene annotation.");
        if (!group.Rows.All(row => row.EnableAnnotation)) throw new InvalidOperationException("This connection annotation is already disabled.");
        foreach (var row in group.Rows) row.EnableAnnotation = false;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return group.Rows.Select(CloneConnection).ToList();
    }

    /// <summary>
    /// Moves an already-rendered connection annotation. Admission and the group
    /// update deliberately share one SQLite transaction: a stale scene callback
    /// can neither cross rooms nor turn a disabled group back on.
    /// </summary>
    public async Task<IReadOnlyList<SubroomConnection>> MoveConnectionAnnotationAsync(Guid displayedRoomId, Guid connectionId, double x, double y, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidOperationException("Scene annotation coordinates must be finite.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = await db.SubroomConnections.SingleOrDefaultAsync(item => item.Id == connectionId, cancellationToken)
            ?? throw new ConnectionAnnotationMoveMissingException();
        if (connection.RoomId != displayedRoomId) throw new ConnectionAnnotationMoveMissingException();
        if (connection.IsArchived) throw new InvalidOperationException("This connection is no longer active in the current room.");
        var connections = await db.SubroomConnections.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var subrooms = await db.Subrooms.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var group = ConnectionAnnotations.FindEligibleGroup(connection, connections, subrooms) ?? throw new InvalidOperationException("This connection is not eligible for a scene annotation.");
        if (!group.Rows.All(row => row.EnableAnnotation)) throw new InvalidOperationException("This connection annotation is no longer enabled.");
        // A visible group has one complete, finite shared anchor.  Do not use a
        // move request to repair a stale/partial group: it was not rendered and
        // therefore cannot be the source of a direct scene gesture.
        if (!group.Rows.All(row => row.SceneUnitX is double currentX && double.IsFinite(currentX)
            && row.SceneUnitY is double currentY && double.IsFinite(currentY)))
            throw new InvalidOperationException("This connection annotation no longer has a complete rendered position.");
        foreach (var row in group.Rows) { row.SceneUnitX = x; row.SceneUnitY = y; }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return group.Rows.Select(CloneConnection).ToList();
    }

    /// <summary>Commits initial placement only; an already enabled group is never moved by a stale arm.</summary>
    public async Task<IReadOnlyList<SubroomConnection>> PlaceConnectionAnnotationAsync(Guid connectionId, double x, double y, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidOperationException("Scene annotation coordinates must be finite.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = await db.SubroomConnections.SingleAsync(item => item.Id == connectionId, cancellationToken);
        var connections = await db.SubroomConnections.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var subrooms = await db.Subrooms.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var group = ConnectionAnnotations.FindEligibleGroup(connection, connections, subrooms) ?? throw new InvalidOperationException("This connection is not eligible for a scene annotation.");
        if (group.Rows.All(row => row.EnableAnnotation) && group.Rows.All(row => row.SceneUnitX is double currentX && double.IsFinite(currentX)
            && row.SceneUnitY is double currentY && double.IsFinite(currentY)))
            throw new InvalidOperationException("This connection annotation is already enabled.");
        if (group.Rows.Any(row => row.EnableAnnotation) && !group.Rows.All(row => row.EnableAnnotation))
            throw new InvalidOperationException("This connection annotation has inconsistent visibility.");
        foreach (var row in group.Rows) { row.EnableAnnotation = true; row.SceneUnitX = x; row.SceneUnitY = y; }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return group.Rows.Select(CloneConnection).ToList();
    }

    /// <summary>Clears only the shared alias anchor; visibility is intentionally retained.</summary>
    public async Task<IReadOnlyList<SubroomConnection>> ClearConnectionAnnotationAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = await db.SubroomConnections.SingleAsync(item => item.Id == connectionId, cancellationToken);
        var rows = await db.SubroomConnections.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var subrooms = await db.Subrooms.Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var group = ConnectionAnnotations.FindEligibleGroup(connection, rows, subrooms) ?? throw new InvalidOperationException("This connection is not eligible for a scene annotation.");
        foreach (var row in group.Rows) { row.SceneUnitX = null; row.SceneUnitY = null; }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return group.Rows.Select(CloneConnection).ToList();
    }

    public async Task AddSubroomSceneRectangleAsync(Guid subroomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var subroom = await db.Subrooms.SingleAsync(item => item.Id == subroomId, cancellationToken);
        var room = await db.Rooms.SingleAsync(item => item.Id == subroom.RoomId, cancellationToken);
        if (!ValidRoomDimensions(room)) throw new InvalidOperationException("Scene bounds are required before adding a subroom rectangle.");
        var width = room.SceneUnitWidth!.Value * .25;
        var height = room.SceneUnitHeight!.Value * .25;
        subroom.SceneUnitWidth = width;
        subroom.SceneUnitHeight = height;
        subroom.SceneUnitX = (room.SceneUnitWidth.Value - width) / 2;
        subroom.SceneUnitY = (room.SceneUnitHeight.Value - height) / 2;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateSubroomSceneRectangleAsync(Guid subroomId, double? x, double? y, double? width, double? height, CancellationToken cancellationToken = default)
    {
        if (!ValidRectangle(x, y, width, height)) throw new InvalidOperationException("Subroom geometry must be complete, finite, and positive.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var subroom = await db.Subrooms.SingleAsync(item => item.Id == subroomId, cancellationToken);
        subroom.SceneUnitX = x;
        subroom.SceneUnitY = y;
        subroom.SceneUnitWidth = width;
        subroom.SceneUnitHeight = height;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveSubroomSceneRectangleAsync(Guid subroomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var subroom = await db.Subrooms.SingleAsync(item => item.Id == subroomId, cancellationToken);
        subroom.SceneUnitX = null;
        subroom.SceneUnitY = null;
        subroom.SceneUnitWidth = null;
        subroom.SceneUnitHeight = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Room>> MoveRoomAsync(Guid roomId, Guid? targetGroupId, int targetIndex, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var room = await db.Rooms.SingleAsync(x => x.Id == roomId && !x.IsArchived, cancellationToken);
        var sourceGroupId = room.RoomGroupId;
        var sourceIndex = await db.Rooms
            .Where(x => !x.IsArchived && x.RoomGroupId == sourceGroupId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.FriendlyName)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var originalIndex = sourceIndex.IndexOf(roomId);
        var targetRooms = await db.Rooms
            .Where(x => !x.IsArchived && x.RoomGroupId == targetGroupId && x.Id != roomId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.FriendlyName)
            .ToListAsync(cancellationToken);

        if (sourceGroupId == targetGroupId && originalIndex >= 0 && originalIndex < targetIndex)
        {
            targetIndex--;
        }

        room.RoomGroupId = targetGroupId;
        targetRooms.Insert(Math.Clamp(targetIndex, 0, targetRooms.Count), room);
        for (var index = 0; index < targetRooms.Count; index++)
        {
            targetRooms[index].SortOrder = index;
        }

        if (sourceGroupId != targetGroupId)
        {
            var sourceRooms = await db.Rooms
                .Where(x => !x.IsArchived && x.RoomGroupId == sourceGroupId && x.Id != roomId)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.FriendlyName)
                .ToListAsync(cancellationToken);
            for (var index = 0; index < sourceRooms.Count; index++)
            {
                sourceRooms[index].SortOrder = index;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await db.Rooms
            .AsNoTracking()
            .Where(x => !x.IsArchived && (x.RoomGroupId == sourceGroupId || x.RoomGroupId == targetGroupId))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.FriendlyName)
            .ToListAsync(cancellationToken);
    }

    public async Task MoveSubroomAsync(Guid subroomId, int targetIndex, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var subroom = await db.Subrooms.SingleAsync(x => x.Id == subroomId, cancellationToken);
        var originalIndex = await db.Subrooms.Where(x => x.RoomId == subroom.RoomId && x.IsArchived == subroom.IsArchived).OrderBy(x => x.SortOrder).Select(x => x.Id).ToListAsync(cancellationToken);
        if (originalIndex.IndexOf(subroomId) < targetIndex) targetIndex--;
        var subrooms = await db.Subrooms.Where(x => x.RoomId == subroom.RoomId && x.IsArchived == subroom.IsArchived && x.Id != subroomId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        subrooms.Insert(Math.Clamp(targetIndex, 0, subrooms.Count), subroom);
        for (var index = 0; index < subrooms.Count; index++) subrooms[index].SortOrder = index;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveTransitionAsync(Guid transitionId, int targetIndex, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var transition = await db.RoomTransitions.SingleAsync(x => x.Id == transitionId, cancellationToken);
        var originalIndex = await db.RoomTransitions.Where(x => x.RoomId == transition.RoomId && x.IsArchived == transition.IsArchived).OrderBy(x => x.SortOrder).Select(x => x.Id).ToListAsync(cancellationToken);
        if (originalIndex.IndexOf(transitionId) < targetIndex) targetIndex--;
        var transitions = await db.RoomTransitions.Where(x => x.RoomId == transition.RoomId && x.IsArchived == transition.IsArchived && x.Id != transitionId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        transitions.Insert(Math.Clamp(targetIndex, 0, transitions.Count), transition);
        for (var index = 0; index < transitions.Count; index++) transitions[index].SortOrder = index;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveConnectionAsync(Guid connectionId, int targetIndex, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await db.SubroomConnections.SingleAsync(x => x.Id == connectionId, cancellationToken);
        var originalIndex = await db.SubroomConnections.Where(x => x.RoomId == connection.RoomId && x.IsArchived == connection.IsArchived).OrderBy(x => x.SortOrder).Select(x => x.Id).ToListAsync(cancellationToken);
        if (originalIndex.IndexOf(connectionId) < targetIndex) targetIndex--;
        var connections = await db.SubroomConnections.Where(x => x.RoomId == connection.RoomId && x.IsArchived == connection.IsArchived && x.Id != connectionId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        connections.Insert(Math.Clamp(targetIndex, 0, connections.Count), connection);
        for (var index = 0; index < connections.Count; index++) connections[index].SortOrder = index;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveCheckAsync(Guid checkId, int targetIndex, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var check = await db.CheckLocations.SingleAsync(x => x.Id == checkId, cancellationToken);
        var originalIndex = await db.CheckLocations.Where(x => x.RoomId == check.RoomId && x.IsArchived == check.IsArchived).OrderBy(x => x.SortOrder).Select(x => x.Id).ToListAsync(cancellationToken);
        if (originalIndex.IndexOf(checkId) < targetIndex) targetIndex--;
        var checks = await db.CheckLocations.Where(x => x.RoomId == check.RoomId && x.IsArchived == check.IsArchived && x.Id != checkId).OrderBy(x => x.SortOrder).ToListAsync(cancellationToken);
        checks.Insert(Math.Clamp(targetIndex, 0, checks.Count), check);
        for (var index = 0; index < checks.Count; index++) checks[index].SortOrder = index;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetArchivedAsync<TEntity>(TEntity entity, bool isArchived, CancellationToken cancellationToken = default) where TEntity : ArchivableEntity
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var persistedEntity = await db.Set<TEntity>().SingleAsync(x => x.Id == entity.Id, cancellationToken);
        persistedEntity.IsArchived = isArchived;
        await db.SaveChangesAsync(cancellationToken);
        if (!isArchived)
        {
            if (persistedEntity is Room room)
            {
                var context = await requirementStatus.LoadContextAsync(db, cancellationToken);
                var transitions = await db.RoomTransitions.Where(row => row.RoomId == room.Id && !row.IsArchived).ToListAsync(cancellationToken);
                var connections = await db.SubroomConnections.Where(row => row.RoomId == room.Id && !row.IsArchived).ToListAsync(cancellationToken);
                var checks = await db.CheckLocations.Where(row => row.RoomId == room.Id && !row.IsArchived).ToListAsync(cancellationToken);
                foreach (var row in transitions) ApplyRequirementsStatus(context, row, RequirementBearingRowKind.Transition);
                foreach (var row in connections) ApplyRequirementsStatus(context, row, RequirementBearingRowKind.Connection);
                foreach (var row in checks) ApplyRequirementsStatus(context, row, RequirementBearingRowKind.Check);
                using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(cancellationToken);
            }
            else if (persistedEntity is RoomTransition transition)
                await ValidateRequirementsAsync(db, transition, RequirementBearingRowKind.Transition, cancellationToken);
            else if (persistedEntity is SubroomConnection connection)
                await ValidateRequirementsAsync(db, connection, RequirementBearingRowKind.Connection, cancellationToken);
            else if (persistedEntity is CheckLocation check)
                await ValidateRequirementsAsync(db, check, RequirementBearingRowKind.Check, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteRoomPermanentlyAsync(Guid roomId, CancellationToken cancellationToken = default)
        => await DeleteRoomPermanentlyWithSceneImageTimingAsync(roomId, cancellationToken);

    /// <summary>
    /// Deletes an archived room's SQLite-owned data atomically, then removes its
    /// derived scene image. The latter is deliberately timed separately from the
    /// database/resolver lifecycle interval.
    /// </summary>
    public async Task<TimeSpan> DeleteRoomPermanentlyWithSceneImageTimingAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await db.Rooms.SingleAsync(x => x.Id == roomId && x.IsArchived, cancellationToken);
        var transitionIds = await db.RoomTransitions.Where(x => x.RoomId == roomId).Select(x => x.Id).ToListAsync(cancellationToken);
        var subrooms = await db.Subrooms.Where(x => x.RoomId == roomId).ToListAsync(cancellationToken);
        var transitions = await db.RoomTransitions.Where(x => x.RoomId == roomId).ToListAsync(cancellationToken);
        var connections = await db.SubroomConnections.Where(x => x.RoomId == roomId).ToListAsync(cancellationToken);
        var checks = await db.CheckLocations.Where(x => x.RoomId == roomId).ToListAsync(cancellationToken);

        // These inbound resolver fields are independent metadata. A stale value in
        // one field must not erase a valid non-target value in the other.
        await db.RoomTransitions
            .Where(x => !transitionIds.Contains(x.Id) && x.ResolvedDestinationRoomId == roomId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ResolvedDestinationRoomId, (Guid?)null), cancellationToken);
        await db.RoomTransitions
            .Where(x => !transitionIds.Contains(x.Id) && x.ResolvedDestinationTransitionId != null && transitionIds.Contains(x.ResolvedDestinationTransitionId.Value))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ResolvedDestinationTransitionId, (Guid?)null), cancellationToken);
        await db.MapScenes.Where(x => x.ResolvedRoomId == roomId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ResolvedRoomId, (Guid?)null), cancellationToken);
        db.RemoveRange(checks);
        db.RemoveRange(connections);
        db.RemoveRange(transitions);
        db.RemoveRange(subrooms);
        db.Remove(room);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var fileStopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (sceneImages is not null) await sceneImages.DeleteAsync(roomId, cancellationToken);
        fileStopwatch.Stop();
        return fileStopwatch.Elapsed;
    }

    public async Task DeleteSubroomPermanentlyAsync(Guid subroomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var subroom = await db.Subrooms.SingleAsync(x => x.Id == subroomId && x.IsArchived, cancellationToken);
        await db.RoomTransitions.Where(x => x.ResolvedSourceSubroomId == subroomId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ResolvedSourceSubroomId, (Guid?)null), cancellationToken);
        await db.SubroomConnections.Where(x => x.ResolvedSourceSubroomId == subroomId || x.ResolvedDestinationSubroomId == subroomId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.ResolvedSourceSubroomId, (Guid?)null)
            .SetProperty(x => x.ResolvedDestinationSubroomId, (Guid?)null), cancellationToken);
        await db.CheckLocations.Where(x => x.ResolvedSubroomId == subroomId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ResolvedSubroomId, (Guid?)null), cancellationToken);
        db.Subrooms.Remove(subroom);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteTransitionPermanentlyAsync(Guid transitionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var transition = await db.RoomTransitions.SingleAsync(x => x.Id == transitionId && x.IsArchived, cancellationToken);
        await db.RoomTransitions.Where(x => x.Id != transitionId && x.ResolvedDestinationTransitionId == transitionId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ResolvedDestinationTransitionId, (Guid?)null), cancellationToken);
        db.RoomTransitions.Remove(transition);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteConnectionPermanentlyAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var connection = await db.SubroomConnections.SingleAsync(x => x.Id == connectionId && x.IsArchived, cancellationToken);
        db.SubroomConnections.Remove(connection);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteCheckPermanentlyAsync(Guid checkId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var check = await db.CheckLocations.SingleAsync(x => x.Id == checkId && x.IsArchived, cancellationToken);
        db.CheckLocations.Remove(check);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReferenceTextUpdateCandidate>> GetSubroomReferenceUpdateCandidatesAsync(Guid subroomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = new List<ReferenceTextUpdateCandidate>();
        candidates.AddRange(await db.RoomTransitions.AsNoTracking().Where(x => !x.IsArchived && x.ResolvedSourceSubroomId == subroomId).Select(x => new ReferenceTextUpdateCandidate(nameof(RoomTransition), x.Id, nameof(RoomTransition.SourceSubroomReferenceText))).ToListAsync(cancellationToken));
        candidates.AddRange(await db.SubroomConnections.AsNoTracking().Where(x => !x.IsArchived && x.ResolvedSourceSubroomId == subroomId).Select(x => new ReferenceTextUpdateCandidate(nameof(SubroomConnection), x.Id, nameof(SubroomConnection.SourceSubroomReferenceText))).ToListAsync(cancellationToken));
        candidates.AddRange(await db.SubroomConnections.AsNoTracking().Where(x => !x.IsArchived && x.ResolvedDestinationSubroomId == subroomId).Select(x => new ReferenceTextUpdateCandidate(nameof(SubroomConnection), x.Id, nameof(SubroomConnection.DestinationSubroomReferenceText))).ToListAsync(cancellationToken));
        candidates.AddRange(await db.CheckLocations.AsNoTracking().Where(x => !x.IsArchived && x.ResolvedSubroomId == subroomId).Select(x => new ReferenceTextUpdateCandidate(nameof(CheckLocation), x.Id, nameof(CheckLocation.SubroomReferenceText))).ToListAsync(cancellationToken));
        return candidates;
    }

    public async Task<IReadOnlyList<ReferenceTextUpdateCandidate>> GetRoomReferenceUpdateCandidatesAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.RoomTransitions.AsNoTracking()
            .Where(x => !x.IsArchived && x.ResolvedDestinationRoomId == roomId)
            .Select(x => new ReferenceTextUpdateCandidate(nameof(RoomTransition), x.Id, nameof(RoomTransition.DestinationRoomReferenceText)))
            .ToListAsync(cancellationToken);
        candidates.AddRange(await db.MapScenes.AsNoTracking()
            .Where(x => x.ResolvedRoomId == roomId)
            .Select(x => new ReferenceTextUpdateCandidate(nameof(MapScene), x.Id, nameof(MapScene.RoomReferenceText)))
            .ToListAsync(cancellationToken));
        return candidates;
    }

    public async Task UpdateReferenceTextAsync(IReadOnlyCollection<ReferenceTextUpdateCandidate> candidates, string newReferenceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            switch (candidate)
            {
                case { EntityType: nameof(RoomTransition), FieldName: nameof(RoomTransition.SourceSubroomReferenceText) }:
                    var transition = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == candidate.EntityId && !x.IsArchived, cancellationToken);
                    if (transition is not null) transition.SourceSubroomReferenceText = newReferenceId;
                    break;
                case { EntityType: nameof(RoomTransition), FieldName: nameof(RoomTransition.DestinationRoomReferenceText) }:
                    var destinationTransition = await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == candidate.EntityId && !x.IsArchived, cancellationToken);
                    if (destinationTransition is not null) destinationTransition.DestinationRoomReferenceText = newReferenceId;
                    break;
                case { EntityType: nameof(SubroomConnection), FieldName: nameof(SubroomConnection.SourceSubroomReferenceText) }:
                    var sourceConnection = await db.SubroomConnections.SingleOrDefaultAsync(x => x.Id == candidate.EntityId && !x.IsArchived, cancellationToken);
                    if (sourceConnection is not null) sourceConnection.SourceSubroomReferenceText = newReferenceId;
                    break;
                case { EntityType: nameof(SubroomConnection), FieldName: nameof(SubroomConnection.DestinationSubroomReferenceText) }:
                    var destinationConnection = await db.SubroomConnections.SingleOrDefaultAsync(x => x.Id == candidate.EntityId && !x.IsArchived, cancellationToken);
                    if (destinationConnection is not null) destinationConnection.DestinationSubroomReferenceText = newReferenceId;
                    break;
                case { EntityType: nameof(CheckLocation), FieldName: nameof(CheckLocation.SubroomReferenceText) }:
                    var check = await db.CheckLocations.SingleOrDefaultAsync(x => x.Id == candidate.EntityId && !x.IsArchived, cancellationToken);
                    if (check is not null) check.SubroomReferenceText = newReferenceId;
                    break;
                case { EntityType: nameof(MapScene), FieldName: nameof(MapScene.RoomReferenceText) }:
                    var mapScene = await db.MapScenes.SingleOrDefaultAsync(x => x.Id == candidate.EntityId, cancellationToken);
                    if (mapScene is not null) mapScene.RoomReferenceText = newReferenceId;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await new LogicReferenceResolver(db).ResolveAsync(cancellationToken);
    }

    public async Task<bool> SaveAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default) where TEntity : AuditedEntity
        => (await SaveWithOutcomeAsync(entity, cancellationToken)).Changed;

    public async Task<CatalogSaveOutcome> SaveWithOutcomeAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default) where TEntity : AuditedEntity
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var persistedEntity = await db.Set<TEntity>().SingleOrDefaultAsync(x => x.Id == entity.Id, cancellationToken);
        if (persistedEntity is null)
        {
            throw new InvalidOperationException($"{typeof(TEntity).Name} {entity.Id} no longer exists.");
        }

        if (persistedEntity.UpdatedUtc != entity.UpdatedUtc)
        {
            throw new InvalidOperationException($"{typeof(TEntity).Name} {entity.Id} changed before this save completed. Reload the document and retry the edit.");
        }

        var connectionSnapshot = persistedEntity is SubroomConnection connection
            ? await db.SubroomConnections.AsNoTracking().Where(item => item.RoomId == connection.RoomId && !item.IsArchived).ToListAsync(cancellationToken)
            : null;
        if (entity is Room draftRoom && !SceneImageTransforms.IsValidTransform(draftRoom))
        {
            throw new InvalidOperationException("Scene image transforms must be complete, finite, and use positive scales.");
        }

        var markSceneImageStale = persistedEntity is Room persistedRoom && entity is Room roomDraft && SceneImageTransforms.ShouldMarkStale(persistedRoom, roomDraft);
        var entry = db.Entry(persistedEntity);
        var originalTextValues = entry.Properties.ToDictionary(item => item.Metadata.Name, item => item.OriginalValue as string, StringComparer.Ordinal);
        var originalRequirementStatus = persistedEntity switch
        {
            RoomTransition value => value.RequirementsParseSucceeded,
            SubroomConnection value => value.RequirementsParseSucceeded,
            CheckLocation value => value.RequirementsParseSucceeded,
            _ => null
        };
        entry.CurrentValues.SetValues(entity);
        switch (persistedEntity)
        {
            case RoomTransition value: value.RequirementsParseSucceeded = originalRequirementStatus; break;
            case SubroomConnection value: value.RequirementsParseSucceeded = originalRequirementStatus; break;
            case CheckLocation value: value.RequirementsParseSucceeded = originalRequirementStatus; break;
        }
        if (markSceneImageStale && persistedEntity is Room savedRoom)
        {
            savedRoom.IsSceneImageStale = true;
        }
        if (persistedEntity is SubroomConnection savedConnection && connectionSnapshot is not null) await ReconcileConnectionAnnotationAliasAsync(db, savedConnection, connectionSnapshot, cancellationToken);
        await ValidateResolvedOwnershipAsync(db, persistedEntity, cancellationToken);
        if (!entry.Properties.Any(x => x.IsModified))
        {
            return CatalogSaveOutcome.Unchanged;
        }

        var modifiedProperties = entry.Properties.Where(x => x.IsModified).Select(x => x.Metadata.Name).ToHashSet(StringComparer.Ordinal);
        var requiresResolution = RequiresResolution(persistedEntity, modifiedProperties);
        var validatesRequirements = modifiedProperties.Contains("Requirements") &&
            persistedEntity is RoomTransition or SubroomConnection or CheckLocation;
        await using var transaction = requiresResolution || validatesRequirements ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await db.SaveChangesAsync(cancellationToken);
        var resolverElapsed = TimeSpan.Zero;
        if (requiresResolution)
        {
            var resolverStopwatch = Stopwatch.StartNew();
            await new LogicReferenceResolver(db, scopedResolverTrace).ResolveScopedAsync(persistedEntity, modifiedProperties, originalTextValues, cancellationToken);
            resolverElapsed = resolverStopwatch.Elapsed;
        }
        if (validatesRequirements)
        {
            var kind = persistedEntity switch
            {
                RoomTransition => RequirementBearingRowKind.Transition,
                SubroomConnection => RequirementBearingRowKind.Connection,
                CheckLocation => RequirementBearingRowKind.Check,
                _ => throw new ArgumentOutOfRangeException(nameof(persistedEntity))
            };
            if (persistedEntity is not ArchivableEntity requirementRow) throw new InvalidOperationException("Requirement status requires an archivable row.");
            await ValidateRequirementsAsync(db, requirementRow, kind, cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        entity.UpdatedUtc = persistedEntity.UpdatedUtc;
        return new CatalogSaveOutcome(
            true,
            requiresResolution,
            requiresResolution || RequiresDocumentRefresh(persistedEntity, modifiedProperties),
            RequiresSidebarRefresh(persistedEntity, modifiedProperties),
            RequiresSceneLayoutRefresh(persistedEntity, modifiedProperties),
            resolverElapsed);
    }

    public async Task<TransitionAliasSavePatch> SaveTransitionAliasWithPatchAsync(RoomTransition snapshot, Guid displayedRoomId, CancellationToken cancellationToken = default)
    {
        // Capture the old target alias before the ordinary synchronous save so
        // inbound resolution impacts can be patched without replacing a room document.
        string? oldAlias;
        Guid owningRoomId;
        await using (var before = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var persisted = await before.RoomTransitions.AsNoTracking().SingleAsync(item => item.Id == snapshot.Id, cancellationToken);
            oldAlias = persisted.Alias;
            owningRoomId = persisted.RoomId;
        }

        var outcome = await SaveWithOutcomeAsync(snapshot, cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var owner = await db.Rooms.AsNoTracking().SingleAsync(item => item.Id == owningRoomId, cancellationToken);
        var affectedIds = new HashSet<Guid> { snapshot.Id };
        if (!string.Equals(oldAlias, snapshot.Alias, StringComparison.Ordinal))
        {
            var inbound = await db.RoomTransitions.AsNoTracking()
                .Where(item => item.DestinationRoomReferenceText != null && item.DestinationTransitionAliasText != null)
                .Select(item => new { item.Id, item.DestinationRoomReferenceText, item.DestinationTransitionAliasText })
                .ToListAsync(cancellationToken);
            foreach (var candidate in inbound.Where(item => TextMatches(item.DestinationRoomReferenceText, owner.ReferenceId) &&
                (TextMatches(item.DestinationTransitionAliasText, oldAlias) || TextMatches(item.DestinationTransitionAliasText, snapshot.Alias))))
            {
                affectedIds.Add(candidate.Id);
            }
        }

        var transitions = await db.RoomTransitions.AsNoTracking()
            .Where(item => item.RoomId == displayedRoomId && affectedIds.Contains(item.Id))
            .ToListAsync(cancellationToken);
        var report = await new LogicReferenceResolver(db).GetResolutionReportAsync(cancellationToken);
        var diagnostics = report.References.Where(item => transitions.Any(transition => transition.Id == item.EntityId)).ToList();
        var saved = transitions.SingleOrDefault(item => item.Id == snapshot.Id)
            ?? await db.RoomTransitions.AsNoTracking().SingleAsync(item => item.Id == snapshot.Id, cancellationToken);
        return new TransitionAliasSavePatch(saved, transitions, diagnostics, outcome);
    }

    /// <summary>Saves a complete draft by applying only explicit editable transition differences.</summary>
    public async Task<ChildRowSavePatch<RoomTransition>> SaveTransitionWithPatchAsync(RoomTransition baseline, RoomTransition draft, Guid displayedRoomId, CancellationToken cancellationToken = default)
    {
        var localFields = TransitionFieldDiffMapper.Differences(baseline, draft);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var durable = await db.RoomTransitions.SingleOrDefaultAsync(item => item.Id == baseline.Id, cancellationToken);
        if (durable is null || durable.IsArchived || durable.RoomId != displayedRoomId) return new(ChildRowSaveStatus.Missing, null, [], [], [], CatalogSaveOutcome.Unchanged);
        if (durable.UpdatedUtc != baseline.UpdatedUtc && TransitionFieldDiffMapper.Differences(baseline, durable).Intersect(localFields, StringComparer.Ordinal).Any())
            return new(ChildRowSaveStatus.Conflict, TransitionFieldDiffMapper.Clone(durable), localFields, [], [], CatalogSaveOutcome.Unchanged);
        if (localFields.Count == 0)
            return new(ChildRowSaveStatus.Unchanged, TransitionFieldDiffMapper.Clone(durable), [], [TransitionFieldDiffMapper.Clone(durable)], [], CatalogSaveOutcome.Unchanged);

        var oldAlias = durable.Alias;
        var owningRoomId = durable.RoomId;
        var entry = db.Entry(durable);
        var originalTextValues = entry.Properties.ToDictionary(item => item.Metadata.Name, item => item.OriginalValue as string, StringComparer.Ordinal);
        TransitionFieldDiffMapper.Apply(durable, draft, localFields);
        await ValidateResolvedOwnershipAsync(db, durable, cancellationToken);
        var fields = localFields.ToHashSet(StringComparer.Ordinal);
        var requiresResolution = RequiresResolution(durable, fields);
        var validatesRequirements = fields.Contains(nameof(RoomTransition.Requirements));
        await using var transaction = requiresResolution || validatesRequirements ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await db.SaveChangesAsync(cancellationToken);
        var resolverElapsed = TimeSpan.Zero;
        if (requiresResolution)
        {
            var stopwatch = Stopwatch.StartNew();
            await new LogicReferenceResolver(db, scopedResolverTrace).ResolveScopedAsync(durable, fields, originalTextValues, cancellationToken);
            resolverElapsed = stopwatch.Elapsed;
        }
        if (validatesRequirements) await ValidateRequirementsAsync(db, durable, RequirementBearingRowKind.Transition, cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        var outcome = new CatalogSaveOutcome(true, requiresResolution, false, RequiresSidebarRefresh(durable, fields), RequiresSceneLayoutRefresh(durable, fields), resolverElapsed);
        var owner = await db.Rooms.AsNoTracking().SingleAsync(item => item.Id == owningRoomId, cancellationToken);
        var affectedIds = new HashSet<Guid> { durable.Id };
        if (fields.Contains(nameof(RoomTransition.Alias)) && !string.Equals(oldAlias, durable.Alias, StringComparison.Ordinal))
        {
            var inbound = await db.RoomTransitions.AsNoTracking().Where(item => item.DestinationRoomReferenceText != null && item.DestinationTransitionAliasText != null)
                .Select(item => new { item.Id, item.DestinationRoomReferenceText, item.DestinationTransitionAliasText }).ToListAsync(cancellationToken);
            foreach (var candidate in inbound.Where(item => TextMatches(item.DestinationRoomReferenceText, owner.ReferenceId) &&
                (TextMatches(item.DestinationTransitionAliasText, oldAlias) || TextMatches(item.DestinationTransitionAliasText, durable.Alias)))) affectedIds.Add(candidate.Id);
        }

        var rows = await db.RoomTransitions.AsNoTracking().Where(item => item.RoomId == displayedRoomId && affectedIds.Contains(item.Id)).ToListAsync(cancellationToken);
        var saved = rows.SingleOrDefault(item => item.Id == durable.Id) ?? TransitionFieldDiffMapper.Clone(durable);
        var diagnostics = requiresResolution ? (await new LogicReferenceResolver(db).GetResolutionReportAsync(cancellationToken)).References.Where(item => rows.Any(row => row.Id == item.EntityId)).ToList() : [];
        return new(ChildRowSaveStatus.Committed, saved, localFields, rows, diagnostics, outcome);
    }

    public async Task<RoomTransition?> ReadTransitionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.RoomTransitions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    /// <summary>
    /// Saves one connection from immutable full rows.  Alias annotation state is
    /// reconciled in the same transaction, while the returned affected rows are
    /// intentionally field-local patches rather than a replacement room document.
    /// </summary>
    public async Task<ChildRowSavePatch<SubroomConnection>> SaveConnectionWithPatchAsync(SubroomConnection baseline, SubroomConnection draft, Guid displayedRoomId, CancellationToken cancellationToken = default)
    {
        var mapper = new ConnectionFieldDiffMapper();
        var fields = mapper.Differences(baseline, draft);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var durable = await db.SubroomConnections.SingleOrDefaultAsync(item => item.Id == baseline.Id, cancellationToken);
        if (durable is null || durable.IsArchived || ChildRoomId(durable) != displayedRoomId) return new(ChildRowSaveStatus.Missing, null, [], [], [], CatalogSaveOutcome.Unchanged);
        if (durable.UpdatedUtc != baseline.UpdatedUtc && mapper.Differences(baseline, durable).Intersect(fields, StringComparer.Ordinal).Any())
            return new(ChildRowSaveStatus.Conflict, mapper.Clone(durable), fields, [], [], CatalogSaveOutcome.Unchanged);
        if (fields.Count == 0)
            return new(ChildRowSaveStatus.Unchanged, mapper.Clone(durable), [], [mapper.Clone(durable)], [], CatalogSaveOutcome.Unchanged);

        // Capture the active group before applying the explicit field delta so
        // split/merge annotation semantics use the actual durable topology.
        var roomSnapshot = await db.SubroomConnections.AsNoTracking().Where(item => item.RoomId == durable.RoomId && !item.IsArchived).ToListAsync(cancellationToken);
        var entry = db.Entry(durable);
        var originalTextValues = entry.Properties.ToDictionary(item => item.Metadata.Name, item => item.OriginalValue as string, StringComparer.Ordinal);
        mapper.Apply(durable, draft, fields);
        await ReconcileConnectionAnnotationAliasAsync(db, durable, roomSnapshot, cancellationToken);
        await ValidateResolvedOwnershipAsync(db, durable, cancellationToken);
        var changed = fields.ToHashSet(StringComparer.Ordinal);
        var requiresResolution = RequiresResolution(durable, changed);
        var validatesRequirements = changed.Contains(nameof(SubroomConnection.Requirements));
        await using var transaction = requiresResolution || validatesRequirements ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await db.SaveChangesAsync(cancellationToken);
        var resolverElapsed = TimeSpan.Zero;
        if (requiresResolution)
        {
            var stopwatch = Stopwatch.StartNew();
            await new LogicReferenceResolver(db, scopedResolverTrace).ResolveScopedAsync(durable, changed, originalTextValues, cancellationToken);
            resolverElapsed = stopwatch.Elapsed;
        }
        if (validatesRequirements) await ValidateRequirementsAsync(db, durable, RequirementBearingRowKind.Connection, cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        var groupAliases = roomSnapshot.Where(item => item.Id == durable.Id).Select(item => item.Alias)
            .Append(durable.Alias).ToArray();
        var displayedRows = await db.SubroomConnections.AsNoTracking().Where(item => item.RoomId == displayedRoomId).ToListAsync(cancellationToken);
        var affected = displayedRows.Where(item => item.Id == durable.Id || (!item.IsArchived && groupAliases.Any(alias => TextMatches(alias, item.Alias)))).ToList();
        var saved = affected.SingleOrDefault(item => item.Id == durable.Id) ?? mapper.Clone(durable);
        var diagnostics = requiresResolution
            ? (await new LogicReferenceResolver(db).GetResolutionReportAsync(cancellationToken)).References.Where(item => affected.Any(row => row.Id == item.EntityId)).ToList()
            : [];
        var outcome = new CatalogSaveOutcome(true, requiresResolution, false, RequiresSidebarRefresh(saved, changed), RequiresSceneLayoutRefresh(saved, changed), resolverElapsed);
        return new(ChildRowSaveStatus.Committed, saved, fields, affected, diagnostics, outcome);
    }

    public Task<ChildRowSavePatch<Subroom>> SaveSubroomWithPatchAsync(Subroom baseline, Subroom draft, Guid displayedRoomId, CancellationToken cancellationToken = default) =>
        SaveChildWithPatchAsync(db => db.Subrooms, new SubroomFieldDiffMapper(), baseline, draft, displayedRoomId, cancellationToken);

    public async Task<Subroom?> ReadSubroomAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Subrooms.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public async Task<SubroomConnection?> ReadConnectionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SubroomConnections.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    /// <summary>Commits only explicit check fields from a complete immutable row draft.</summary>
    public Task<ChildRowSavePatch<CheckLocation>> SaveCheckWithPatchAsync(CheckLocation baseline, CheckLocation draft, Guid displayedRoomId, CancellationToken cancellationToken = default) =>
        SaveChildWithPatchAsync(db => db.CheckLocations, new CheckFieldDiffMapper(), baseline, draft, displayedRoomId, cancellationToken);

    /// <summary>Direct marker routes alone require an active row owned by the displayed room.</summary>
    public Task<ChildRowSavePatch<CheckLocation>> SaveActiveCheckAnnotationWithPatchAsync(CheckLocation baseline, CheckLocation draft, Guid displayedRoomId, CancellationToken cancellationToken = default) =>
        SaveChildWithPatchAsync(db => db.CheckLocations, new CheckFieldDiffMapper(), baseline, draft, displayedRoomId, cancellationToken, requireActiveDisplayedOwner: true);

    public async Task<CheckLocation?> ReadCheckAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.CheckLocations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    // Compatibility adapter for older callers. Ordinary editor paths use the
    // baseline/draft overload above.
    public async Task<CheckSavePatch> SaveCheckWithPatchAsync(CheckLocation snapshot, Guid displayedRoomId, CancellationToken cancellationToken = default)
    {
        var baseline = new CheckFieldDiffMapper().Clone(snapshot);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var durable = await db.CheckLocations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == snapshot.Id, cancellationToken);
        if (durable is null) throw new InvalidOperationException($"CheckLocation {snapshot.Id} no longer exists.");
        baseline = new CheckFieldDiffMapper().Clone(durable);
        var patch = await SaveCheckWithPatchAsync(baseline, snapshot, displayedRoomId, cancellationToken);
        if (patch.SavedRow is null) throw new InvalidOperationException($"CheckLocation {snapshot.Id} could not be saved.");
        return new(patch.SavedRow.RoomId, patch.SavedRow, patch.Diagnostics, patch.Outcome);
    }

    private async Task<ChildRowSavePatch<T>> SaveChildWithPatchAsync<T>(Func<LogicDbContext, DbSet<T>> set, IChildRowFieldDiffMapper<T> mapper, T baseline, T draft, Guid displayedRoomId, CancellationToken cancellationToken, bool requireActiveDisplayedOwner = false) where T : ArchivableEntity
    {
        var fields = mapper.Differences(baseline, draft);
        if (draft is Subroom subroomDraft && fields.Any(field => field is nameof(Subroom.SceneUnitX) or nameof(Subroom.SceneUnitY) or nameof(Subroom.SceneUnitWidth) or nameof(Subroom.SceneUnitHeight)) &&
            !(subroomDraft.SceneUnitX is null && subroomDraft.SceneUnitY is null && subroomDraft.SceneUnitWidth is null && subroomDraft.SceneUnitHeight is null) &&
            !ValidRectangle(subroomDraft.SceneUnitX, subroomDraft.SceneUnitY, subroomDraft.SceneUnitWidth, subroomDraft.SceneUnitHeight))
            throw new InvalidOperationException("Subroom geometry must be complete, finite, and positive.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var durable = await set(db).SingleOrDefaultAsync(item => item.Id == baseline.Id, cancellationToken);
        if (durable is null || (requireActiveDisplayedOwner && (durable.IsArchived || ChildRoomId(durable) != displayedRoomId))) return new(ChildRowSaveStatus.Missing, null, [], [], [], CatalogSaveOutcome.Unchanged);
        if (durable.UpdatedUtc != baseline.UpdatedUtc && mapper.Differences(baseline, durable).Intersect(fields, StringComparer.Ordinal).Any())
            return new(ChildRowSaveStatus.Conflict, mapper.Clone(durable), fields, [], [], CatalogSaveOutcome.Unchanged);
        if (fields.Count == 0)
            return new(ChildRowSaveStatus.Unchanged, mapper.Clone(durable), [], [mapper.Clone(durable)], [], CatalogSaveOutcome.Unchanged);

        var entry = db.Entry(durable);
        var originalTextValues = entry.Properties.ToDictionary(item => item.Metadata.Name, item => item.OriginalValue as string, StringComparer.Ordinal);
        mapper.Apply(durable, draft, fields);
        await ValidateResolvedOwnershipAsync(db, durable, cancellationToken);
        var changed = fields.ToHashSet(StringComparer.Ordinal);
        var requiresResolution = RequiresResolution(durable, changed);
        var validatesRequirements = durable is CheckLocation && changed.Contains(nameof(CheckLocation.Requirements));
        await using var transaction = requiresResolution || validatesRequirements ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await db.SaveChangesAsync(cancellationToken);
        var resolverElapsed = TimeSpan.Zero;
        if (requiresResolution)
        {
            var stopwatch = Stopwatch.StartNew();
            await new LogicReferenceResolver(db, scopedResolverTrace).ResolveScopedAsync(durable, changed, originalTextValues, cancellationToken);
            resolverElapsed = stopwatch.Elapsed;
        }
        if (validatesRequirements) await ValidateRequirementsAsync(db, (CheckLocation)(object)durable, RequirementBearingRowKind.Check, cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        var saved = await set(db).AsNoTracking().SingleAsync(item => item.Id == durable.Id, cancellationToken);
        var rows = ChildRoomId(saved) == displayedRoomId ? new[] { saved } : Array.Empty<T>();
        var diagnostics = requiresResolution
            ? (await new LogicReferenceResolver(db).GetResolutionReportAsync(cancellationToken)).References.Where(item => rows.Any(row => row.Id == item.EntityId)).ToList()
            : [];
        var outcome = new CatalogSaveOutcome(true, requiresResolution, false, RequiresSidebarRefresh(saved, changed), RequiresSceneLayoutRefresh(saved, changed), resolverElapsed);
        return new(ChildRowSaveStatus.Committed, saved, fields, rows, diagnostics, outcome);
    }

    private static Guid ChildRoomId(ArchivableEntity entity) => entity switch
    {
        Subroom row => row.RoomId,
        SubroomConnection row => row.RoomId,
        CheckLocation row => row.RoomId,
        _ => throw new ArgumentOutOfRangeException(nameof(entity))
    };

    private async Task ValidateRequirementsAsync(LogicDbContext db, ArchivableEntity row, RequirementBearingRowKind kind,
        CancellationToken cancellationToken)
    {
        var context = await requirementStatus.LoadContextAsync(db, cancellationToken);
        await ValidateRequirementsAsync(db, context, row, kind, cancellationToken);
    }

    private async Task ValidateRequirementsAsync(LogicDbContext db, RequirementValidationContext context,
        ArchivableEntity row, RequirementBearingRowKind kind, CancellationToken cancellationToken)
    {
        ApplyRequirementsStatus(context, row, kind);
        using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(cancellationToken);
    }

    private void ApplyRequirementsStatus(RequirementValidationContext context, ArchivableEntity row,
        RequirementBearingRowKind kind)
    {
        if (row.IsArchived) return;
        var roomId = row switch
        {
            RoomTransition value => value.RoomId,
            SubroomConnection value => value.RoomId,
            CheckLocation value => value.RoomId,
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        if (!context.Rooms.Any(room => room.Id == roomId)) return;
        var requirements = row switch
        {
            RoomTransition value => value.Requirements,
            SubroomConnection value => value.Requirements,
            CheckLocation value => value.Requirements,
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        var status = requirementStatus.ValidateSafely(context, roomId, row.Id, kind, requirements);
        switch (row)
        {
            case RoomTransition value: value.RequirementsParseSucceeded = status; break;
            case SubroomConnection value: value.RequirementsParseSucceeded = status; break;
            case CheckLocation value: value.RequirementsParseSucceeded = status; break;
        }
    }

    private static bool TextMatches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static SubroomConnection CloneConnection(SubroomConnection value) => new()
    {
        Id = value.Id, RoomId = value.RoomId, Alias = value.Alias, FriendlyName = value.FriendlyName,
        SourceSubroomReferenceText = value.SourceSubroomReferenceText, DestinationSubroomReferenceText = value.DestinationSubroomReferenceText,
        Requirements = value.Requirements, RequirementsParseSucceeded = value.RequirementsParseSucceeded,
        Notes = value.Notes, EnableAnnotation = value.EnableAnnotation,
        SceneUnitX = value.SceneUnitX, SceneUnitY = value.SceneUnitY, ResolvedSourceSubroomId = value.ResolvedSourceSubroomId,
        ResolvedDestinationSubroomId = value.ResolvedDestinationSubroomId, SortOrder = value.SortOrder, IsTodo = value.IsTodo,
        IsVerified = value.IsVerified, IsArchived = value.IsArchived, ArchivedUtc = value.ArchivedUtc,
        CreatedUtc = value.CreatedUtc, UpdatedUtc = value.UpdatedUtc
    };

    private static bool RequiresResolution(AuditedEntity entity, IReadOnlySet<string> properties) => entity switch
    {
        Room => properties.Contains(nameof(Room.ReferenceId)),
        Subroom => properties.Contains(nameof(Subroom.ReferenceId)),
        RoomTransition => properties.Contains(nameof(RoomTransition.Alias)) || properties.Contains(nameof(RoomTransition.SourceSubroomReferenceText)) || properties.Contains(nameof(RoomTransition.DestinationRoomReferenceText)) || properties.Contains(nameof(RoomTransition.DestinationTransitionAliasText)),
        SubroomConnection => properties.Contains(nameof(SubroomConnection.SourceSubroomReferenceText)) || properties.Contains(nameof(SubroomConnection.DestinationSubroomReferenceText)),
        CheckLocation => properties.Contains(nameof(CheckLocation.SubroomReferenceText)),
        RoomGroup => properties.Contains(nameof(RoomGroup.ZoneReferenceText)),
        _ => false
    };

    private static bool RequiresDocumentRefresh(AuditedEntity entity, IReadOnlySet<string> properties) => entity switch
    {
        RoomTransition => properties.Contains(nameof(RoomTransition.InGameId)),
        CheckLocation => properties.Contains(nameof(CheckLocation.InGameId)),
        _ => false
    };

    private static bool RequiresSidebarRefresh(AuditedEntity entity, IReadOnlySet<string> properties) => entity switch
    {
        Room => properties.Contains(nameof(Room.FriendlyName)),
        RoomGroup => properties.Contains(nameof(RoomGroup.FriendlyName)) || properties.Contains(nameof(RoomGroup.ZoneReferenceText)),
        RoomTransition => properties.Contains(nameof(RoomTransition.IsTodo)),
        SubroomConnection => properties.Contains(nameof(SubroomConnection.IsTodo)),
        CheckLocation => properties.Contains(nameof(CheckLocation.IsTodo)),
        _ => false
    };

    private static bool RequiresSceneLayoutRefresh(AuditedEntity entity, IReadOnlySet<string> properties) => entity switch
    {
        Room => properties.Contains(nameof(Room.SceneUnitWidth)) || properties.Contains(nameof(Room.SceneUnitHeight)),
        Subroom => properties.Overlaps([nameof(Subroom.FriendlyName), nameof(Subroom.SceneUnitX), nameof(Subroom.SceneUnitY), nameof(Subroom.SceneUnitWidth), nameof(Subroom.SceneUnitHeight)]),
        RoomTransition => properties.Overlaps([nameof(RoomTransition.FriendlyName), nameof(RoomTransition.Alias), nameof(RoomTransition.EnableAnnotation), nameof(RoomTransition.InGamePositionX), nameof(RoomTransition.InGamePositionY), nameof(RoomTransition.AnnotationSceneUnitX), nameof(RoomTransition.AnnotationSceneUnitY)]),
        SubroomConnection => properties.Overlaps([nameof(SubroomConnection.Alias), nameof(SubroomConnection.FriendlyName), nameof(SubroomConnection.EnableAnnotation), nameof(SubroomConnection.SceneUnitX), nameof(SubroomConnection.SceneUnitY)]),
        CheckLocation => properties.Overlaps([nameof(CheckLocation.FriendlyName), nameof(CheckLocation.EnableAnnotation), nameof(CheckLocation.InGamePositionX), nameof(CheckLocation.InGamePositionY), nameof(CheckLocation.AnnotationSceneUnitX), nameof(CheckLocation.AnnotationSceneUnitY)]),
        _ => false
    };

    private static async Task ValidateResolvedOwnershipAsync<TEntity>(LogicDbContext db, TEntity entity, CancellationToken cancellationToken) where TEntity : AuditedEntity
    {
        switch (entity)
        {
            case RoomTransition transition when transition.ResolvedSourceSubroomId is { } sourceSubroomId && !await db.Subrooms.AnyAsync(x => x.Id == sourceSubroomId && x.RoomId == transition.RoomId, cancellationToken):
                throw new InvalidOperationException("Resolved transition source subroom must belong to the owning room.");
            case RoomTransition transition when transition.ResolvedDestinationTransitionId is { } destinationTransitionId && transition.ResolvedDestinationRoomId is { } destinationRoomId && !await db.RoomTransitions.AnyAsync(x => x.Id == destinationTransitionId && x.RoomId == destinationRoomId, cancellationToken):
                throw new InvalidOperationException("Resolved transition destination must belong to the resolved destination room.");
            case SubroomConnection connection when connection.ResolvedSourceSubroomId is { } sourceId && !await db.Subrooms.AnyAsync(x => x.Id == sourceId && x.RoomId == connection.RoomId, cancellationToken):
                throw new InvalidOperationException("Resolved connection source subroom must belong to the owning room.");
            case SubroomConnection connection when connection.ResolvedDestinationSubroomId is { } destinationId && !await db.Subrooms.AnyAsync(x => x.Id == destinationId && x.RoomId == connection.RoomId, cancellationToken):
                throw new InvalidOperationException("Resolved connection destination subroom must belong to the owning room.");
            case CheckLocation check when check.ResolvedSubroomId is { } subroomId && !await db.Subrooms.AnyAsync(x => x.Id == subroomId && x.RoomId == check.RoomId, cancellationToken):
                throw new InvalidOperationException("Resolved check subroom must belong to the owning room.");
        }
    }

    private static async Task ReconcileConnectionAnnotationAliasAsync(LogicDbContext db, SubroomConnection connection, IReadOnlyList<SubroomConnection> snapshot, CancellationToken cancellationToken)
    {
        var before = snapshot.Single(item => item.Id == connection.Id);
        if (string.Equals(before.Alias.Trim(), connection.Alias.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        var oldGroup = snapshot.Where(item => string.Equals(item.Alias.Trim(), before.Alias.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        var targetGroup = await db.SubroomConnections.Where(item => item.RoomId == connection.RoomId && !item.IsArchived && item.Id != connection.Id && item.Alias.ToLower() == connection.Alias.Trim().ToLower()).ToListAsync(cancellationToken);
        if (targetGroup.Count > 0)
        {
            connection.EnableAnnotation = targetGroup[0].EnableAnnotation;
            connection.SceneUnitX = targetGroup[0].SceneUnitX;
            connection.SceneUnitY = targetGroup[0].SceneUnitY;
        }
        else if (oldGroup.Length > 1)
        {
            connection.EnableAnnotation = false;
            connection.SceneUnitX = null;
            connection.SceneUnitY = null;
        }
        else
        {
            connection.EnableAnnotation = before.EnableAnnotation;
            connection.SceneUnitX = before.SceneUnitX;
            connection.SceneUnitY = before.SceneUnitY;
        }
    }

    private static bool ValidRoomDimensions(Room room) => room.SceneUnitWidth is { } width && room.SceneUnitHeight is { } height && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
    private static bool ValidRectangle(double? x, double? y, double? width, double? height) => x is { } xValue && y is { } yValue && width is { } widthValue && height is { } heightValue && double.IsFinite(xValue) && double.IsFinite(yValue) && double.IsFinite(widthValue) && double.IsFinite(heightValue) && widthValue > 0 && heightValue > 0;

    private static async Task<int> NextSortOrderAsync<TEntity>(IQueryable<TEntity> query, CancellationToken cancellationToken) where TEntity : class
    {
        var sortOrder = await query.Select(x => (int?)EF.Property<int>(x, "SortOrder")).MaxAsync(cancellationToken);
        return (sortOrder ?? -1) + 1;
    }

    private static async Task<string> CreateUniqueRoomReferenceIdAsync(LogicDbContext db, string friendlyName, CancellationToken cancellationToken)
    {
        var baseId = CreateReferenceId(friendlyName);
        var candidate = baseId;
        var suffix = 2;
        while (await db.Rooms.AnyAsync(x => !x.IsArchived && x.ReferenceId == candidate, cancellationToken))
        {
            candidate = $"{baseId}-{suffix++}";
        }
        return candidate;
    }

    private static string CreateReferenceId(string value)
    {
        var characters = value.Trim().ToLowerInvariant().Select(x => char.IsLetterOrDigit(x) ? x : '-').ToArray();
        var compact = string.Join('-', new string(characters).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(compact) ? "room" : compact;
    }

    private static bool TryAssignRoomGroupFromMapZone(Room room, IEnumerable<RoomGroupZoneMatch> groups, IEnumerable<MapSceneZoneMatch> mapScenes)
    {
        var zoneIds = mapScenes
            .Where(scene => string.Equals(room.InGameId!.Trim(), scene.InGameId.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(scene => scene.MapZoneId)
            .Distinct()
            .ToList();
        if (zoneIds.Count != 1) return false;

        var groupIds = groups.Where(group => group.MapZoneId == zoneIds[0]).Select(group => group.Id).ToList();
        if (groupIds.Count != 1) return false;

        room.RoomGroupId = groupIds[0];
        return true;
    }

}

internal sealed record RoomGroupZoneMatch(Guid Id, Guid MapZoneId);
internal sealed record MapSceneZoneMatch(Guid MapZoneId, string InGameId);

public sealed record SidebarWorkspaceSnapshot(
    IReadOnlyList<SidebarRoomGroup> RoomGroups,
    IReadOnlyList<SidebarRoom> Rooms);

public sealed record SidebarRoomGroup(Guid Id, string FriendlyName, int SortOrder);
public sealed record SidebarRoom(Guid Id, Guid? RoomGroupId, string FriendlyName, int SortOrder, bool IsArchived, AppliedRoomStatus Status);
public sealed record GroupEditorView(Guid Id, string FriendlyName, string? ZoneReferenceText, bool IsVirtual, DateTime UpdatedUtc);
public sealed record RoomGroupEditorBaseline(Guid Id, string FriendlyName, string? ZoneReferenceText, bool IsVirtual, DateTime UpdatedUtc);
public sealed class RoomGroupEditorDraft(Guid id, string friendlyName, string? zoneReferenceText, bool isVirtual)
{
    public Guid Id { get; } = id;
    public string FriendlyName { get; set; } = friendlyName;
    public string? ZoneReferenceText { get; set; } = zoneReferenceText;
    public bool IsVirtual { get; set; } = isVirtual;
}
public sealed record RoomGroupEditorSaveCommand(RoomGroupEditorBaseline Baseline, RoomGroupEditorDraft Draft);
public sealed record GroupEditorData(
    GroupEditorView View,
    RoomGroupEditorBaseline Baseline,
    RoomGroupEditorDraft Draft,
    IReadOnlyList<string> ZoneIds,
    ReferenceResolutionStatus? ZoneReferenceStatus);
internal sealed record MapZoneReferenceTarget(Guid Id, string InGameId);

public sealed record RoomDocument(
    Room Room,
    IReadOnlyList<Subroom> Subrooms,
    IReadOnlyList<RoomTransition> Transitions,
    IReadOnlyList<SubroomConnection> Connections,
    IReadOnlyList<CheckLocation> Checks,
    IReadOnlyList<Room> AvailableRooms,
    IReadOnlyList<RoomTransition> AvailableTransitions,
    IReadOnlyList<CheckLocation> AvailableChecks,
    LogicResolutionReport ResolutionReport);

public sealed record ReferenceTextUpdateCandidate(string EntityType, Guid EntityId, string FieldName);

public sealed record CatalogSaveOutcome(bool Changed, bool Resolved, bool RequiresDocumentRefresh, bool RequiresSidebarRefresh, bool RequiresSceneLayoutRefresh, TimeSpan ResolverElapsed)
{
    public static CatalogSaveOutcome Unchanged { get; } = new(false, false, false, false, false, TimeSpan.Zero);
}

public sealed record TransitionAliasSavePatch(
    RoomTransition SavedTransition,
    IReadOnlyList<RoomTransition> CurrentRoomTransitions,
    IReadOnlyList<ReferenceResolution> CurrentRoomDiagnostics,
    CatalogSaveOutcome Outcome);

public sealed record ConnectionSavePatch(
    Guid RoomId,
    IReadOnlyList<SubroomConnection> CurrentRoomConnections,
    IReadOnlyList<ReferenceResolution> CurrentRoomDiagnostics,
    CatalogSaveOutcome Outcome,
    SubroomConnection? Snapshot);

public sealed record CheckSavePatch(
    Guid RoomId,
    CheckLocation SavedCheck,
    IReadOnlyList<ReferenceResolution> CurrentRoomDiagnostics,
    CatalogSaveOutcome Outcome);

public sealed record CatalogCreateOutcome<TEntity>(TEntity Entity, bool RequiresDocumentRefresh, bool RequiresSidebarRefresh, bool RequiresSceneLayoutRefresh, TimeSpan ResolverElapsed) where TEntity : AuditedEntity;
