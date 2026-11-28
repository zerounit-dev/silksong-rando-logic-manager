using Microsoft.EntityFrameworkCore;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class LogicCatalogService
{
    internal async Task<RoomMutationEffect?> RecheckTrackedRoomAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        if (tracker.CurrentHead?.Operation is not Wire.RecheckRoomRequirements)
            throw new RoomTrackingProtocolException("The head is not a current-room requirement recheck.");
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var roomId = tracker.Identity.RoomId;
        var room = await db.Rooms.AsNoTracking().Where(x => x.Id == roomId)
            .Select(x => new { x.IsArchived }).SingleOrDefaultAsync();
        if (room is null) { RoomEditingMutationScope.Missing(tracker, roomId); return null; }
        if (room.IsArchived)
        {
            RoomEditingMutationScope.Corrective(tracker, roomId,
                "Room logic can only be rechecked in an active room. Copy pending work before reloading.");
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        // One fresh recognition/dependency context for the whole operation, with
        // the retained safe false/null policy. No browser-derived validation and
        // no per-row vocabulary query or validation of other rooms' authored rows.
        var context = await requirementStatus.LoadContextAsync(db, roomId, CancellationToken.None);
        var transitions = await db.RoomTransitions.Where(x => x.RoomId == roomId && !x.IsArchived).ToListAsync();
        var connections = await db.SubroomConnections.Where(x => x.RoomId == roomId && !x.IsArchived).ToListAsync();
        var checks = await db.CheckLocations.Where(x => x.RoomId == roomId && !x.IsArchived).ToListAsync();
        foreach (var row in transitions) ApplyRequirementsStatus(context, row, RequirementBearingRowKind.Transition);
        foreach (var row in connections) ApplyRequirementsStatus(context, row, RequirementBearingRowKind.Connection);
        foreach (var row in checks) ApplyRequirementsStatus(context, row, RequirementBearingRowKind.Check);
        using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(CancellationToken.None);
        // Complete active snapshots supply the existing successful/failed/unknown
        // counts together with statuses, without a separately sampled count read.
        return await scope.CompleteAsync(db, tx,
            RoomProjectionFamilies.Transitions | RoomProjectionFamilies.Connections | RoomProjectionFamilies.Checks,
            Wire.RoomSceneConcerns.None);
    }
}
