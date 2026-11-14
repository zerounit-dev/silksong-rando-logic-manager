using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

[Flags]
internal enum RoomProjectionFamilies { None = 0, Subrooms = 1, Transitions = 2, Connections = 4, Checks = 8, All = 15 }

internal sealed record RoomMutationEffect(Guid SourceRoomId, ImmutableArray<Guid> AffectedRoomIds,
    ImmutableArray<RoomDocumentAdvance> Advances, RoomProjectionFamilies Families,
    Wire.RoomSceneConcerns SceneConcerns, Wire.RoomRemovedRows Removed)
{
    public ImmutableArray<Guid> DeletedRoomIds { get; init; } = [];
    public SharedRoomCommitPlan? SharedPlan { get; init; }
}

/// <summary>
/// Commit accounting for one logical R3 command, never a batch transaction. The
/// final capture identifies persisted resolver effects without a world inventory
/// or treating a cache flag as save permission.
/// </summary>
internal sealed class RoomEditingMutationScope
{
    private readonly RoomEditingTracker.Entry entry;
    private readonly Wire.RoomQueuedOperation operation;
    private readonly SharedPublicationCoordinator.Admission publication;

    private RoomEditingMutationScope(RoomEditingTracker.Entry entry, Wire.RoomQueuedOperation operation,
        SharedPublicationCoordinator.Admission publication)
    {
        this.entry = entry; this.operation = operation; this.publication = publication;
    }

    public static Task<RoomEditingMutationScope> CaptureAsync(LogicDbContext db, RoomEditingTracker.Entry entry,
        SharedPublicationCoordinator.Admission publication)
    {
        var operation = entry.CurrentHead ?? throw new RoomTrackingProtocolException("There is no admitted room operation.");
        return Task.FromResult(new RoomEditingMutationScope(entry, operation, publication));
    }

    public async Task<RoomMutationEffect> CompleteAsync(LogicDbContext db, LogicWriteScope transaction,
        RoomProjectionFamilies families, Wire.RoomSceneConcerns sceneConcerns,
        Wire.RoomRemovedRows? removed = null, Guid? createdId = null)
    {
        await transaction.FinalizeAsync(CancellationToken.None);
        var effects = transaction.FinalEffects;
        var changed = effects.Rooms.Keys.ToImmutableArray();
        var affected = changed.Append(entry.Identity.RoomId).Distinct().ToImmutableArray();
        var prepared = publication.PrepareAdvance(effects.RoomFactsChanged, false);
        var plan = await SharedRoomCommitPlan.PrepareAsync(db, transaction, prepared, CancellationToken.None, affected);
        var receipt = new Wire.RoomTerminalReceipt(operation.Sequence, operation.OperationId,
            changed.IsEmpty ? Wire.RoomTerminalDisposition.Satisfied : Wire.RoomTerminalDisposition.Committed,
            createdId, affected, null) { SharedTimestamps = prepared.Timestamps,
                SharedOriginalRooms = plan.OriginalRooms };
        var effect = new RoomMutationEffect(entry.Identity.RoomId, affected, plan.Advances, families,
            changed.IsEmpty ? Wire.RoomSceneConcerns.None : sceneConcerns, removed ?? new([], [], [], []))
        { SharedPlan = plan, DeletedRoomIds = effects.Rooms.Where(x => x.Value.Final is null).Select(x => x.Key).ToImmutableArray() };
        // Everything needed for the receipt is prepared before Commit. No read,
        // notification or projection can intervene after a known successful commit.
        try
        {
            await transaction.CommitAsync(() =>
            {
                try { entry.RecordReceipt(receipt); }
                finally { publication.PublishCommitted(prepared); }
            });
        }
        catch
        {
            entry.Invalidate();
            throw;
        }
        return effect;
    }

    public static void Block(RoomEditingTracker.Entry entry, Wire.RoomBlockedDisposition disposition,
        string message, Wire.RoomHeadEvidence evidence)
    {
        var head = entry.CurrentHead ?? throw new RoomTrackingProtocolException("There is no admitted room operation.");
        entry.RecordBlocked(new(head.Sequence, head.OperationId, disposition, Guid.NewGuid(),
            (entry.Blocked?.Revision ?? 0) + 1, message, evidence));
    }

    public static void Missing(RoomEditingTracker.Entry entry, Guid entityId) => Block(entry,
        Wire.RoomBlockedDisposition.MissingBlocked,
        "This record is no longer available in this room. Copy pending work before reloading.",
        new Wire.RoomReloadRequiredEvidence(entityId));

    public static void Corrective(RoomEditingTracker.Entry entry, Guid entityId, string message) => Block(entry,
        Wire.RoomBlockedDisposition.CorrectiveBlocked, message, new Wire.RoomReloadRequiredEvidence(entityId));
}
