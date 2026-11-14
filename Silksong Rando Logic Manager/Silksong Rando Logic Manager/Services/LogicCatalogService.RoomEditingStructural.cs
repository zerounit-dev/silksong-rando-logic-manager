using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class LogicCatalogService
{
    internal async Task<RoomMutationEffect?> DeleteTrackedRoomAsync(RoomEditingTracker.Entry tracker)
    {
        if (tracker.CurrentHead?.Operation is not Wire.DeleteRoom)
            throw new RoomTrackingProtocolException("The head is not a room deletion.");
        var roomId = tracker.Identity.RoomId;
        var sequence = tracker.CurrentHead.Sequence;
        // Preserve the shared lifecycle-before-DB order and postcommit-only cleanup.
        using var imageAdmission = sceneImages is null ? null : await sceneImages.AcquireLifecycleAsync(CancellationToken.None);
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var room = await db.Rooms.SingleOrDefaultAsync(x => x.Id == roomId);
        if (room is null) { RoomEditingMutationScope.Missing(tracker, roomId); return null; }
        if (!room.IsArchived)
        {
            RoomEditingMutationScope.Corrective(tracker, roomId, "Only an archived room can be permanently deleted. Copy pending work before reloading.");
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        var transitions = await db.RoomTransitions.Where(x => x.RoomId == roomId).ToListAsync();
        var transitionIds = transitions.Select(x => x.Id).ToArray();
        var subrooms = await db.Subrooms.Where(x => x.RoomId == roomId).ToListAsync();
        var connections = await db.SubroomConnections.Where(x => x.RoomId == roomId).ToListAsync();
        var checks = await db.CheckLocations.Where(x => x.RoomId == roomId).ToListAsync();
        await db.RoomTransitions.Where(x => !transitionIds.Contains(x.Id) && x.ResolvedDestinationRoomId == roomId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedDestinationRoomId, (Guid?)null));
        await db.RoomTransitions.Where(x => !transitionIds.Contains(x.Id) && x.ResolvedDestinationTransitionId != null
            && transitionIds.Contains(x.ResolvedDestinationTransitionId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedDestinationTransitionId, (Guid?)null));
        await db.MapScenes.Where(x => x.ResolvedRoomId == roomId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedRoomId, (Guid?)null));
        db.RemoveRange(checks); db.RemoveRange(connections); db.RemoveRange(transitions); db.RemoveRange(subrooms); db.Remove(room);
        await db.SaveChangesAsync(CancellationToken.None);
        var effect = await scope.CompleteAsync(db, tx, RoomProjectionFamilies.Transitions, Wire.RoomSceneConcerns.None);
        if (sceneImages is not null)
        {
            List<Exception> failures = [];
            try { await sceneImages.DeleteAsync(roomId, CancellationToken.None); } catch (Exception failure) { failures.Add(failure); }
            try { await sceneImages.DeleteOriginalAsync(roomId, CancellationToken.None); } catch (Exception failure) { failures.Add(failure); }
            if (failures.Count != 0)
            {
                tracker.RecordCleanupWarning(sequence, "Room deleted; image cleanup failed. See server diagnostics.");
                // Keep technical cleanup failures on the normal server logging path.
                throw new RoomImageDeletionCleanupException(TimeSpan.Zero, failures);
            }
        }
        return effect with { DeletedRoomIds = [roomId] };
    }

    internal async Task<RoomMutationEffect?> SetTrackedArchiveAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var operation = tracker.CurrentHead?.Operation;
        var (id, archived) = operation switch
        {
            Wire.SetRoomArchive x => (tracker.Identity.RoomId, x.Archived),
            Wire.SetRoomSubroomArchive x => (x.EntityId, x.Archived),
            Wire.SetRoomTransitionArchive x => (x.EntityId, x.Archived),
            Wire.SetRoomConnectionArchive x => (x.EntityId, x.Archived),
            Wire.SetRoomCheckArchive x => (x.EntityId, x.Archived),
            _ => throw new RoomTrackingProtocolException("The head is not an archive/restore operation.")
        };
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        ArchivableEntity? row = operation switch
        {
            Wire.SetRoomArchive => await db.Rooms.SingleOrDefaultAsync(x => x.Id == id),
            Wire.SetRoomSubroomArchive => await db.Subrooms.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId),
            Wire.SetRoomTransitionArchive => await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId),
            Wire.SetRoomConnectionArchive => await db.SubroomConnections.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId),
            Wire.SetRoomCheckArchive => await db.CheckLocations.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId),
            _ => null
        };
        if (row is null) { RoomEditingMutationScope.Missing(tracker, id); return null; }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        var changed = row.IsArchived != archived;
        row.IsArchived = archived;
        await db.SaveChangesAsync(CancellationToken.None);
        if (changed && !archived)
        {
            // Same explicit restoration validation as the retained service. A
            // satisfied repeated restore does not become an implicit recheck.
            switch (row)
            {
                case Room room:
                    var context = await requirementStatus.LoadContextAsync(db, CancellationToken.None);
                    var transitions = await db.RoomTransitions.Where(x => x.RoomId == room.Id && !x.IsArchived).ToListAsync();
                    var connections = await db.SubroomConnections.Where(x => x.RoomId == room.Id && !x.IsArchived).ToListAsync();
                    var checks = await db.CheckLocations.Where(x => x.RoomId == room.Id && !x.IsArchived).ToListAsync();
                    foreach (var x in transitions) ApplyRequirementsStatus(context, x, RequirementBearingRowKind.Transition);
                    foreach (var x in connections) ApplyRequirementsStatus(context, x, RequirementBearingRowKind.Connection);
                    foreach (var x in checks) ApplyRequirementsStatus(context, x, RequirementBearingRowKind.Check);
                    using (db.SuppressAuditMetadata()) await db.SaveChangesAsync(CancellationToken.None);
                    break;
                case RoomTransition x: await ValidateRequirementsAsync(db, x, RequirementBearingRowKind.Transition, CancellationToken.None); break;
                case SubroomConnection x: await ValidateRequirementsAsync(db, x, RequirementBearingRowKind.Connection, CancellationToken.None); break;
                case CheckLocation x: await ValidateRequirementsAsync(db, x, RequirementBearingRowKind.Check, CancellationToken.None); break;
            }
        }
        var family = row switch
        {
            Room => RoomProjectionFamilies.All,
            Subroom => RoomProjectionFamilies.Subrooms,
            RoomTransition => RoomProjectionFamilies.Transitions,
            SubroomConnection => RoomProjectionFamilies.Connections,
            CheckLocation => RoomProjectionFamilies.Checks,
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        var concerns = Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport;
        if (row is Room) concerns |= Wire.RoomSceneConcerns.BoundsImageAction | Wire.RoomSceneConcerns.ImageDisplay;
        return await scope.CompleteAsync(db, tx, family, concerns);
    }

    internal async Task<RoomMutationEffect?> ReorderTrackedChildAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var operation = tracker.CurrentHead?.Operation;
        var (id, target) = operation switch
        {
            Wire.ReorderRoomSubroom x => (x.EntityId, x.TargetIndex),
            Wire.ReorderRoomTransition x => (x.EntityId, x.TargetIndex),
            Wire.ReorderRoomConnection x => (x.EntityId, x.TargetIndex),
            Wire.ReorderRoomCheck x => (x.EntityId, x.TargetIndex),
            _ => throw new RoomTrackingProtocolException("The head is not a child reorder.")
        };
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        var family = RoomProjectionFamilies.None;
        var concerns = Wire.RoomSceneConcerns.None;
        switch (operation)
        {
            case Wire.ReorderRoomSubroom:
                var subrooms = await db.Subrooms.Where(x => x.RoomId == tracker.Identity.RoomId && !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
                if (!MoveTrackedRows(subrooms, id, target, x => x.Id, (x, order) => x.SortOrder = order))
                { RoomEditingMutationScope.Missing(tracker, id); return null; }
                family = RoomProjectionFamilies.Subrooms;
                break;
            case Wire.ReorderRoomTransition:
                var transitions = await db.RoomTransitions.Where(x => x.RoomId == tracker.Identity.RoomId && !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
                if (!MoveTrackedRows(transitions, id, target, x => x.Id, (x, order) => x.SortOrder = order))
                { RoomEditingMutationScope.Missing(tracker, id); return null; }
                family = RoomProjectionFamilies.Transitions;
                break;
            case Wire.ReorderRoomConnection:
                var connections = await db.SubroomConnections.Where(x => x.RoomId == tracker.Identity.RoomId && !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
                var moving = connections.SingleOrDefault(x => x.Id == id);
                if (moving is null) { RoomEditingMutationScope.Missing(tracker, id); return null; }
                var canonical = connections.First(x => string.Equals(x.Alias.Trim(), moving.Alias.Trim(), StringComparison.OrdinalIgnoreCase)).Id;
                MoveTrackedRows(connections, id, target, x => x.Id, (x, order) => x.SortOrder = order);
                if (!string.IsNullOrWhiteSpace(moving.Alias) && canonical != connections.First(x =>
                    string.Equals(x.Alias.Trim(), moving.Alias.Trim(), StringComparison.OrdinalIgnoreCase)).Id)
                    concerns = Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport;
                family = RoomProjectionFamilies.Connections;
                break;
            case Wire.ReorderRoomCheck checkOrder:
                var checks = await OrderedActiveChecksAsync(db, tracker.Identity.RoomId, CancellationToken.None);
                var section = checks.Where(x => CheckLocationTypeCatalogue.IsEnemy(x.LocationType) == checkOrder.Enemies).ToList();
                if (section.All(x => x.Id != id))
                {
                    RoomEditingMutationScope.Corrective(tracker, id,
                        "This check is no longer active in the captured section. Copy pending work before reloading.");
                    return null;
                }
                if (target < 0 || target > section.Count)
                {
                    RoomEditingMutationScope.Corrective(tracker, id,
                        "This reorder position is no longer available. Copy pending work before reloading.");
                    return null;
                }
                MoveTrackedRows(section, id, target, x => x.Id, (_, _) => { });
                var opposite = checks.Where(x => CheckLocationTypeCatalogue.IsEnemy(x.LocationType) != checkOrder.Enemies);
                NormalizeCheckOrder(checkOrder.Enemies ? opposite.Concat(section).ToList() : section.Concat(opposite).ToList());
                family = RoomProjectionFamilies.Checks;
                break;
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return await scope.CompleteAsync(db, tx, family, concerns);
    }

    private static bool MoveTrackedRows<T>(List<T> rows, Guid id, int target, Func<T, Guid> identity, Action<T, int> setOrder)
    {
        var index = rows.FindIndex(x => identity(x) == id);
        if (index < 0) return false;
        var moving = rows[index]; rows.RemoveAt(index);
        if (index < target) target--;
        rows.Insert(Math.Clamp(target, 0, rows.Count), moving);
        for (var i = 0; i < rows.Count; i++) setOrder(rows[i], i);
        return true;
    }

    internal async Task<RoomMutationEffect?> DeleteTrackedChildAsync(RoomEditingTracker.Entry tracker)
    {
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(CancellationToken.None);
        using var publicationLease = publication;
        var operation = tracker.CurrentHead?.Operation;
        var id = operation switch
        {
            Wire.DeleteRoomSubroom x => x.ConfirmedArchivedRow.EntityId,
            Wire.DeleteRoomTransition x => x.ConfirmedArchivedRow.EntityId,
            Wire.DeleteRoomConnection x => x.ConfirmedArchivedRow.EntityId,
            Wire.DeleteRoomCheck x => x.ConfirmedArchivedRow.EntityId,
            _ => throw new RoomTrackingProtocolException("The head is not a child deletion.")
        };
        await using var db = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await using var tx = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        ArchivableEntity? row = operation switch
        {
            Wire.DeleteRoomSubroom => await db.Subrooms.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId),
            Wire.DeleteRoomTransition => await db.RoomTransitions.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId),
            Wire.DeleteRoomConnection => await db.SubroomConnections.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId),
            Wire.DeleteRoomCheck => await db.CheckLocations.SingleOrDefaultAsync(x => x.Id == id && x.RoomId == tracker.Identity.RoomId),
            _ => null
        };
        if (row is null) { RoomEditingMutationScope.Missing(tracker, id); return null; }
        if (!row.IsArchived)
        {
            RoomEditingMutationScope.Corrective(tracker, id, "Restore/archive state changed. Only an archived record can be permanently deleted. Copy pending work before reloading.");
            return null;
        }
        var scope = await RoomEditingMutationScope.CaptureAsync(db, tracker, publication);
        var family = RoomProjectionFamilies.None;
        var removed = new Wire.RoomRemovedRows([], [], [], []);
        switch (row)
        {
            case Subroom:
                await db.RoomTransitions.Where(x => x.ResolvedSourceSubroomId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedSourceSubroomId, (Guid?)null));
                await db.SubroomConnections.Where(x => x.ResolvedSourceSubroomId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedSourceSubroomId, (Guid?)null));
                await db.SubroomConnections.Where(x => x.ResolvedDestinationSubroomId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedDestinationSubroomId, (Guid?)null));
                await db.CheckLocations.Where(x => x.ResolvedSubroomId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedSubroomId, (Guid?)null));
                family = RoomProjectionFamilies.All; removed = removed with { Subrooms = [id] };
                break;
            case RoomTransition:
                await db.RoomTransitions.Where(x => x.Id != id && x.ResolvedDestinationTransitionId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResolvedDestinationTransitionId, (Guid?)null));
                family = RoomProjectionFamilies.Transitions; removed = removed with { Transitions = [id] };
                break;
            case SubroomConnection: family = RoomProjectionFamilies.Connections; removed = removed with { Connections = [id] }; break;
            case CheckLocation: family = RoomProjectionFamilies.Checks; removed = removed with { Checks = [id] }; break;
        }
        db.Remove(row);
        await db.SaveChangesAsync(CancellationToken.None);
        return await scope.CompleteAsync(db, tx, family, Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport, removed);
    }
}
