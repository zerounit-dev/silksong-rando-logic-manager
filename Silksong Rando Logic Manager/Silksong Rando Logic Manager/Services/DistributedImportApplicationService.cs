using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Text.Json;

namespace Silksong_Rando_Logic_Manager.Services;

// Stateless import orchestration. Callers hold only DistributedImportOrchestrationState
// and shaped comparison evidence; every durable operation has its own transaction.
public sealed class DistributedImportApplicationService(IDbContextFactory<LogicDbContext> contexts, TimeProvider timeProvider, SceneImageFileService sceneImages)
{
    public Task<DistributedImportOutcome> StartAsync(DistributedImportStartRequest request, CancellationToken ct = default) =>
        StartAsync(request, null, ct);

    // A failed later startup stage must not repeat an earlier committed stage.
    // The caller owns this transient state only for the open wizard instance.
    public async Task<DistributedImportOutcome> StartAsync(DistributedImportStartRequest request, DistributedImportOrchestrationState? priorState, CancellationToken ct = default)
    {
        var errors = await ValidateAgainstDatabaseAsync(request.Package, ct);
        if (errors.Count != 0) return new DistributedImportRejected(errors);

        var map = priorState?.AreaMapStageCompleted == true ? priorState.AreaMapOutcome
            : request.Package.AreaMap is null ? new("area-map", false, true, "No area-map snapshot was supplied.")
            : request.ApplyAreaMap ? await ApplyMapAsync(request.Package.AreaMap, ct) : new("area-map", false, true, "Area-map snapshot was not selected.");
        var stateAfterMap = NewState(request.Package, true, !Failed(map, out var mapError), false, new("room-groupings", 0, 1, "Waiting for room-grouping stage."), map, new("room-groupings", false, false, null), new HashSet<Guid>(), new HashSet<Guid>(), []);
        if (!stateAfterMap.AreaMapStageCompleted) return new DistributedImportFailed("area-map", mapError, stateAfterMap);

        var groups = priorState?.RoomGroupingStageCompleted == true ? priorState.RoomGroupingOutcome
            : request.Package.RoomGroupings is null ? new("room-groupings", false, true, "No room-grouping snapshot was supplied.")
            : request.Package.IsPartialRoomDump == true ? new("room-groupings", false, true, "Partial-package group context is nonmutating.")
            : request.ApplyRoomGroupings ? await ApplyGroupsAsync(request.Package.RoomGroupings, ct) : new("room-groupings", false, true, "Room-grouping snapshot was not selected.");
        if (Failed(groups, out var groupError)) return new DistributedImportFailed("room-groupings", groupError, stateAfterMap);

        return new DistributedImportStarted(await CreateReadyStateAsync(request.Package, map, groups, ct), map, groups);
    }

    public async Task<DistributedImportOutcome> PrepareRoomAsync(DistributedImportOrchestrationState state, Guid roomId, CancellationToken ct = default)
    {
        if (!ReadyForRooms(state, out var rejected)) return rejected;
        var incoming = state.Package.Rooms.SingleOrDefault(x => x.Id == roomId);
        if (incoming is null) return new DistributedImportRejected(["The requested room is not in the package."]);
        var comparison = await CompareAsync(incoming, ct);
        return comparison.Kind == DistributedRoomComparisonKind.Identical
            ? new DistributedImportRoomSkipped(roomId, state)
            : new DistributedImportRoomPrepared(comparison);
    }

    public async Task<DistributedImportOutcome> DecideRoomAsync(DistributedImportOrchestrationState state, DistributedRoomDecisionIntent intent, DistributedRoomComparison reviewed, CancellationToken ct = default)
    {
        if (!ReadyForRooms(state, out var rejected)) return rejected;
        var incoming = state.Package.Rooms.SingleOrDefault(x => x.Id == intent.RoomId);
        if (incoming is null || reviewed.RoomId != intent.RoomId) return new DistributedImportRejected(["The room decision does not match this package comparison."]);
        if (!state.PendingRoomIds.Contains(intent.RoomId)) return new DistributedImportRejected(["This room is not pending an import decision."]);

        // Keep/skip are no-write decisions but are still constrained to the kind
        // that was reviewed. An identical document admits no decision at all.
        if (!IsAdmitted(reviewed.Kind, intent)) return new DistributedImportRejected(["The room decision is not valid for this comparison."]);
        if (reviewed.Kind == DistributedRoomComparisonKind.New && intent is SkipIncomingRoom) return new DistributedImportRoomSkipped(intent.RoomId, Decide(state, intent.RoomId));
        if (reviewed.Kind == DistributedRoomComparisonKind.Changed && intent is KeepMasterRoom) return new DistributedImportRoomKept(intent.RoomId, Decide(state, intent.RoomId));

        try
        {
            var outcome = await ApplyReviewedRoomAsync(incoming, reviewed, ct);
            return outcome is DistributedImportRoomApplied applied ? applied with { State = Decide(state, applied.RoomId) } : outcome;
        }
        catch (DbUpdateException ex) { return new DistributedImportFailed("room", ex.Message, state); }
    }

    public async Task<DistributedImportOutcome> PrepareSweepAsync(DistributedImportOrchestrationState state, CancellationToken ct = default)
    {
        if (!ReadyForRooms(state, out var rejected)) return rejected;
        if (state.PendingRoomIds.Count != 0) return new DistributedImportRejected(["Every new or changed room requires an explicit decision before final omitted-room review."]);
        if (state.Package.IsPartialRoomDump != false) return new DistributedImportSweepPrepared([], new([]));
        var ids = state.Package.Rooms.Select(x => x.Id).ToHashSet();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived && !ids.Contains(x.Id)).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new DistributedImportSweepRoom(x.Id, x.FriendlyName)).ToArrayAsync(ct);
        return new DistributedImportSweepPrepared(rooms, new(rooms.Select(x => x.RoomId).ToArray()));
    }

    public async Task<DistributedImportOutcome> DecideSweepAsync(DistributedImportOrchestrationState state, DistributedImportSweepReview review, bool archive, CancellationToken ct = default)
    {
        if (!ReadyForRooms(state, out var rejected)) return rejected;
        if (state.PendingRoomIds.Count != 0) return new DistributedImportRejected(["Every new or changed room requires an explicit decision before final omitted-room review."]);
        if (state.Package.IsPartialRoomDump != false || !archive) return new DistributedImportSweepCompleted(false);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var reviewedIds = review.RoomIds.ToHashSet();
            var rows = await db.Rooms.Where(x => !x.IsArchived && reviewedIds.Contains(x.Id)).ToListAsync(ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            foreach (var row in rows) { row.IsArchived = true; row.ArchivedUtc = now; row.UpdatedUtc = now; }
            using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new DistributedImportSweepCompleted(true);
        }
        catch (DbUpdateException ex) { return new DistributedImportFailed("final-sweep", ex.Message, state); }
    }

    // Fresh comparison and mutation intentionally share this one SQLite transaction.
    private async Task<DistributedImportOutcome> ApplyReviewedRoomAsync(DistributedRoomDocument incoming, DistributedRoomComparison reviewed, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var fresh = await CompareAsync(db, incoming, ct);
        if (!SameComparison(reviewed, fresh)) return new DistributedImportComparisonRefreshed(fresh);

        var room = await db.Rooms.SingleOrDefaultAsync(x => x.Id == incoming.Id, ct);
        var invalidateSceneImage = room is null || CaptureStateDiffers(room, incoming);
        if (room is null) { room = new Room { Id = incoming.Id }; db.Rooms.Add(room); }
        var groupExists = incoming.RoomGroupId is { } group && await db.RoomGroups.AnyAsync(x => x.Id == group, ct);
        CopyRoom(room, incoming, groupExists ? incoming.RoomGroupId : null);
        await ReplaceChildrenAsync(db, room.Id, incoming, timeProvider.GetUtcNow().UtcDateTime, ct);
        using (db.SuppressAuditMetadata()) { await db.SaveChangesAsync(ct); await new LogicReferenceResolver(db).ResolveAsync(ct); }
        await tx.CommitAsync(ct);
        if (invalidateSceneImage) await sceneImages.DeleteAsync(incoming.Id, ct);
        return new DistributedImportRoomApplied(incoming.Id, null!);
    }

    private async Task<DistributedImportStageOutcome> ApplyGroupsAsync(DistributedRoomGroupingSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var current = await db.RoomGroups.ToDictionaryAsync(x => x.Id, ct); var incomingIds = snapshot.Groups.Select(x => x.Id).ToHashSet();
            foreach (var source in snapshot.Groups)
            {
                if (!current.TryGetValue(source.Id, out var row)) { row = new RoomGroup { Id = source.Id }; db.RoomGroups.Add(row); }
                row.FriendlyName = source.FriendlyName; row.ZoneReferenceText = source.ZoneReferenceText; row.SortOrder = source.SortOrder; row.CreatedUtc = source.CreatedUtc; row.UpdatedUtc = source.UpdatedUtc;
            }
            var removed = current.Keys.Where(x => !incomingIds.Contains(x)).ToHashSet();
            if (removed.Count != 0)
            {
                foreach (var room in await db.Rooms.Where(x => x.RoomGroupId != null && removed.Contains(x.RoomGroupId.Value)).ToListAsync(ct)) room.RoomGroupId = null;
                db.RoomGroups.RemoveRange(current.Values.Where(x => removed.Contains(x.Id)));
            }
            using (db.SuppressAuditMetadata()) { await db.SaveChangesAsync(ct); await new LogicReferenceResolver(db).ResolveAsync(ct); }
            await tx.CommitAsync(ct); return new("room-groupings", true, false, null);
        }
        catch (DbUpdateException ex) { return new("room-groupings", false, false, "failed:" + ex.Message); }
    }

    private async Task<DistributedImportStageOutcome> ApplyMapAsync(DistributedAreaMapSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var maps = await db.Maps.ToDictionaryAsync(x => x.Id, ct); var zones = await db.MapZones.ToDictionaryAsync(x => x.Id, ct); var scenes = await db.MapScenes.ToDictionaryAsync(x => x.Id, ct);
            var chunks = await db.MapChunks.ToDictionaryAsync(x => x.Id, ct); var overlays = await db.MapOverlays.ToDictionaryAsync(x => x.Id, ct);
            var mapIds = snapshot.Maps.Select(x => x.Id).ToHashSet(); var zoneIds = snapshot.Maps.SelectMany(x => x.Zones).Select(x => x.Id).ToHashSet();
            var sceneIds = snapshot.Maps.SelectMany(x => x.Zones).SelectMany(x => x.Scenes).Select(x => x.Id).ToHashSet();
            var chunkIds = snapshot.Maps.SelectMany(x => x.Zones).SelectMany(x => x.Scenes).SelectMany(x => x.Chunks).Select(x => x.Id).ToHashSet(); var overlayIds = snapshot.Maps.SelectMany(x => x.Overlays).Select(x => x.Id).ToHashSet();
            // Remove local-only rows first (and child-first) so a matching GUID can
            // safely take an incoming structural identity that a removed row held.
            var removedZones = zones.Keys.Where(x => !zoneIds.Contains(x)).ToHashSet();
            if (removedZones.Count != 0) foreach (var group in await db.RoomGroups.Where(x => x.ResolvedMapZoneId != null && removedZones.Contains(x.ResolvedMapZoneId.Value)).ToListAsync(ct)) group.ResolvedMapZoneId = null;
            db.MapChunks.RemoveRange(chunks.Values.Where(x => !chunkIds.Contains(x.Id)));
            db.MapScenes.RemoveRange(scenes.Values.Where(x => !sceneIds.Contains(x.Id)));
            db.MapZones.RemoveRange(zones.Values.Where(x => !zoneIds.Contains(x.Id)));
            db.MapOverlays.RemoveRange(overlays.Values.Where(x => !overlayIds.Contains(x.Id)));
            db.Maps.RemoveRange(maps.Values.Where(x => !mapIds.Contains(x.Id)));
            using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(ct);

            // SQLite enforces each structural unique index for each individual
            // UPDATE. Stage every retained identity first, then write the valid
            // complete snapshot values. This permits GUID-stable swaps and parent
            // moves without depending on EF's update ordering.
            StageStructuralIdentities(maps, zones, scenes, chunks, mapIds, zoneIds, sceneIds, chunkIds, snapshot);
            using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(ct);
            foreach (var source in snapshot.Maps)
            {
                if (!maps.TryGetValue(source.Id, out var map)) { map = new Map { Id = source.Id }; db.Maps.Add(map); } CopyMap(map, source);
                foreach (var sourceOverlay in source.Overlays) { if (!overlays.TryGetValue(sourceOverlay.Id, out var overlay)) { overlay = new MapOverlay { Id = sourceOverlay.Id }; db.MapOverlays.Add(overlay); } CopyOverlay(overlay, sourceOverlay, source.Id); }
                foreach (var sourceZone in source.Zones)
                {
                    if (!zones.TryGetValue(sourceZone.Id, out var zone)) { zone = new MapZone { Id = sourceZone.Id }; db.MapZones.Add(zone); } CopyZone(zone, sourceZone, source.Id);
                    foreach (var sourceScene in sourceZone.Scenes)
                    {
                        if (!scenes.TryGetValue(sourceScene.Id, out var scene)) { scene = new MapScene { Id = sourceScene.Id }; db.MapScenes.Add(scene); } CopyScene(scene, sourceScene, sourceZone.Id);
                        foreach (var sourceChunk in sourceScene.Chunks) { if (!chunks.TryGetValue(sourceChunk.Id, out var chunk)) { chunk = new MapChunk { Id = sourceChunk.Id }; db.MapChunks.Add(chunk); } CopyChunk(chunk, sourceChunk, sourceScene.Id); }
                    }
                }
            }
            using (db.SuppressAuditMetadata()) { await db.SaveChangesAsync(ct); await new LogicReferenceResolver(db).ResolveAsync(ct); }
            await tx.CommitAsync(ct); return new("area-map", true, false, null);
        }
        catch (DbUpdateException ex) { return new("area-map", false, false, "failed:" + ex.Message); }
    }

    private static void CopyRoom(Room r, DistributedRoomDocument x, Guid? group) { r.RoomGroupId=group; r.ReferenceId=x.ReferenceId; r.FriendlyName=x.FriendlyName; r.InGameId=x.InGameId; r.Contributors=x.Contributors; r.Comments=x.Comments; r.SceneUnitWidth=x.SceneUnitWidth; r.SceneUnitHeight=x.SceneUnitHeight; r.SceneImageScaleXPercent=x.SceneImageScaleXPercent; r.SceneImageScaleYPercent=x.SceneImageScaleYPercent; r.SceneImagePanXPercent=x.SceneImagePanXPercent; r.SceneImagePanYPercent=x.SceneImagePanYPercent; r.IsSceneImageStale=x.IsSceneImageStale; r.SortOrder=x.SortOrder; r.IsArchived=x.IsArchived; r.ArchivedUtc=x.ArchivedUtc; r.CreatedUtc=x.CreatedUtc; r.UpdatedUtc=x.UpdatedUtc; }
    private static bool CaptureStateDiffers(Room current, DistributedRoomDocument incoming) =>
        current.SceneUnitWidth != incoming.SceneUnitWidth ||
        current.SceneUnitHeight != incoming.SceneUnitHeight ||
        current.SceneImageScaleXPercent != incoming.SceneImageScaleXPercent ||
        current.SceneImageScaleYPercent != incoming.SceneImageScaleYPercent ||
        current.SceneImagePanXPercent != incoming.SceneImagePanXPercent ||
        current.SceneImagePanYPercent != incoming.SceneImagePanYPercent ||
        current.IsSceneImageStale != incoming.IsSceneImageStale;
    private static void CopyMap(Map r, DistributedMap x) { r.InGameId=x.InGameId; r.FriendlyName=x.FriendlyName; r.SortOrder=x.SortOrder; r.MapUnitMinX=x.MapUnitMinX; r.MapUnitMinY=x.MapUnitMinY; r.MapUnitMaxX=x.MapUnitMaxX; r.MapUnitMaxY=x.MapUnitMaxY; }
    private static void CopyOverlay(MapOverlay r, DistributedMapOverlay x, Guid parent) { r.MapId=parent; r.FriendlyName=x.FriendlyName; r.ImageAssetKey=x.ImageAssetKey; r.ScaleXPercent=x.ScaleXPercent; r.ScaleYPercent=x.ScaleYPercent; r.LeftOffsetPercent=x.LeftOffsetPercent; r.BottomOffsetPercent=x.BottomOffsetPercent; r.SortOrder=x.SortOrder; }
    private static void CopyZone(MapZone r, DistributedMapZone x, Guid parent) { r.MapId=parent; r.InGameId=x.InGameId; r.FriendlyName=x.FriendlyName; r.MapUnitMinX=x.MapUnitMinX; r.MapUnitMinY=x.MapUnitMinY; r.MapUnitMaxX=x.MapUnitMaxX; r.MapUnitMaxY=x.MapUnitMaxY; }
    private static void CopyScene(MapScene r, DistributedMapScene x, Guid parent) { r.MapZoneId=parent; r.InGameId=x.InGameId; r.FriendlyName=x.FriendlyName; r.RoomReferenceText=x.RoomReferenceText; }
    private static void CopyChunk(MapChunk r, DistributedMapChunk x, Guid parent) { r.MapSceneId=parent; r.CacheIndex=x.CacheIndex; r.InitialState=x.InitialState; r.MapUnitMinX=x.MapUnitMinX; r.MapUnitMinY=x.MapUnitMinY; r.MapUnitMaxX=x.MapUnitMaxX; r.MapUnitMaxY=x.MapUnitMaxY; r.MapUnitZ=x.MapUnitZ; }

    private static void StageStructuralIdentities(
        IReadOnlyDictionary<Guid, Map> maps,
        IReadOnlyDictionary<Guid, MapZone> zones,
        IReadOnlyDictionary<Guid, MapScene> scenes,
        IReadOnlyDictionary<Guid, MapChunk> chunks,
        IReadOnlySet<Guid> mapIds,
        IReadOnlySet<Guid> zoneIds,
        IReadOnlySet<Guid> sceneIds,
        IReadOnlySet<Guid> chunkIds,
        DistributedAreaMapSnapshot snapshot)
    {
        var reservedText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in snapshot.Maps)
        {
            reservedText.Add(map.InGameId);
            foreach (var zone in map.Zones)
            {
                reservedText.Add(zone.InGameId);
                foreach (var scene in zone.Scenes) reservedText.Add(scene.InGameId);
            }
        }
        string StageText(string prefix, Guid id)
        {
            var candidate = $"__exchange_stage_{prefix}_{id:N}";
            while (!reservedText.Add(candidate)) candidate += "_";
            return candidate;
        }
        foreach (var row in maps.Values.Where(x => mapIds.Contains(x.Id))) row.InGameId = StageText("map", row.Id);
        foreach (var row in zones.Values.Where(x => zoneIds.Contains(x.Id))) row.InGameId = StageText("zone", row.Id);
        foreach (var row in scenes.Values.Where(x => sceneIds.Contains(x.Id))) row.InGameId = StageText("scene", row.Id);
        var reservedCacheIndexes = snapshot.Maps.SelectMany(x => x.Zones).SelectMany(x => x.Scenes).SelectMany(x => x.Chunks).Select(x => x.CacheIndex).ToHashSet();
        var candidateIndex = int.MinValue;
        foreach (var row in chunks.Values.Where(x => chunkIds.Contains(x.Id)).OrderBy(x => x.Id))
        {
            while (!reservedCacheIndexes.Add(candidateIndex)) candidateIndex = checked(candidateIndex + 1);
            row.CacheIndex = candidateIndex;
            candidateIndex = checked(candidateIndex + 1);
        }
    }

    private static async Task ReplaceChildrenAsync(LogicDbContext db, Guid roomId, DistributedRoomDocument incoming, DateTime now, CancellationToken ct)
    {
        var subs=await db.Subrooms.Where(x=>x.RoomId==roomId).ToDictionaryAsync(x=>x.Id,ct); foreach(var x in incoming.Subrooms){ if(!subs.TryGetValue(x.Id,out var r)){r=new Subroom{Id=x.Id,RoomId=roomId};db.Subrooms.Add(r);} r.ReferenceId=x.ReferenceId;r.FriendlyName=x.FriendlyName;r.Notes=x.Notes;r.SceneUnitX=x.SceneUnitX;r.SceneUnitY=x.SceneUnitY;r.SceneUnitWidth=x.SceneUnitWidth;r.SceneUnitHeight=x.SceneUnitHeight;r.EnableAnnotation=x.EnableAnnotation;r.SortOrder=x.SortOrder;r.IsArchived=x.IsArchived;r.ArchivedUtc=x.ArchivedUtc;r.CreatedUtc=x.CreatedUtc;r.UpdatedUtc=x.UpdatedUtc;} ArchiveMissing(subs, incoming.Subrooms.Select(x=>x.Id), now);
        var trans=await db.RoomTransitions.Where(x=>x.RoomId==roomId).ToDictionaryAsync(x=>x.Id,ct); foreach(var x in incoming.Transitions){ if(!trans.TryGetValue(x.Id,out var r)){r=new RoomTransition{Id=x.Id,RoomId=roomId};db.RoomTransitions.Add(r);} r.Alias=x.Alias;r.FriendlyName=x.FriendlyName;r.InGameId=x.InGameId;r.InGamePositionX=x.InGamePositionX;r.InGamePositionY=x.InGamePositionY;r.InGamePositionZ=x.InGamePositionZ;r.LocalPositionX=x.LocalPositionX;r.LocalPositionY=x.LocalPositionY;r.LocalPositionZ=x.LocalPositionZ;r.AnnotationSceneUnitX=x.AnnotationSceneUnitX;r.AnnotationSceneUnitY=x.AnnotationSceneUnitY;r.EnableAnnotation=x.EnableAnnotation;r.SourceSubroomReferenceText=x.SourceSubroomReferenceText;r.DestinationRoomReferenceText=x.DestinationRoomReferenceText;r.DestinationTransitionAliasText=x.DestinationTransitionAliasText;r.Requirements=x.Requirements;r.RequirementsParseSucceeded=null;r.Notes=x.Notes;r.SortOrder=x.SortOrder;r.IsTodo=x.IsTodo;r.IsVerified=x.IsVerified;r.IsArchived=x.IsArchived;r.ArchivedUtc=x.ArchivedUtc;r.CreatedUtc=x.CreatedUtc;r.UpdatedUtc=x.UpdatedUtc;} ArchiveMissing(trans, incoming.Transitions.Select(x=>x.Id), now);
        var cons=await db.SubroomConnections.Where(x=>x.RoomId==roomId).ToDictionaryAsync(x=>x.Id,ct); foreach(var x in incoming.Connections){ if(!cons.TryGetValue(x.Id,out var r)){r=new SubroomConnection{Id=x.Id,RoomId=roomId};db.SubroomConnections.Add(r);} r.Alias=x.Alias;r.FriendlyName=x.FriendlyName;r.SourceSubroomReferenceText=x.SourceSubroomReferenceText;r.DestinationSubroomReferenceText=x.DestinationSubroomReferenceText;r.Requirements=x.Requirements;r.RequirementsParseSucceeded=null;r.Notes=x.Notes;r.EnableAnnotation=x.EnableAnnotation;r.SceneUnitX=x.SceneUnitX;r.SceneUnitY=x.SceneUnitY;r.SortOrder=x.SortOrder;r.IsTodo=x.IsTodo;r.IsVerified=x.IsVerified;r.IsArchived=x.IsArchived;r.ArchivedUtc=x.ArchivedUtc;r.CreatedUtc=x.CreatedUtc;r.UpdatedUtc=x.UpdatedUtc;} ArchiveMissing(cons, incoming.Connections.Select(x=>x.Id), now);
        var checks=await db.CheckLocations.Where(x=>x.RoomId==roomId).ToDictionaryAsync(x=>x.Id,ct); foreach(var x in incoming.Checks){ if(!checks.TryGetValue(x.Id,out var r)){r=new CheckLocation{Id=x.Id,RoomId=roomId};db.CheckLocations.Add(r);} r.FriendlyName=x.FriendlyName;r.InGameId=x.InGameId;r.InGamePositionX=x.InGamePositionX;r.InGamePositionY=x.InGamePositionY;r.InGamePositionZ=x.InGamePositionZ;r.LocalPositionX=x.LocalPositionX;r.LocalPositionY=x.LocalPositionY;r.LocalPositionZ=x.LocalPositionZ;r.AnnotationSceneUnitX=x.AnnotationSceneUnitX;r.AnnotationSceneUnitY=x.AnnotationSceneUnitY;r.SubroomReferenceText=x.SubroomReferenceText;r.Requirements=x.Requirements;r.RequirementsParseSucceeded=null;r.Notes=x.Notes;r.LocationType=x.LocationType;r.EnableAnnotation=x.EnableAnnotation;r.SortOrder=x.SortOrder;r.IsTodo=x.IsTodo;r.IsVerified=x.IsVerified;r.IsArchived=x.IsArchived;r.ArchivedUtc=x.ArchivedUtc;r.CreatedUtc=x.CreatedUtc;r.UpdatedUtc=x.UpdatedUtc;} ArchiveMissing(checks, incoming.Checks.Select(x=>x.Id), now);
    }
    private static void ArchiveMissing<T>(Dictionary<Guid,T> current, IEnumerable<Guid> incoming, DateTime now) where T : ArchivableEntity { var ids=incoming.ToHashSet(); foreach(var row in current.Values.Where(x=>!ids.Contains(x.Id)&&!x.IsArchived)){row.IsArchived=true;row.ArchivedUtc=now;row.UpdatedUtc=now;} }

    private async Task<DistributedRoomComparison> CompareAsync(DistributedRoomDocument incoming, CancellationToken ct) { await using var db = await contexts.CreateDbContextAsync(ct); return await CompareAsync(db, incoming, ct); }
    private static async Task<DistributedRoomComparison> CompareAsync(LogicDbContext db, DistributedRoomDocument incoming, CancellationToken ct) { var p=await DistributedImportProjectionLoader.LoadRoomAsync(db,incoming.Id,ct); var current=p is null ? null : DistributedImportProjectionMapper.MapRoom(p); var identities=await db.Rooms.AsNoTracking().Select(x=>new DistributedImportRoomIdentity(x.Id,x.FriendlyName,x.InGameId)).ToArrayAsync(ct); return DistributedRoomComparisonService.CompareWithIdentities(current,incoming,identities); }
    private static bool ReadyForRooms(DistributedImportOrchestrationState state, out DistributedImportOutcome rejected) { if (!state.HasStarted) { rejected=new DistributedImportRejected(["Start import is required before room operations."]); return false; } if (!state.RoomGroupingStageCompleted) { rejected=new DistributedImportRejected(["Room grouping must complete or be skipped before room operations."]); return false; } rejected=null!; return true; }
    private static bool IsAdmitted(DistributedRoomComparisonKind kind, DistributedRoomDecisionIntent intent) => kind switch { DistributedRoomComparisonKind.New => intent is SkipIncomingRoom or ImportIncomingRoom, DistributedRoomComparisonKind.Changed => intent is KeepMasterRoom or UseIncomingRoom, _ => false };
    private static bool Failed(DistributedImportStageOutcome value, out string error) { error=value.Detail?["failed:".Length..] ?? string.Empty; return value.Detail?.StartsWith("failed:", StringComparison.Ordinal)==true; }

    private async Task<DistributedImportOrchestrationState> CreateReadyStateAsync(DistributedImportPackage package, DistributedImportStageOutcome map, DistributedImportStageOutcome groups, CancellationToken ct)
    {
        var comparisons = new List<(DistributedRoomDocument Room, DistributedRoomComparisonKind Kind)>();
        foreach (var room in package.Rooms)
            comparisons.Add((room, (await CompareAsync(room, ct)).Kind));

        var groupOrder = package.RoomGroupings?.Groups
            .ToDictionary(x => x.Id, x => x.SortOrder) ?? [];
        var reviewRoomIds = comparisons
            .Where(x => x.Kind is DistributedRoomComparisonKind.New or DistributedRoomComparisonKind.Changed)
            .OrderBy(x => x.Kind == DistributedRoomComparisonKind.New ? 0 : 1)
            .ThenBy(x => x.Room.RoomGroupId is { } groupId && groupOrder.TryGetValue(groupId, out var sortOrder) ? 0 : 1)
            .ThenBy(x => x.Room.RoomGroupId is { } groupId && groupOrder.TryGetValue(groupId, out var sortOrder) ? sortOrder : 0)
            .ThenBy(x => x.Room.SortOrder)
            .ThenBy(x => x.Room.Id)
            .Select(x => x.Room.Id)
            .ToArray();
        var pending = reviewRoomIds.ToHashSet();
        return NewState(package, true, true, true, new("rooms", package.Rooms.Count - pending.Count, package.Rooms.Count, "Awaiting explicit decisions for new or changed rooms."), map, groups, pending, new HashSet<Guid>(), reviewRoomIds);
    }

    private static DistributedImportOrchestrationState Decide(DistributedImportOrchestrationState state, Guid roomId)
    {
        var pending = state.PendingRoomIds.Where(x => x != roomId).ToHashSet();
        var decided = state.DecidedRoomIds.Append(roomId).ToHashSet();
        return state with { PendingRoomIds = pending, DecidedRoomIds = decided, Progress = new("rooms", state.Package.Rooms.Count - pending.Count, state.Package.Rooms.Count, pending.Count == 0 ? "All room decisions are complete." : "Awaiting explicit room decisions.") };
    }

    private static DistributedImportOrchestrationState NewState(DistributedImportPackage package, bool started, bool mapDone, bool groupDone, DistributedImportProgress progress, DistributedImportStageOutcome map, DistributedImportStageOutcome groups, IReadOnlySet<Guid> pending, IReadOnlySet<Guid> decided, IReadOnlyList<Guid> reviewRoomIds) => new(package, started, mapDone, groupDone, progress, map, groups, pending, decided, reviewRoomIds);

    private async Task<List<string>> ValidateAgainstDatabaseAsync(DistributedImportPackage package, CancellationToken ct)
    {
        await using var db=await contexts.CreateDbContextAsync(ct); var errors=new List<string>(); var types=new Dictionary<Guid,string>(); void Add(Guid id,string type){ if(types.TryGetValue(id,out var prior)&&prior!=type) errors.Add($"GUID {id} is reused for {prior} and {type} entities."); else types[id]=type; }
        foreach(var room in package.Rooms){Add(room.Id,"room");foreach(var x in room.Subrooms)Add(x.Id,"subroom");foreach(var x in room.Transitions)Add(x.Id,"transition");foreach(var x in room.Connections)Add(x.Id,"connection");foreach(var x in room.Checks){Add(x.Id,"check");if(package.ExportVersion==3&&x.LocationType is not null&&!CheckLocationTypeCatalogue.IsRecognized(x.LocationType))errors.Add($"Check {x.Id} in room {room.Id} has an unrecognized location type.");}} foreach(var x in package.RoomGroupings?.Groups??[])Add(x.Id,"group"); foreach(var x in package.AreaMap?.Maps??[]){Add(x.Id,"map");foreach(var y in x.Overlays)Add(y.Id,"overlay");foreach(var y in x.Zones){Add(y.Id,"zone");foreach(var z in y.Scenes){Add(z.Id,"scene");foreach(var q in z.Chunks)Add(q.Id,"chunk");}}}
        var all=types.Keys.ToHashSet(); var tables=new[]{("room",await db.Rooms.Select(x=>x.Id).ToArrayAsync(ct)),("subroom",await db.Subrooms.Select(x=>x.Id).ToArrayAsync(ct)),("transition",await db.RoomTransitions.Select(x=>x.Id).ToArrayAsync(ct)),("connection",await db.SubroomConnections.Select(x=>x.Id).ToArrayAsync(ct)),("check",await db.CheckLocations.Select(x=>x.Id).ToArrayAsync(ct)),("group",await db.RoomGroups.Select(x=>x.Id).ToArrayAsync(ct)),("map",await db.Maps.Select(x=>x.Id).ToArrayAsync(ct)),("zone",await db.MapZones.Select(x=>x.Id).ToArrayAsync(ct)),("scene",await db.MapScenes.Select(x=>x.Id).ToArrayAsync(ct)),("chunk",await db.MapChunks.Select(x=>x.Id).ToArrayAsync(ct)),("overlay",await db.MapOverlays.Select(x=>x.Id).ToArrayAsync(ct))}; foreach(var(type,ids) in tables)foreach(var id in ids.Where(all.Contains))if(types[id]!=type)errors.Add($"GUID {id} is reused for local {type} and incoming {types[id]} entities.");
        async Task CheckOwners<T>(IEnumerable<(Guid Id,Guid Owner)> incoming, IQueryable<T> query, Func<T,Guid> id, Func<T,Guid> owner,string name){var local=(await query.ToListAsync(ct)).Where(x=>all.Contains(id(x))).ToDictionary(id,owner);foreach(var x in incoming)if(local.TryGetValue(x.Id,out var found)&&found!=x.Owner)errors.Add($"{name} GUID {x.Id} belongs to a different local room.");} await CheckOwners(package.Rooms.SelectMany(r=>r.Subrooms.Select(x=>(x.Id,r.Id))),db.Subrooms,x=>x.Id,x=>x.RoomId,"Subroom");await CheckOwners(package.Rooms.SelectMany(r=>r.Transitions.Select(x=>(x.Id,r.Id))),db.RoomTransitions,x=>x.Id,x=>x.RoomId,"Transition");await CheckOwners(package.Rooms.SelectMany(r=>r.Connections.Select(x=>(x.Id,r.Id))),db.SubroomConnections,x=>x.Id,x=>x.RoomId,"Connection");await CheckOwners(package.Rooms.SelectMany(r=>r.Checks.Select(x=>(x.Id,r.Id))),db.CheckLocations,x=>x.Id,x=>x.RoomId,"Check"); return errors;
    }
    private static bool SameComparison(DistributedRoomComparison left, DistributedRoomComparison right) => JsonSerializer.Serialize(left)==JsonSerializer.Serialize(right);
}
