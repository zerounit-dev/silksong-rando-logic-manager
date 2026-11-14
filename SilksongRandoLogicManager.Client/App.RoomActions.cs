using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    public readonly record struct RoomActionState(bool Unfinished, bool Failed, bool Active);
    private Func<RoomActionState>? roomActionState;
    private enum DepartureMode { Framework, Document, Recovery, Logout, CacheReload }
    private sealed record DepartureIdentity(Guid Id, object Visit, string Target, string? HistoryState, DepartureMode Mode, Func<bool>? TargetValid);
    private sealed record DirectRoomDestination(object Visit, string Target, Func<bool> Valid);
    private DirectRoomDestination? directRoomDestination;
    public void NavigateRoomAction(object visit, string target, Func<bool> valid)
    {
        if (!CanInteract || !ReferenceEquals(visit, roomWorkspaceOwner) || !valid()) return;
        var intent = new DirectRoomDestination(visit, Navigation.ToAbsoluteUri(target).AbsoluteUri, valid);
        directRoomDestination = intent;
        try { Navigation.NavigateTo(intent.Target); }
        catch { if (ReferenceEquals(directRoomDestination, intent)) directRoomDestination = null; throw; }
    }
    private sealed class Departure(DepartureIdentity identity, CancellationToken cancellation)
    {
        public DepartureIdentity Identity { get; } = identity;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource<bool> Signal { get; set; } = NewSignal();
        public bool Confirmed { get; set; }
        public bool Retired { get; set; }
        public static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private Departure? roomDeparture, replacementDeparture;
    private ElementReference departureDialog;
    private bool departureDialogPending;
    private string? departureFeedback;
    private Guid? recoveryWarningSuppression;
    private RoomActionState lastRoomActionState;
    // Retires only post-create navigation, including non-Room away/back and
    // workspace replacement without a different final URL.
    private object creationNavigationOwner = new();
    public void PublishRoomQueueWarning(object visit)
    { if (ReferenceEquals(visit, roomWorkspaceOwner)) PublishRoomWarning(); }

    private void PublishRoomWarning()
    {
        if (module is IJSInProcessObjectReference native)
            native.InvokeVoid("setRoomQueueWarning", !disposed && !Runtime.Discarding && roomWorkspaceOwner is not null
                && recoveryWarningSuppression is null && roomActionState?.Invoke().Unfinished == true);
    }
    public void ObserveRoomActions(object visit)
    {
        if (!ReferenceEquals(visit, roomWorkspaceOwner)) return;
        PublishRoomWarning();
        var state = roomActionState?.Invoke() ?? default;
        var changed = state != lastRoomActionState;
        lastRoomActionState = state;
        if (!state.Active && roomDeparture is not null) { CancelRoomDeparture(); return; }
        if (state.Failed && roomDeparture is { Identity.Mode: not DepartureMode.Recovery })
        {
            CancelRoomDeparture("The requested action was canceled. Recover room changes, then invoke the action again.");
            return;
        }
        roomDeparture?.Signal.TrySetResult(true);
        if (changed && roomDeparture is not null && !disposed) StateHasChanged();
    }
    private void RetireDeparture(Departure request)
    {
        request.Retired = true; request.Signal.TrySetResult(false);
        if (ReferenceEquals(roomDeparture, request)) roomDeparture = null;
        if (ReferenceEquals(replacementDeparture, request)) replacementDeparture = null;
    }
    private void RetireDepartureAndPromote(Departure request)
    {
        var successor = ReferenceEquals(roomDeparture, request) ? replacementDeparture : null;
        RetireDeparture(request);
        if (successor is not null)
        { replacementDeparture = null; roomDeparture = successor; departureDialogPending = true; }
        if (roomDeparture is null) CloseDepartureDialog();
    }
    public void FailRoomActionObservation(object visit)
    {
        if (!ReferenceEquals(roomWorkspaceOwner, visit)) return;
        // Managed retirement must still succeed if the browser warning/dialog
        // interop itself failed. The existing general-error host reports that fault.
        if (roomDeparture is { } request) RetireDeparture(request);
        if (replacementDeparture is { } replacement) RetireDeparture(replacement);
        departureDialogPending = false;
        departureFeedback = "The requested action was canceled because room completion could not be observed.";
        if (!disposed) StateHasChanged();
    }
    private void CancelRoomDeparture(string? feedback = null)
    {
        if (roomDeparture is { } request) RetireDeparture(request);
        if (replacementDeparture is { } replacement) RetireDeparture(replacement);
        departureFeedback = feedback;
        CloseDepartureDialog();
        if (!disposed) StateHasChanged();
    }
    private void CloseDepartureDialog()
    {
        departureDialogPending = false;
        if (module is IJSInProcessObjectReference native) native.InvokeVoid("closeRoomDepartureDialog", departureDialog);
    }
    private bool DepartureOwns(Departure request) => CanInteract && !request.Retired
        && ReferenceEquals(roomWorkspaceOwner, request.Identity.Visit) && !request.Cancellation.IsCancellationRequested;
    private bool ValidDepartureTarget(Departure request)
    {
        if (!DepartureOwns(request) || roomActionState?.Invoke().Active != true || errorOpen || roomSwitch is not null || roomLoadOwner is not null
            || request.Identity.TargetValid?.Invoke() == false) return false;
        var uri = Navigation.ToAbsoluteUri(request.Identity.Target);
        if (uri.Scheme is not ("http" or "https")) return false;
        if (Locked) return false;
        if (request.Identity.Mode == DepartureMode.Recovery) return roomActionState?.Invoke() is { Failed: true } or { Unfinished: false };
        if (uri.AbsoluteUri.StartsWith(Navigation.BaseUri, StringComparison.Ordinal)
            && RoomRoute(Navigation.ToBaseRelativePath(uri.AbsoluteUri)) is { } room && !Catalogue.Rooms.ContainsKey(room)) return false;
        return true;
    }
    private async Task<bool> AdmitRoomDepartureAsync(string target, string? history, DepartureMode mode, CancellationToken cancellation, Func<bool>? valid = null, Guid? dispatch = null)
    {
        if (!CanInteract) return false;
        if (roomWorkspaceOwner is not { } visit) return true;
        if (valid?.Invoke() == false) return false;
        if (mode == DepartureMode.Framework && Navigation.ToAbsoluteUri(target).AbsoluteUri.StartsWith(Navigation.BaseUri, StringComparison.Ordinal)
            && RoomRoute(Navigation.ToBaseRelativePath(Navigation.ToAbsoluteUri(target).AbsoluteUri)) is { } sameRoom
            && sameRoom == RoomRoute(Navigation.ToBaseRelativePath(Navigation.Uri))) return !Locked && !errorOpen;
        var state = roomActionState?.Invoke() ?? default;
        if (mode != DepartureMode.Recovery && state.Failed)
        {
            departureFeedback = "This action cannot complete until room changes are recovered. Invoke it again after recovery.";
            StateHasChanged(); return false;
        }
        var identity = new DepartureIdentity(dispatch ?? Guid.NewGuid(), visit, Navigation.ToAbsoluteUri(target).AbsoluteUri, history, mode, valid);
        var request = new Departure(identity, cancellation);
        if (roomDeparture is { } obsolete && !DepartureOwns(obsolete))
        {
            RetireDeparture(obsolete);
            if (replacementDeparture is { } staleChoice) RetireDeparture(staleChoice);
        }
        if (!state.Unfinished && roomDeparture is null)
        {
            if (!ValidDepartureTarget(request)) return false;
            return await DispatchDocumentAsync(request);
        }
        if (roomDeparture is { } existing)
        {
            if (mode != DepartureMode.Framework && existing.Identity.Target == identity.Target && existing.Identity.Mode == mode)
            { departureDialogPending = !existing.Confirmed; StateHasChanged(); return false; }
            if (replacementDeparture is { } oldReplacement) RetireDeparture(oldReplacement);
            replacementDeparture = request;
        }
        else
        {
            roomDeparture = request;
            if (!state.Unfinished)
            { request.Confirmed = true; request.Signal.TrySetResult(true); }
        }
        departureFeedback = null; departureDialogPending = !request.Confirmed; StateHasChanged();
        try
        {
        using var registration = cancellation.Register(() =>
        {
            request.Signal.TrySetResult(false);
            _ = InvokeAsync(() =>
            {
                if (ReferenceEquals(roomDeparture, request) || ReferenceEquals(replacementDeparture, request))
                {
                    RetireDepartureAndPromote(request);
                    departureFeedback = "Navigation was canceled or superseded."; StateHasChanged();
                }
            });
        });
        // State-transition notification, not polling or a second queue drain. A
        // new accepted suffix or replacement choice can revoke a completed signal.
        while (await request.Signal.Task)
        {
            request.Signal = Departure.NewSignal();
            if (!DepartureOwns(request)) break;
            if (!ReferenceEquals(roomDeparture, request) || replacementDeparture is not null || !request.Confirmed) continue;
            state = roomActionState?.Invoke() ?? default;
            if (mode != DepartureMode.Recovery && state.Failed) break;
            if (mode != DepartureMode.Recovery && state.Unfinished) continue;
            if (!ValidDepartureTarget(request)) break;
            // No await from fresh certification/consumption to dispatch/return.
            RetireDeparture(request); CloseDepartureDialog(); StateHasChanged();
            if (mode == DepartureMode.Recovery) { recoveryWarningSuppression = identity.Id; PublishRoomWarning(); }
            return await DispatchDocumentAsync(request);
        }
        if (ReferenceEquals(roomDeparture, request) || ReferenceEquals(replacementDeparture, request))
        { RetireDepartureAndPromote(request); departureFeedback = "The requested action was canceled; its target or room state changed."; StateHasChanged(); }
        return false;
        }
        finally
        {
            if (!request.Retired && (ReferenceEquals(roomDeparture, request) || ReferenceEquals(replacementDeparture, request)))
                RetireDepartureAndPromote(request);
        }
    }
    private void ConfirmRoomDeparture(Guid id)
    {
        if (roomDeparture is not { } request || request.Identity.Id != id || replacementDeparture is not null || !DepartureOwns(request)) return;
        request.Confirmed = true; CloseDepartureDialog(); request.Signal.TrySetResult(true); StateHasChanged();
    }
    private void ChooseDepartureReplacement(Guid original, Guid incoming, bool replace)
    {
        if (roomDeparture is not { } current || replacementDeparture is not { } next
            || current.Identity.Id != original || next.Identity.Id != incoming) return;
        if (replace)
        {
            RetireDeparture(current); replacementDeparture = null; roomDeparture = next;
            next.Confirmed = roomActionState?.Invoke().Unfinished != true;
            departureDialogPending = !next.Confirmed;
            if (next.Confirmed) { CloseDepartureDialog(); next.Signal.TrySetResult(true); }
        }
        else
        {
            RetireDeparture(next);
            if (!DepartureOwns(current)) RetireDepartureAndPromote(current);
            else
            {
                if (current.Confirmed) CloseDepartureDialog();
                else departureDialogPending = true;
                current.Signal.TrySetResult(true);
            }
        }
        StateHasChanged();
    }
    private void CancelRoomDeparture(Guid id)
    {
        if (roomDeparture?.Identity.Id != id && replacementDeparture?.Identity.Id != id) return;
        CancelRoomDeparture();
    }
    private async Task AdmitFrameworkRoomDepartureAsync(LocationChangingContext context)
    {
        var invocation = Guid.NewGuid();
        var visit = roomWorkspaceOwner;
        try
        {
            var direct = directRoomDestination; directRoomDestination = null;
            var target = Navigation.ToAbsoluteUri(context.TargetLocation).AbsoluteUri;
            var valid = direct is not null && ReferenceEquals(direct.Visit, roomWorkspaceOwner) && direct.Target == target ? direct.Valid : null;
            if (!await AdmitRoomDepartureAsync(context.TargetLocation, context.HistoryEntryState, DepartureMode.Framework, context.CancellationToken, valid, invocation))
                context.PreventNavigation();
        }
        catch (Exception exception)
        {
            context.PreventNavigation();
            if (ReferenceEquals(roomWorkspaceOwner, visit) && (roomDeparture is null || roomDeparture.Identity.Id == invocation))
            { departureFeedback = "Navigation could not be admitted. Room work has been retained."; StateHasChanged(); }
            Console.Error.WriteLine(exception);
        }
    }
    public bool HasRequestedRoomAction(object visit) => ReferenceEquals(roomWorkspaceOwner, visit) && roomDeparture is not null;
    public Task RefreshRoomDocumentAsync(object visit)
        => ReferenceEquals(visit, roomWorkspaceOwner) ? DepartDocumentAsync(Navigation.Uri, true) : Task.CompletedTask;
    private async Task DepartDocumentAsync(string target, bool recovery)
    {
        if (!CanInteract) return;
        var visit = roomWorkspaceOwner;
        var dispatch = Guid.NewGuid();
        if (recovery) CancelRoomDeparture();
        try
        {
            if (visit is null)
            {
                if (CanInteract && !Locked && !errorOpen)
                    ((IJSInProcessObjectReference)module!).InvokeVoid("dispatchRoomDocument", Navigation.ToAbsoluteUri(target).AbsoluteUri, !recovery);
                return;
            }
            await AdmitRoomDepartureAsync(target, null, recovery ? DepartureMode.Recovery : DepartureMode.Document, lifetime.Token, dispatch: dispatch);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(visit, roomWorkspaceOwner) && recoveryWarningSuppression == dispatch)
            { recoveryWarningSuppression = null; PublishRoomWarning(); }
            if (ReferenceEquals(visit, roomWorkspaceOwner) && roomDeparture is null)
                await ReportRoomErrorAsync(exception, "Refresh or navigation could not be dispatched. Local work is retained; no write was repeated.");
            else Console.Error.WriteLine(exception);
        }
    }
    private Task<bool> DispatchDocumentAsync(Departure request)
    {
        if (!CanInteract) return Task.FromResult(false);
        if (request.Identity.Mode == DepartureMode.Framework) return Task.FromResult(true);
        if (request.Identity.Mode == DepartureMode.CacheReload)
            return ExecuteCacheReplacementAsync();
        if (request.Identity.Mode == DepartureMode.Logout)
            return ExecuteLogoutAsync();
        ((IJSInProcessObjectReference)module!).InvokeVoid("dispatchRoomDocument", request.Identity.Target, request.Identity.Mode == DepartureMode.Document);
        return Task.FromResult(true);
    }
}
