using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    [Parameter, EditorRequired] public long EntryStarted { get; set; }
    private double? initialRoomReadyMilliseconds;
    private bool initialContextSettled, initialSceneRequired, initialEnabledRenderCompleted, initialReadinessFailed;
    private long? initialSceneGeneration;
    private bool EntryControlsReady => workspace.IsAdmitted && browserReady && (initialEnabledRenderCompleted || initialContextSettled
        && (!initialSceneRequired || mapHeightOwner is not null
            && scenePane?.IsReadyOwner(Entry.RoomId, sceneOwnerGeneration) == true
            && initialSceneGeneration == sceneOwnerGeneration));

    private void SettleInitialReadinessContext()
    {
        if (initialContextSettled) return;
        initialContextSettled = true;
        initialSceneRequired = !Archived && !externalRoomDeleted && showMapContext;
        if (initialSceneRequired && sceneOwnerGeneration > 0) initialSceneGeneration = sceneOwnerGeneration;
    }

    private void ObserveInitialSceneOwner(long generation)
    {
        if (initialContextSettled && initialSceneRequired && initialSceneGeneration is null)
            initialSceneGeneration = generation;
    }

    private bool TryCompleteInitialReadiness()
    {
        if (!Current || !workspace.IsAdmitted
            || lifetime.IsCancellationRequested || externalRoomDeleted || workspace.Confirmed.Document is null
            || Shell.GeneralErrorOpen || !EntryControlsReady || !initialEnabledRenderCompleted)
            return false;
        if (initialSceneRequired && (mapHeightOwner is null || !SceneContextReady
            || initialSceneGeneration != sceneOwnerGeneration)) return false;
        var lifecycleCompleted = CompleteSwitchSetup();
        if (initialRoomReadyMilliseconds is not null || initialReadinessFailed) return lifecycleCompleted;
        initialRoomReadyMilliseconds = Stopwatch.GetElapsedTime(EntryStarted).TotalMilliseconds;
        readinessTiming?.End();
        return true;
    }

    private static string ProgressInteger(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Milliseconds(double value) => value.ToString("0", CultureInfo.InvariantCulture);
    private string VisibleElapsedText => (workspace.AdmittedQueue?.CompletedSaveElapsedMilliseconds ?? initialRoomReadyMilliseconds) is { } elapsed
        ? Milliseconds(elapsed) : "";
    private string SaveTimingLabel => $"{SaveLabel}. Updates Processed: {ProgressInteger(workspace.AdmittedQueue?.TerminalThrough ?? 0)} contiguous server-confirmed terminal operations of {ProgressInteger(workspace.AdmittedQueue?.QueuedThrough ?? 0)} queued, including unsent operations; includes successful no-op and explicit no-write completion. "
        + (workspace.AdmittedQueue?.CompletedSaveElapsedMilliseconds is { } save
            ? $"Displayed duration: last completed client save request, {Milliseconds(save)} ms, from send through response decoding, excluding queue/admission wait, installation, rendering and refresh. "
            : initialRoomReadyMilliseconds is not null ? "Displayed duration: initial room-entry readiness. " : "Room-entry readiness has not completed. ")
        + (initialRoomReadyMilliseconds is { } ready
            ? $"Initial room-entry readiness: {Milliseconds(ready)} ms, from admitted room loading through the enabled editor render and required native/parser, map-height and initially displayed scene viewport initialization; excludes preceding application bootstrap and does not measure physical paint or image downloads."
            : "No completed initial room-entry readiness sample.")
        + (workspace.AdmittedQueue?.CompletedRequestMilliseconds is { } server ? $" Server-only request duration: {Milliseconds(server)} ms." : "");
}
