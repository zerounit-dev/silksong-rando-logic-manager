using SilksongRandoLogicManager.Client.RoomEditing;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private bool observingRoomResponse, roomResponseObservationFailed;
    private App.RoomActionState RoomQueueActionState()
    {
        if (!Current) return default;
        if (workspace.AdmittedQueue is not { } queue) return new(false, false, true);
        var failed = roomResponseObservationFailed || workspace.AcceptanceFailed || queue.RecoveryRequired
            || queue.TrackingUnavailable || queue.Blocked is not null || queue.ProjectionPending
            || queue.QueuedThrough != 0 && (initialReadinessFailed || nativeOwner is null || !browserReady);
        var q = queue.QueuedThrough;
        var unfinished = q != 0 && (failed || observingRoomResponse || queue.RequestOutstanding
            || q != queue.SubmittedThrough || q != queue.RegisteredThrough || q != queue.TerminalThrough
            || q != queue.IncorporatedThrough || q != queue.AcknowledgedThrough);
        return new(unfinished, failed, true);
    }
    private void NotifyRoomActions()
    {
        try { Shell.ObserveRoomActions(this); }
        catch
        {
            roomResponseObservationFailed = true;
            Shell.FailRoomActionObservation(this);
            throw;
        }
    }
    private void ObserveRoomRequestFailure()
    {
        // Optional map/freshness follow-up after ACK is not a failed mandatory
        // response observer and must not manufacture a queue obligation.
        if (workspace.Queue.RecoveryRequired || workspace.Queue.TrackingUnavailable || workspace.Queue.HasPendingIntent)
            roomResponseObservationFailed = true;
        NotifyRoomActions();
    }
    private void ObserveAppendedRoomIntent() => Shell.PublishRoomQueueWarning(this);
}
