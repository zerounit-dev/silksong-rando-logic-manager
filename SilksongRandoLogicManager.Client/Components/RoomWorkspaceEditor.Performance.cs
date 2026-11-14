namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private readonly OperationWork observedRoomWork = new();
    private long observedShellOperation;
    private OperationTiming? readinessTiming;
    private void ObserveShellOperation()
    {
        var current = Shell.PerformanceObservation;
        if (current == 0 || current == observedShellOperation) return;
        observedRoomWork.Reset();
        observedShellOperation = current;
    }
    private void ReportObservedRoomWork()
    {
        if (observedShellOperation == 0) return;
        observedRoomWork.Report(observedShellOperation);
        observedShellOperation = 0;
    }
    private TimingOutcome QueueTimingOutcome => !Current ? TimingOutcome.Retired
        : workspace.Queue.ProjectionPending ? TimingOutcome.KnownCommitFollowupFailed
        : workspace.Queue.TrackingUnavailable || workspace.Queue.RecoveryRequired ? TimingOutcome.Uncertain
        : workspace.Queue.Blocked is not null ? TimingOutcome.ReviewRequired : TimingOutcome.Complete;
}
