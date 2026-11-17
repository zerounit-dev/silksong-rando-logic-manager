using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed class RoomCreationIdentityCollisionException : Exception { }
internal sealed class RoomCreationMissingOwnerException : Exception { }

/// <summary>
/// Permanent R3-only admission at retained catalogue creation seams. Legacy callers
/// keep their existing identity/cancellation paths. R3 supplies a permanent ID and
/// a reliable in-memory receipt callback, not a replacement transaction or EF state
/// crossing the application boundary. Own this object for one creation only.
/// </summary>
internal sealed class RoomCreationAdmission(RoomEditingTracker.Entry entry, Wire.RoomQueuedOperation operation, Guid entityId)
{
    public Guid EntityId { get; } = entityId;
    public ImmutableArray<RoomDocumentAdvance> Advances { get; private set; } = [];
    public ImmutableArray<Guid> AffectedRoomIds { get; private set; } = [];
    private bool admitted;
    public SharedRoomCommitPlan? SharedPlan { get; private set; }

    public async Task CaptureAsync(LogicDbContext db, Guid roomId)
    {
        if (EntityId == Guid.Empty || roomId != entry.Identity.RoomId || admitted
            || entry.CurrentHead?.OperationId != operation.OperationId)
            throw new RoomTrackingProtocolException("Creation does not own the admitted tracking head.");
        if (!await db.Rooms.AsNoTracking().AnyAsync(x => x.Id == roomId, CancellationToken.None)) throw new RoomCreationMissingOwnerException();
        admitted = true;
    }

    public async Task CommitAsync(LogicDbContext db, LogicWriteScope transaction,
        SharedPublicationCoordinator.Admission publication, SharedPublicationCoordinator.PreparedPublication prepared)
    {
        if (!admitted) throw new RoomTrackingProtocolException("Creation was not admitted before its writes.");
        await transaction.FinalizeAsync(CancellationToken.None);
        // Capture affected scope while still in the existing transaction, before
        // starting Commit. Read failure here rolls back, never becomes a saved claim.
        AffectedRoomIds = transaction.FinalEffects.Rooms.Keys.Append(entry.Identity.RoomId).Distinct().ToImmutableArray();
        SharedPlan = await SharedRoomCommitPlan.PrepareAsync(db, transaction, prepared, CancellationToken.None, AffectedRoomIds);
        Advances = SharedPlan.Advances;
        var receipt = new Wire.RoomTerminalReceipt(operation.Sequence, operation.OperationId,
            Wire.RoomTerminalDisposition.Committed, EntityId, AffectedRoomIds, null) { SharedTimestamps = prepared.Timestamps,
                SharedOriginalRooms = SharedPlan.OriginalRooms };
        try
        {
            var history = await transaction.CommitAsync(() =>
            {
                try { entry.RecordReceipt(receipt); }
                finally { publication.PublishCommitted(prepared); }
            });
            if (history.Warning is { } warning) entry.RecordHistoryWarning(operation.Sequence, warning);
        }
        catch
        {
            // No replay if Commit may have taken effect without a reliable receipt.
            entry.Invalidate();
            throw;
        }
    }
}
