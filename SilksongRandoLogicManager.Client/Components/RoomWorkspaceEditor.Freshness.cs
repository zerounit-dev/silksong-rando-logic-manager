using System.Collections.Immutable;
using Microsoft.JSInterop;
using SilksongRandoLogicManager.Client.RoomEditing;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private bool deferredRoomPull, roomPullInFlight, roomPullActive, roomPullFailed, externalRoomDeleted;
    private long roomReadGeneration;

    [JSInvokable]
    public Task RoomNativeCompositionEnded()
    {
        // This callback never installs or saves. The existing reconciliation path
        // rechecks this mounted visit, all local work and actual native composition.
        if (!Current || !workspace.IsAdmitted || nativeOwner is null) return Task.CompletedTask;
        return ReconcileFreshnessAsync();
    }

    private bool ClientUpdateWorkAtRisk()
    {
        // Unlike ordinary departure, passive guide/map/entry loading is not work.
        // Called synchronously BEFORE Runtime.Retired changes Current or native cleanup.
        if (!Current || roomResponseObservationFailed || workspace.AcceptanceFailed || keyboardUnsentFailure
            || HasManagedKeyboardDraft || localModal is not null || imageDraft is not null || imageBusy
            || imageUploadUncertain || scenePlacement is not null || editingComments || draining || controlPending) return true;
        var actions = RoomQueueActionState();
        if (actions.Failed || actions.Unfinished) return true;
        // Tracking/initial native setup cannot have admitted edits before controls are ready.
        if (!workspace.IsAdmitted) return false;
        if (nativeOwner is null) return browserReady || initialEnabledRenderCompleted || initialReadinessFailed;
        if (nativeOwner.Invoke<bool>("hasRoomAuthoringGesture")) return true;
        if (NativeKeyboardSnapshot()?.Drafts.Any(x => x.State != "incorporated") == true) return true;
        if (externalRoomDeleted) return false;
        return workspace.HasLocalWork(nativeOwner.Invoke<RoomNativeField[]>("snapshotRoomNative").ToImmutableArray());
    }

    private bool WorkspaceBlocksDeparture()
    {
        if (!Current) return false;
        if (!workspace.IsAdmitted) return false;
        if (roomResponseObservationFailed || workspace.AcceptanceFailed) return true;
        try { if (KeyboardBlocksDeparture()) return true; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return true; }
        if (externalRoomDeleted) return false;
        if (workspace.Queue.Confirmed.Document is null && !workspace.Queue.HasPendingIntent && !workspace.Queue.RequestOutstanding
            && !workspace.Queue.RecoveryRequired && !workspace.Queue.TrackingUnavailable) return false;
        if (historyOpening is not null || localModal is not null || guide is not null || guideLoading || imageDraft is not null || imageBusy || roomLinksOpen || roomLinksOpening || scenePlacement is not null || editingComments || draining || controlPending || !browserReady || nativeOwner is null) return true;
        try
        {
            ObserveShellOperation();
            using var work = observedRoomWork.Measure(ObservedWork.NativeReconcile);
            return workspace.HasLocalWork(nativeOwner.Invoke<RoomNativeField[]>("snapshotRoomNative").ToImmutableArray());
        }
        catch (Exception exception)
        {
            // A broken native owner cannot certify a clean departure. Boundary and
            // explicit refresh paths retain their normal general-error reporting.
            Console.Error.WriteLine(exception);
            return true;
        }
    }
    private bool CanPullRoom => Current && workspace.IsAdmitted && browserReady && nativeOwner is not null && Shell.CanUseRoom && !Shell.RoomSwitchPending && !Catalogue.MutationActive && Catalogue.CanAdoptLocal
        && !draining && !controlPending && localModal is null && !editingComments && !workspace.Queue.TrackingUnavailable
        && workspace.Queue.Confirmed.Document is not null && !WorkspaceBlocksDeparture();

    private async Task ReconcileFreshnessAsync()
    {
        if (!Current || !workspace.IsAdmitted || externalRoomDeleted) return;
        var confirmed = workspace.Queue.Confirmed;
        var known = Catalogue.Rooms.GetValueOrDefault(Entry.RoomId);
        if (confirmed.Document is { } old && (confirmed.ExternallyStale || known is null
            || known.Observation.CacheFlagUtc != old.Observation.CacheFlagUtc || known.Header.Observation.IsArchived != old.Header.Observation.IsArchived
            || known.Header.Metadata != old.Header.Metadata)) deferredRoomPull = true;
        if (!deferredRoomPull || roomPullInFlight || roomPullFailed || entryCheckFailed || !CanPullRoom) return;

        var generation = ++roomReadGeneration;
        bool Owns() => Current && generation == roomReadGeneration && ReferenceEquals(workspace.Queue.Confirmed, confirmed);
        roomPullInFlight = true;
        try
        {
            // Only comprehensive CAT coverage or owned targeted Missing can remove
            // this visit's presentation. Never infer absence from flags or failure.
            if (known is null || ReferenceEquals(targetedMissingFrom, confirmed))
            {
                if (Owns() && CanPullRoom && (ReferenceEquals(targetedMissingFrom, confirmed) || !Catalogue.Rooms.ContainsKey(Entry.RoomId))
                    && workspace.ReplaceExternal(null, confirmed, nativeOwner!))
                { externalRoomDeleted = true; deferredRoomPull = false; sceneSelection = null; scenePlacement = null; StateHasChanged(); }
                return;
            }
            var read = await Catalogue.ReadRoomDocumentAsync(Opened.Identity, Owns, () =>
            { if (Owns()) { roomPullActive = true; StateHasChanged(); } }, lifetime.Token);
            // Mutation retirement, a boundary, modal or typing during the read is
            // not permission to replace drafts. Keep one deferred pull, no loop.
            if (read is null || !Owns() || !CanPullRoom) return;
            var document = read.Document;
            if (workspace.ReplaceExternal(document, confirmed, nativeOwner!))
            {
                if (document is null)
                {
                    externalRoomDeleted = true; sceneSelection = null; scenePlacement = null;
                }
                else if (MapContextChanged(confirmed.Document?.Header, document.Header)) mapReloadNeeded = true;
                targetedMissingFrom = null;
                deferredRoomPull = false;
                ReconcileSceneSelectionAfterPull();
                StateHasChanged();
                if (mapReloadNeeded && !Archived) await RetryRoomMapAsync();
            }
        }
        catch (ConfirmedCatalogue.RoomTrackingUnavailableException)
        { if (Current) { workspace.Queue.LoseTracking(); NotifyRoomActions(); StateHasChanged(); } }
        catch (OperationCanceledException) when (!Current) { }
        catch (Exception exception)
        {
            if (Current)
            {
                roomPullFailed = true;
                await Shell.ReportRoomErrorAsync(exception, "The current room could not be refreshed. Existing values and local work have been retained. Retry reads the room; it does not repeat a saved operation.");
                StateHasChanged();
            }
        }
        finally
        {
            var wasActive = roomPullActive;
            roomPullInFlight = roomPullActive = false;
            // An admission race returning null before dispatch has no activity to
            // clear. Retain demand quietly until a real settlement/owner signal.
            if (Current && wasActive) StateHasChanged();
        }
    }
    private Task RetryRoomPullAsync()
    { roomPullFailed = false; deferredRoomPull = true; return ReconcileFreshnessAsync(); }
    private void ReconcileSceneSelectionAfterPull()
    {
        if (sceneSelection is { } selected)
        {
            var row = TrySceneRow(selected.Kind == Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts.V2SceneSelectedItemKind.Transition ? "exit" : selected.Kind.ToString().ToLowerInvariant(), selected.EntityId);
            if (row is null || row.IsArchived || row.Attention) sceneSelection = null;
        }
        // Placement is a retained explicit intent, not an automatic re-arm/focus
        // request. Missing or archived targets cannot keep an armed interaction.
        if (scenePlacement is { } placement)
        {
            EnsurePresentation(); // A mapper fault is not evidence that the receiver disappeared.
            try { if (SceneRow(placement.Kind, placement.EntityId).IsArchived) scenePlacement = null; }
            catch (InvalidOperationException) { scenePlacement = null; }
        }
    }
}
