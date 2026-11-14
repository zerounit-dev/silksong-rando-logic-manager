using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>
/// Fresh affected-family scalar projection. The operation chooses the family and
/// scene concerns; this service never calls the complete room/V2 logic/map loader.
/// Each returned stamp and its payload come from the same read snapshot.
/// Restore UTC kind on SQLite audit values only as they enter the contract;
/// their ticks and the independently converted cache observations stay unchanged.
/// </summary>
internal sealed class RoomEditingMutationProjection(IDbContextFactory<LogicDbContext> contexts, ISceneLayoutLoader scenes, SceneImageFileService images)
{
    public async Task<ImmutableArray<Wire.RoomDocumentDelta>> LoadAsync(RoomMutationEffect effect)
    {
        if (effect.AffectedRoomIds.IsEmpty) return [];
        using var imageAdmission = await images.AcquireLifecycleAsync(CancellationToken.None);
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await using var snapshot = await db.Database.BeginTransactionAsync(CancellationToken.None);
        if (effect.SharedPlan is { } plan) await plan.VerifyAsync(db, CancellationToken.None);
        var ids = effect.AffectedRoomIds.Except(effect.DeletedRoomIds).ToArray();
        var headers = await db.Rooms.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new
        {
            Observation = new Wire.RoomDocumentObservation(x.Id, x.CacheFlagUtc),
            Header = new Wire.RoomHeaderSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived),
                new(x.FriendlyName, x.ReferenceId, x.InGameId, x.Contributors, x.Comments), x.RoomGroupId, x.SortOrder,
                new(x.SceneMinX, x.SceneMinY, x.SceneMaxX, x.SceneMaxY, x.SceneImagePixelsPerUnitX,
                    x.SceneImagePixelsPerUnitY, x.SceneImagePixelOffsetX, x.SceneImagePixelOffsetY,
                    x.UseSceneImage, x.UseUploadedSceneImage, x.IsSceneImageStale,
                    x.SceneImageOriginalHash, x.SceneImageCropHash, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc)))
                { CompletionStatus = (Wire.RoomStatus)x.CompletionStatus }
        }).ToListAsync();
        if (headers.Count != ids.Length) throw new InvalidOperationException("An affected room disappeared before its projection was read.");
        var subrooms = effect.Families.HasFlag(RoomProjectionFamilies.Subrooms)
            ? await db.Subrooms.AsNoTracking().Where(x => ids.Contains(x.RoomId)).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
                .Select(x => new { x.RoomId, Row = new Wire.RoomSubroomSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived), x.SortOrder,
                    new(x.FriendlyName, x.ReferenceId, x.Notes), new(x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth, x.SceneUnitHeight, x.EnableAnnotation)) }).ToListAsync() : null;
        var transitions = effect.Families.HasFlag(RoomProjectionFamilies.Transitions)
            ? await db.RoomTransitions.AsNoTracking().Where(x => ids.Contains(x.RoomId)).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
                .Select(x => new { x.RoomId, Row = new Wire.RoomTransitionSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived), x.SortOrder,
                    new(x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText,
                        x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.IsTodo, x.IsVerified, x.IsUnsafeArrival),
                    new(x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ,
                        x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY),
                    x.EnableAnnotation, x.RequirementsParseSucceeded, x.ResolvedSourceSubroomId,
                    x.ResolvedDestinationRoomId, x.ResolvedDestinationTransitionId) }).ToListAsync() : null;
        var connections = effect.Families.HasFlag(RoomProjectionFamilies.Connections)
            ? await db.SubroomConnections.AsNoTracking().Where(x => ids.Contains(x.RoomId)).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
                .Select(x => new { x.RoomId, Row = new Wire.RoomConnectionSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived), x.SortOrder,
                    new(x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText,
                        x.Requirements, x.Notes, x.IsTodo, x.IsVerified), x.SceneUnitX, x.SceneUnitY, x.EnableAnnotation,
                    x.RequirementsParseSucceeded, x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId) }).ToListAsync() : null;
        var checks = effect.Families.HasFlag(RoomProjectionFamilies.Checks)
            ? await db.CheckLocations.AsNoTracking().Where(x => ids.Contains(x.RoomId)).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
                .Select(x => new { x.RoomId, Row = new Wire.RoomCheckSnapshot(new(x.Id, DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc), x.IsArchived), x.SortOrder,
                    new(x.FriendlyName, x.SubroomReferenceText, x.Requirements, x.Notes, x.LocationType, x.IsTodo, x.IsVerified),
                    new(x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ,
                        x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY),
                    x.EnableAnnotation, x.RequirementsParseSucceeded, x.ResolvedSubroomId) }).ToListAsync() : null;
        var result = ImmutableArray.CreateBuilder<Wire.RoomDocumentDelta>();
        foreach (var header in headers)
        {
            var id = header.Observation.RoomId;
            var advance = effect.Advances.SingleOrDefault(x => x.RoomId == id);
            Wire.RoomDocumentObservation? covers = advance is not null && advance.After == header.Observation.CacheFlagUtc
                ? new(id, advance.Before) : null;
            var subroomRows = subrooms?.Where(x => x.RoomId == id).Select(x => x.Row).ToImmutableArray() ?? [];
            var transitionRows = transitions?.Where(x => x.RoomId == id).Select(x => x.Row).ToImmutableArray() ?? [];
            var connectionRows = connections?.Where(x => x.RoomId == id).Select(x => x.Row).ToImmutableArray() ?? [];
            var checkRows = checks?.Where(x => x.RoomId == id).Select(x => x.Row).ToImmutableArray() ?? [];
            var concerns = effect.SceneConcerns;
            var scene = concerns != Wire.RoomSceneConcerns.None && !header.Header.Observation.IsArchived
                ? await scenes.LoadAsync(db, id, CancellationToken.None) : null;
            if (concerns != Wire.RoomSceneConcerns.None && !header.Header.Observation.IsArchived && scene is null)
                throw new InvalidOperationException("The affected scene could not join its scalar projection snapshot.");
            // Removals belong to the source room. Cross-room resolver snapshots
            // cannot accidentally remove an unrelated local row with the same role.
            var removed = id == effect.SourceRoomId ? effect.Removed : new Wire.RoomRemovedRows([], [], [], []);
            result.Add(new(header.Observation, covers, RoomBundleAssembly.Complete(header.Header, images), subroomRows, transitionRows, connectionRows, checkRows,
                removed,
                subrooms is null ? null : Order(subroomRows.Select(x => (x.Observation.EntityId, x.Observation.IsArchived))),
                transitions is null ? null : Order(transitionRows.Select(x => (x.Observation.EntityId, x.Observation.IsArchived))),
                connections is null ? null : Order(connectionRows.Select(x => (x.Observation.EntityId, x.Observation.IsArchived))),
                checks is null ? null : new(
                    Order(checkRows.Where(x => !CheckLocationTypeCatalogue.IsEnemy(x.Values.LocationType)).Select(x => (x.Observation.EntityId, x.Observation.IsArchived))),
                    Order(checkRows.Where(x => CheckLocationTypeCatalogue.IsEnemy(x.Values.LocationType)).Select(x => (x.Observation.EntityId, x.Observation.IsArchived)))),
                concerns, scene));
        }
        return result.ToImmutable();
    }

    private static Wire.RoomOrderReplacement Order(IEnumerable<(Guid Id, bool Archived)> rows)
    {
        var values = rows.ToArray();
        return new(values.Where(x => !x.Archived).Select(x => x.Id).ToImmutableArray(),
            values.Where(x => x.Archived).Select(x => x.Id).ToImmutableArray());
    }
}
