using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>R3-only reliable receipt seam within the retained image publisher.</summary>
internal sealed class RoomImageCommitAdmission
{
    private readonly RoomEditingTracker.Entry entry;
    private readonly Wire.RoomTerminalReceipt receipt;
    public bool BoundsChanged { get; private set; }
    public SharedRoomCommitPlan? SharedPlan { get; set; }

    public RoomImageCommitAdmission(RoomEditingTracker.Entry entry)
    {
        this.entry = entry;
        var head = entry.CurrentHead ?? throw new RoomTrackingProtocolException("No image operation is admitted.");
        receipt = new(head.Sequence, head.OperationId, Wire.RoomTerminalDisposition.Committed, null, [entry.Identity.RoomId], null);
    }

    public async Task<int> ExecuteConditionalAsync(Func<Task<int>> write)
    {
        try { return await write(); }
        catch { entry.Invalidate(); throw; }
    }

    public async Task CommitAsync(LogicWriteScope transaction, bool boundsChanged, SharedPublicationCoordinator.Admission publication,
        SharedRoomCommitPlan plan,
        bool changed = true, Action? durable = null)
    {
        var terminal = (changed ? receipt : receipt with { Disposition = Wire.RoomTerminalDisposition.Satisfied })
            with { SharedTimestamps = plan.Publication.Timestamps,
                SharedOriginalRooms = System.Collections.Immutable.ImmutableArray.CreateRange(
                    plan.OriginalRooms.Where(x => x.RoomId == entry.Identity.RoomId)) };
        try
        {
            var history = await transaction.CommitAsync(() =>
            {
                durable?.Invoke();
                BoundsChanged = boundsChanged;
                try { entry.RecordReceipt(terminal); }
                finally { publication.PublishCommitted(plan.Publication); }
            });
            if (history.Warning is { } warning) entry.RecordHistoryWarning(terminal.Sequence, warning);
        }
        catch { entry.Invalidate(); throw; }
    }

}
