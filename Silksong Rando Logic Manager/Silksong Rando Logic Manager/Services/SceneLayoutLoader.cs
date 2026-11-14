using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public interface ISceneLayoutLoader
{
    Task<SceneLayoutView?> LoadAsync(Guid roomId, CancellationToken cancellationToken);
    /// <summary>Coordinator-only scalar batch operation; components never receive a context.</summary>
    Task<SceneLayoutView?> LoadAsync(LogicDbContext db, Guid roomId, CancellationToken cancellationToken)
        => throw new NotSupportedException("This scene loader cannot join a logical-room snapshot.");
}

/// <summary>Phase-4 scalar-only scene projection; it deliberately has no map or component dependency.</summary>
public sealed class SceneLayoutLoader(IDbContextFactory<LogicDbContext> contexts, Authentication.LiveAccess access, SceneImageFileService? images = null) : ISceneLayoutLoader
{
    public async Task<SceneLayoutView?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
    {
        await access.RequireAsync(token: cancellationToken);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await db.Database.BeginTransactionAsync(cancellationToken);
        return await LoadAsync(db, roomId, cancellationToken);
    }

    public async Task<SceneLayoutView?> LoadAsync(LogicDbContext db, Guid roomId, CancellationToken cancellationToken)
    {
        var room = await db.Rooms.AsNoTracking().Where(x => x.Id == roomId)
            .Select(x => new SceneRoomLoad(x.Id, x.IsArchived, x.SceneMinX, x.SceneMinY, x.SceneMaxX, x.SceneMaxY, x.SceneImagePixelsPerUnitX, x.SceneImagePixelsPerUnitY, x.SceneImagePixelOffsetX, x.SceneImagePixelOffsetY, x.UseSceneImage, x.UseUploadedSceneImage, x.IsSceneImageStale, x.UpdatedUtc)).SingleOrDefaultAsync(cancellationToken);
        if (room is null || room.Archived) return null;
        var subrooms = await db.Subrooms.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived)
            .Select(x => new SceneSubroomLoad(x.Id, x.FriendlyName, x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth, x.SceneUnitHeight, x.EnableAnnotation)).ToListAsync(cancellationToken);
        var transitions = await db.RoomTransitions.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived && x.EnableAnnotation)
            .Select(x => new SceneTransitionLoad(x.Id, x.Alias, x.FriendlyName, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.ResolvedSourceSubroomId)).ToListAsync(cancellationToken);
        var checks = await db.CheckLocations.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived && x.EnableAnnotation)
            .Select(x => new SceneCheckLoad(x.Id, x.FriendlyName, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.ResolvedSubroomId, x.LocationType)).ToListAsync(cancellationToken);
        var connections = await db.SubroomConnections.AsNoTracking().Where(x => x.RoomId == roomId && !x.IsArchived)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new SceneConnectionLoad(x.Id, x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText, x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId, x.EnableAnnotation, x.SceneUnitX, x.SceneUnitY)).ToListAsync(cancellationToken);
        return SceneLayoutMapper.Map(room, subrooms, transitions, checks, connections, room.UseSceneImage && images?.Exists(room.Id) == true);
    }
}
