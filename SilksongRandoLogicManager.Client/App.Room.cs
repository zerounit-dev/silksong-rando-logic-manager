using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SilksongRandoLogicManager.Contracts;
using SilksongRandoLogicManager.Client.RoomEditing;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    internal static Guid? RoomRoute(string path)
    {
        var parts = path.Split('?', '#')[0].Trim('/').Split('/');
        return parts is ["rooms", var id] && Guid.TryParse(id, out var room) ? room : null;
    }
    public bool CanUseRoom => CanInteract && !errorOpen && !SidebarLocked && requirementPageLock is null;
    private object? roomWorkspaceOwner;
    private object? roomLoadOwner;
    private RoomVisitLoad? initialRoom;
    public sealed record RoomVisitLoad(Guid RoomId, RoomVisitEntry? Entry, RoomMapContextView? Map, long EntryStarted);
    private Func<Task>? roomReconcileFreshness;
    private Func<bool>? roomBlocksUnattendedReplacement;
    private bool RoomDepartureBlocked => roomSwitch is not null || roomLoadOwner is not null;
    public RoomVisitLoad? TakeInitialRoom(Guid room)
    {
        var value = initialRoom;
        initialRoom = null;
        return value?.RoomId == room ? value : null;
    }
    private async Task LoadInitialRoomAsync()
    {
        if (RoomRoute(Navigation.ToBaseRelativePath(Navigation.Uri)) is not { } room) return;
        initialRoom = await LoadRoomVisitAsync(this, room, () => !disposed && RoomRoute(Navigation.ToBaseRelativePath(Navigation.Uri)) == room, lifetime.Token,
            ReportInitialProgressAsync, PreparationContext.Startup);
    }
    public Task<RoomVisitLoad?> LoadRoomVisitAsync(object owner, Guid room, Func<bool> owns, CancellationToken token,
        Func<InitialCatalogueProgress, Task>? initialProgress = null)
        => LoadRoomVisitAsync(owner, room, owns, token, initialProgress, PreparationContext.Ordinary);

    private async Task<RoomVisitLoad?> LoadRoomVisitAsync(object owner, Guid room, Func<bool> owns, CancellationToken token,
        Func<InitialCatalogueProgress, Task>? initialProgress, PreparationContext context)
    {
        if (!CanInteract || errorOpen || roomLoadOwner is not null || !owns()) return null;
        roomLoadOwner = owner;
        var entryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        roomEntryObservation?.End(TimingOutcome.Retired);
        roomEntryObservationStart = entryStarted;
        roomEntryObservation = OperationTiming.Observe("room.entry.observed-native-ready", started: entryStarted);
        var observation = roomEntryObservation;
        void EndObservation(TimingOutcome outcome)
        {
            observation.End(outcome);
            if (ReferenceEquals(roomEntryObservation, observation)) roomEntryObservation = null;
        }
        StateHasChanged();
        using var timing = OperationTiming.Start("room.entry.reads", parent: roomEntryObservation.Id);
        try
        {
            var result = await ReadRoomVisitAsync(room, entryStarted, () => CanInteract && ReferenceEquals(roomLoadOwner, owner) && owns(), token, initialProgress, context);
            timing.End(result is null ? TimingOutcome.Retired : result.Entry is null ? TimingOutcome.Missing : TimingOutcome.Complete);
            if (result?.Entry is null) EndObservation(result is null ? TimingOutcome.Retired : TimingOutcome.Missing);
            return result;
        }
        catch { EndObservation(TimingOutcome.FailedOrRetired); throw; }
        finally
        {
            if (ReferenceEquals(roomLoadOwner, owner)) roomLoadOwner = null;
            if (!disposed) { StateHasChanged(); await DrainRefreshAsync(); }
        }
    }
    private async Task<RoomVisitLoad?> ReadRoomVisitAsync(Guid room, long entryStarted, Func<bool> owns, CancellationToken token,
        Func<InitialCatalogueProgress, Task>? initialProgress, PreparationContext context)
    {
        using var timing = OperationTiming.Start("room.entry.document-map");
        if (initialProgress is not null) await initialProgress(new(InitialLoadingPhase.Room));
        var opened = await Catalogue.OpenRoomAsync(room, Guid.NewGuid(), owns, token, context);
        if (!owns()) return null;
        if (opened is null)
        {
            if (Catalogue.Rooms.ContainsKey(room)) throw new InvalidOperationException("The selected room could not be loaded.");
            timing.End(TimingOutcome.Missing);
            return new(room, null, null, entryStarted);
        }
        // Logic and scene use the same local confirmed baseline. Context is a second,
        // explicitly sampled read; neither observation certifies the other.
        RoomDocumentReconciliation.Validate(opened.Document);
        using var mapTiming = OperationTiming.Start("room.map.combined-read-decode-preparation");
        if (initialProgress is not null) await initialProgress(new(InitialLoadingPhase.Map));
        var map = await Catalogue.LoadRoomMapAsync(room, owns, token, context);
        if (!owns()) return null;
        if (map is null) throw new InvalidOperationException("The selected room context could not be loaded.");
        if (map.RoomId != room || (map.State == RoomMapContextState.Ready) != (map.Surface is not null)
            || map.Surface is { } surface && (surface.Context.CurrentRoomId != room || !surface.Context.IsRoomContext))
            throw new System.Text.Json.JsonException("Wrong initial room map context.");
        mapTiming.End();
        timing.End();
        return new(room, opened, map, entryStarted);
    }
    public void RegisterRoomWorkspace(object owner, Func<bool> blocksReplacement, Func<Task> reconcileFreshness, Func<RoomActionState> actions)
    {
        if (!CanInteract) return;
        if (!ReferenceEquals(roomWorkspaceOwner, owner))
        {
            creationNavigationOwner = new();
            CancelRoomDeparture();
        }
        roomWorkspaceOwner = owner; roomReconcileFreshness = reconcileFreshness;
        roomBlocksUnattendedReplacement = blocksReplacement;
        roomActionState = actions; recoveryWarningSuppression = null; PublishRoomWarning();
    }
    public void ReleaseRoomWorkspace(object owner)
    {
        ReleaseClientUpdateRisk(owner);
        if (!ReferenceEquals(roomWorkspaceOwner, owner)) return;
        creationNavigationOwner = new();
        CancelRoomDeparture();
        roomWorkspaceOwner = null; roomReconcileFreshness = null;
        roomBlocksUnattendedReplacement = null;
        roomActionState = null; recoveryWarningSuppression = null; PublishRoomWarning();
        directRoomDestination = null;
        if (!disposed) StateHasChanged();
    }
    private Task ReconcileRoomFreshnessAsync() => roomReconcileFreshness?.Invoke() ?? Task.CompletedTask;
    public async Task DrainRoomRefreshAsync()
    {
        await DrainRefreshAsync();
    }
    public async Task ReportRoomErrorAsync(Exception exception, string message)
    {
        Console.Error.WriteLine(exception);
        await FailAsync(new ReadFailure(message, null, exception), false);
        if (!disposed) StateHasChanged();
    }
    public bool FocusRoomDialog(string id) => CanInteract && !errorOpen
        && ((IJSInProcessRuntime)JS).Invoke<bool>("focusV2ModalDialog", id, errorDialog);
}
