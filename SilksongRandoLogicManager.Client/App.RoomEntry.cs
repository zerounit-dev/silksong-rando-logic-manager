using Microsoft.AspNetCore.Components.Routing;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    // Invocation identity is separate from the read lease and the unlocked candidate.
    // NavigationManager cancels the context even after SUCCESSFUL admission.
    private sealed class RoomEntryNavigation(string source, string target, string? history)
    {
        public string Source { get; } = source;
        public string Target { get; } = target;
        public string? History { get; } = history;
        public CancellationTokenSource? ReadCancellation { get; set; }
        public RoomVisitLoad? Prepared { get; set; }
    }
    private RoomEntryNavigation? roomEntryNavigation, executingRoomEntry, arrivingRoomNavigation;
    private Task roomEntryPreparation = Task.CompletedTask;
    private bool RoomEntryPreparing => executingRoomEntry is not null;

    private RoomEntryNavigation BeginRoomEntryNavigation(LocationChangingContext context)
    {
        RetireRoomEntryNavigation();
        return roomEntryNavigation = new(Navigation.Uri,
            Navigation.ToAbsoluteUri(context.TargetLocation).AbsoluteUri, context.HistoryEntryState);
    }

    private void RetireRoomEntryNavigation()
    {
        var previous = roomEntryNavigation;
        roomEntryNavigation = null;
        arrivingRoomNavigation = null;
        previous?.ReadCancellation?.Cancel();
        if (previous?.Prepared is { } visit && roomEntryObservationStart == visit.EntryStarted)
        {
            roomEntryObservation?.End(TimingOutcome.Retired);
            roomEntryObservation = null;
        }
    }

    private bool OwnsRoomEntryNavigation(RoomEntryNavigation attempt, LocationChangingContext context)
        => !disposed && CanInteract && !errorOpen && ReferenceEquals(roomEntryNavigation, attempt)
            && Navigation.Uri == attempt.Source && !context.CancellationToken.IsCancellationRequested;

    private bool RoomEntrySourceAvailable(bool sceneCompletion)
        => !RoomDepartureBlocked && requirementPageLock is null && landingOwner is null
            && (!Locked || sceneCompletion && !busy && editor is null && editorLoading is null
                && !cacheOpen && !cacheReplacing
                && (sceneOperationOwner is null || ReferenceEquals(sceneOperationOwner, sceneCompletionOwner)));

    private async Task PrepareRoomEntryAsync(RoomEntryNavigation attempt, LocationChangingContext context, Guid room, bool sceneCompletion)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.Token);
        attempt.ReadCancellation = cancellation;
        executingRoomEntry = attempt;
        try
        {
            var visit = await LoadRoomVisitAsync(attempt, room, () => OwnsRoomEntryNavigation(attempt, context), cancellation.Token);
            if (!OwnsRoomEntryNavigation(attempt, context) || !RoomEntrySourceAvailable(sceneCompletion) || visit is null)
            {
                context.PreventNavigation();
                if (visit is not null && roomEntryObservationStart == visit.EntryStarted)
                { roomEntryObservation?.End(TimingOutcome.Retired); roomEntryObservation = null; }
                return;
            }
            // No await between certification and publishing this invocation's candidate.
            // It holds no read/action lease while other admission handlers may still veto.
            attempt.Prepared = visit;
        }
        catch (OperationCanceledException) when (!OwnsRoomEntryNavigation(attempt, context))
        { context.PreventNavigation(); }
        catch (Exception exception)
        {
            context.PreventNavigation();
            if (OwnsRoomEntryNavigation(attempt, context))
                await ReportRoomErrorAsync(exception, "The room and its context could not be loaded. No editing workspace has been opened.");
        }
        finally
        {
            if (ReferenceEquals(executingRoomEntry, attempt)) executingRoomEntry = null;
            attempt.ReadCancellation = null;
            if (!disposed) StateHasChanged();
        }
    }

    // Runs synchronously in the shell's earlier LocationChanged subscription, before
    // Router can instantiate RoomEditor. Never promote based on RoomId alone.
    private bool ArriveRoomEntry(LocationChangedEventArgs args)
    {
        var attempt = roomEntryNavigation;
        if (!disposed && CanInteract && !errorOpen && attempt?.Prepared is { } visit
            && args.Location == attempt.Target && args.HistoryEntryState == attempt.History
            && RoomRoute(Navigation.ToBaseRelativePath(args.Location)) == visit.RoomId)
        {
            arrivingRoomNavigation = attempt;
            return true;
        }
        RetireRoomEntryNavigation();
        return false;
    }

    public RoomVisitLoad? TakeArrivingRoom(Guid room)
    {
        var arrival = arrivingRoomNavigation;
        if (arrival?.Prepared is not { } visit || !ReferenceEquals(roomEntryNavigation, arrival)
            || visit.RoomId != room || Navigation.Uri != arrival.Target) return null;
        arrivingRoomNavigation = roomEntryNavigation = null;
        arrival.Prepared = null;
        return visit;
    }
}
