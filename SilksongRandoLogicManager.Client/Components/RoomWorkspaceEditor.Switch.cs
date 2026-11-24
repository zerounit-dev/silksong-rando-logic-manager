using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    [Parameter] public App.RoomSwitch? Switch { get; set; }
    private App.RoomSwitch? outgoingSwitch;
    private bool presentationRetired, switchReady, switchSetupFailed;
    private Task? presentationRetirement;
    private AreaMapSurface? areaMap;
    private bool SwitchPending => presentationRetired ? outgoingSwitch?.Pending == true
        : Switch?.Pending == true && Switch.Target == Entry.Owner && !switchReady && !switchSetupFailed;
    private string TitleStatusIcon => SwitchPending ? "fa-spinner fa-spin" : StatusIcon;
    private string TitleStatusClass => SwitchPending ? "secondary" : StatusClass;
    private string TitleStatusLabel => SwitchPending ? $"Opening room {(outgoingSwitch ?? Switch)!.Destination:D}" : StatusLabel;

    public Task RetirePresentationAsync(App.RoomSwitch? pending = null)
    {
        if (presentationRetirement is not null) return presentationRetirement;
        outgoingSwitch ??= pending;
        presentationRetired = true;
        RetireHistory();
        RetireEntryRequests();
        // Invalidate native queued composition continuations before any await.
        try { nativeOwner?.InvokeVoid("retireRoomComposition"); }
        catch (Exception exception) { Console.Error.WriteLine(exception); }
        readinessTiming?.End(TimingOutcome.Retired);
        imageGeneration++;
        lifetime.Cancel();
        pendingBoundaryFocus = null; returnFocusFallback = null; focusComments = false;
        ReleaseRoomWorkspace();
        if (!disposed) StateHasChanged();
        return presentationRetirement ??= RetirePresentationCoreAsync();
    }
    private async Task RetirePresentationCoreAsync()
    {
        // Mark both child hosts before the first await. They retain DOM but can
        // never initialize or acquire callback/focus ownership again.
        var scene = scenePane?.RetirePresentationAsync() ?? Task.CompletedTask;
        var map = areaMap?.RetirePresentationAsync() ?? Task.CompletedTask;
        try { await Task.WhenAll(scene, map); }
        finally { await RetireOwnersAsync(); }
    }
    private void ReleaseRoomWorkspace()
    {
        if (workspace.IsAdmitted && workspace.Queue.HasUnresolvedSentOutcome) Runtime.RetainUncertainMutation();
        Shell.ReleaseRoomWorkspace(this);
    }
    private void RetainReleasedRoomUncertainty()
    {
        if ((presentationRetired || disposed) && workspace.Queue.HasUnresolvedSentOutcome) Runtime.RetainUncertainMutation();
    }
    private void ObserveResolutionDispatch(SilksongRandoLogicManager.Contracts.RoomResolveRequest request)
    {
        workspace.Queue.MarkResolutionSent(request);
        RetainReleasedRoomUncertainty();
    }
    private void FailSwitchSetup()
    {
        switchSetupFailed = true;
        Shell.EndRoomSwitch(Switch, Entry.Owner);
    }
    private bool CompleteSwitchSetup()
    {
        if (switchReady) return false;
        switchReady = true;
        Shell.EndRoomSwitch(Switch, Entry.Owner);
        return true;
    }
    private void ToggleArchivedContents() { if (Current) showArchived = !showArchived; }
    private bool SceneSetupAllowed() => Current && !Shell.GeneralErrorOpen;
    private async Task SceneFailedAsync(Guid room, long generation, Exception exception)
    {
        if (!Current || room != Entry.RoomId || generation != sceneOwnerGeneration) return;
        initialReadinessFailed = true;
        readinessTiming?.End(TimingOutcome.FailedOrRetired);
        // ReportRoomErrorAsync sets the existing App error guard synchronously
        // before its first await. Close admission before ending the switch or
        // requesting a render; dismissal, not the report, permits another attempt.
        var report = Shell.ReportRoomErrorAsync(exception, "The room editor could not update its browser controls. Pending work has been retained.");
        FailSwitchSetup();
        StateHasChanged();
        await report;
    }
}
