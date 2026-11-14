namespace SilksongRandoLogicManager.Client;

public partial class App
{
    private OperationTiming? shellRenderObservation;
    private OperationTiming? roomEntryObservation;
    private long roomEntryObservationStart;
    public OperationTiming? TakeRoomEntryTiming(long started)
    {
        if (roomEntryObservationStart != started) return null;
        var value = roomEntryObservation;
        roomEntryObservation = null;
        return value;
    }
    public long PerformanceObservation => shellRenderObservation?.Id ?? 0;
    private void BeginShellObservation(string stage)
    {
        shellRenderObservation?.End(TimingOutcome.Retired);
        shellRenderObservation = OperationTiming.Observe(stage);
    }
    private void CompleteShellObservation()
    {
        shellRenderObservation?.End(disposed ? TimingOutcome.Retired : TimingOutcome.Complete);
        shellRenderObservation = null;
    }
}
