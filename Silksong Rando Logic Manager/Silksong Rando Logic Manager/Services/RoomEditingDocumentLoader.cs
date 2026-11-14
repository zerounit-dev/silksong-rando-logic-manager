using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>
/// Complete R3 entry/external-change/recovery read, not the routine save projection.
/// Logic and applicable scene share one SQLite read snapshot. Map sampling remains
/// the existing area-map loader's separate concern. No EF object leaves this service.
/// SQLite audit timestamps already represent UTC; restore only their kind at the
/// contract boundary, preserving the exact stored ticks.
/// </summary>
internal sealed class RoomEditingDocumentLoader(IDbContextFactory<LogicDbContext> contexts, ISceneLayoutLoader scenes, SceneImageFileService images)
{
    public async Task<Wire.RoomCompleteDocument?> LoadAsync(Guid roomId, CancellationToken token)
    {
        using var imageAdmission = await images.AcquireLifecycleAsync(token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var snapshot = await db.Database.BeginTransactionAsync(token);
        // CompleteOpen and protected reconciliation need a coherent full document.
        // BaselineAdmission and targeted checking do not call this method.
        var observation = await db.Rooms.AsNoTracking().Where(x => x.Id == roomId)
            .Select(x => new Wire.RoomDocumentObservation(x.Id, x.CacheFlagUtc)).SingleOrDefaultAsync(token);
        if (observation is null) return null;
        var room = (await ReadScalarsAsync(db, [roomId], images, token)).SingleOrDefault();
        if (room is null) return null;
        var scene = room.Header.Observation.IsArchived ? null : await scenes.LoadAsync(db, roomId, token);
        if (!room.Header.Observation.IsArchived && scene is null)
            throw new InvalidOperationException("The active room scene could not join its complete document snapshot.");
        var group = room.Header.RoomGroupId is { } groupId ? await db.RoomGroups.AsNoTracking().Where(x => x.Id == groupId)
            .Select(x => new Wire.CatalogueGroup(x.Id, x.FriendlyName, x.SortOrder, x.ZoneReferenceText,
                x.ResolvedMapZoneId, x.IsVirtual, x.UpdatedUtc)).SingleAsync(token) : null;
        var roomOrder = await db.Rooms.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id)
            .Select(x => x.Id).ToArrayAsync(token);
        var groupOrder = await db.RoomGroups.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id)
            .Select(x => x.Id).ToArrayAsync(token);
        return new(room.Observation, room.Header, room.Subrooms, room.Transitions, room.Connections, room.Checks, scene)
        { ReadContext = new(group is null ? null : group with { UpdatedUtc = DateTime.SpecifyKind(group.UpdatedUtc, DateTimeKind.Utc) },
            roomOrder.ToImmutableArray(), groupOrder.ToImmutableArray()) };
    }

    // Caller owns lifecycle admission and the authoritative SQLite snapshot.
    internal static async Task<Wire.RoomScalarSnapshot[]> ReadScalarsAsync(
        LogicDbContext db, IReadOnlyCollection<Guid> roomIds, SceneImageFileService images, CancellationToken token)
    {
        var ids = roomIds.ToArray();
        if (ids.Length == 0) return [];
        var rooms = await db.Rooms.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new
        {
            Observation = new Wire.RoomDocumentObservation(x.Id, x.CacheFlagUtc),
            Header = new Wire.RoomHeaderSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived),
                new(x.FriendlyName, x.ReferenceId, x.InGameId, x.Contributors, x.Comments), x.RoomGroupId, x.SortOrder,
                new(x.SceneMinX, x.SceneMinY, x.SceneMaxX, x.SceneMaxY, x.SceneImagePixelsPerUnitX,
                    x.SceneImagePixelsPerUnitY, x.SceneImagePixelOffsetX, x.SceneImagePixelOffsetY,
                    x.UseSceneImage, x.UseUploadedSceneImage, x.IsSceneImageStale,
                    x.SceneImageOriginalHash, x.SceneImageCropHash, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc)))
                { CompletionStatus = (Wire.RoomStatus)x.CompletionStatus }
        }).ToArrayAsync(token);

        var subrooms = await db.Subrooms.AsNoTracking().Where(x => ids.Contains(x.RoomId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.RoomId, Value = new Wire.RoomSubroomSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived), x.SortOrder,
                new(x.FriendlyName, x.ReferenceId, x.Notes),
                new(x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth, x.SceneUnitHeight, x.EnableAnnotation)) })
            .ToListAsync(token);
        var transitions = await db.RoomTransitions.AsNoTracking().Where(x => ids.Contains(x.RoomId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.RoomId, Value = new Wire.RoomTransitionSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived), x.SortOrder,
                new(x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText,
                    x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.IsTodo, x.IsVerified, x.IsUnsafeArrival),
                new(x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ,
                    x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY),
                x.EnableAnnotation, x.RequirementsParseSucceeded, x.ResolvedSourceSubroomId,
                x.ResolvedDestinationRoomId, x.ResolvedDestinationTransitionId) }).ToListAsync(token);
        var connections = await db.SubroomConnections.AsNoTracking().Where(x => ids.Contains(x.RoomId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.RoomId, Value = new Wire.RoomConnectionSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived), x.SortOrder,
                new(x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText,
                    x.Requirements, x.Notes, x.IsTodo, x.IsVerified), x.SceneUnitX, x.SceneUnitY, x.EnableAnnotation,
                x.RequirementsParseSucceeded, x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId) }).ToListAsync(token);
        var checks = await db.CheckLocations.AsNoTracking().Where(x => ids.Contains(x.RoomId))
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.RoomId, Value = new Wire.RoomCheckSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived), x.SortOrder,
                new(x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, x.LocationType, x.IsTodo, x.IsVerified),
                new(x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ,
                    x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY),
                x.EnableAnnotation, x.RequirementsParseSucceeded, x.ResolvedSubroomId) }).ToListAsync(token);
        var subroomsByRoom = subrooms.ToLookup(x => x.RoomId, x => x.Value);
        var transitionsByRoom = transitions.ToLookup(x => x.RoomId, x => x.Value);
        var connectionsByRoom = connections.ToLookup(x => x.RoomId, x => x.Value);
        var checksByRoom = checks.ToLookup(x => x.RoomId, x => x.Value);
        return rooms.Select(room => new Wire.RoomScalarSnapshot(room.Observation, RoomBundleAssembly.Complete(room.Header, images),
            subroomsByRoom[room.Observation.RoomId].ToImmutableArray(), transitionsByRoom[room.Observation.RoomId].ToImmutableArray(),
            connectionsByRoom[room.Observation.RoomId].ToImmutableArray(), checksByRoom[room.Observation.RoomId].ToImmutableArray())).ToArray();
    }
}
