using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

// Read-only persistence boundary for later comparison preparation. It projects
// scalars directly and returns no entity or DbContext to callers.
public sealed class DistributedImportProjectionLoader(IDbContextFactory<LogicDbContext> dbContextFactory)
{
    public async Task<DistributedImportRoomProjection?> LoadRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await LoadRoomAsync(db, roomId, cancellationToken);
    }

    internal static async Task<DistributedImportRoomProjection?> LoadRoomAsync(LogicDbContext db, Guid roomId, CancellationToken cancellationToken = default)
    {
        return await db.Rooms.AsNoTracking().Where(x => x.Id == roomId).Select(x => new DistributedImportRoomProjection(
            x.Id, x.RoomGroupId, x.ReferenceId, x.FriendlyName, x.InGameId, x.Contributors, x.Comments, x.SceneUnitWidth, x.SceneUnitHeight, x.SceneImageScaleXPercent, x.SceneImageScaleYPercent, x.SceneImagePanXPercent, x.SceneImagePanYPercent, x.IsSceneImageStale, x.SortOrder, x.IsArchived, x.ArchivedUtc, x.CreatedUtc, x.UpdatedUtc,
            x.Subrooms.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedImportSubroomProjection(y.Id, y.ReferenceId, y.FriendlyName, y.Notes, y.SceneUnitX, y.SceneUnitY, y.SceneUnitWidth, y.SceneUnitHeight, y.EnableAnnotation, y.SortOrder, y.IsArchived, y.ArchivedUtc, y.CreatedUtc, y.UpdatedUtc)).ToList(),
            x.Transitions.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedImportTransitionProjection(y.Id, y.Alias, y.FriendlyName, y.InGameId, y.InGamePositionX, y.InGamePositionY, y.InGamePositionZ, y.LocalPositionX, y.LocalPositionY, y.LocalPositionZ, y.AnnotationSceneUnitX, y.AnnotationSceneUnitY, y.EnableAnnotation, y.SourceSubroomReferenceText, y.DestinationRoomReferenceText, y.DestinationTransitionAliasText, y.Requirements, y.Notes, y.SortOrder, y.IsTodo, y.IsVerified, y.IsArchived, y.ArchivedUtc, y.CreatedUtc, y.UpdatedUtc)).ToList(),
            x.Connections.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedImportConnectionProjection(y.Id, y.Alias, y.FriendlyName, y.SourceSubroomReferenceText, y.DestinationSubroomReferenceText, y.Requirements, y.Notes, y.EnableAnnotation, y.SceneUnitX, y.SceneUnitY, y.SortOrder, y.IsTodo, y.IsVerified, y.IsArchived, y.ArchivedUtc, y.CreatedUtc, y.UpdatedUtc)).ToList(),
            x.CheckLocations.OrderBy(y => y.SortOrder).ThenBy(y => y.Id).Select(y => new DistributedImportCheckProjection(y.Id, y.FriendlyName, y.InGameId, y.InGamePositionX, y.InGamePositionY, y.InGamePositionZ, y.LocalPositionX, y.LocalPositionY, y.LocalPositionZ, y.AnnotationSceneUnitX, y.AnnotationSceneUnitY, y.SubroomReferenceText, y.Requirements, y.Notes, y.LocationType, y.EnableAnnotation, y.SortOrder, y.IsTodo, y.IsVerified, y.IsArchived, y.ArchivedUtc, y.CreatedUtc, y.UpdatedUtc)).ToList())).SingleOrDefaultAsync(cancellationToken);
    }
}
