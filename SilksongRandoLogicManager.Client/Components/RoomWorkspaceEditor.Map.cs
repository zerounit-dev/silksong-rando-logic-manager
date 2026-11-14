using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    [Parameter, EditorRequired] public RoomMapContextView MapContext { get; set; } = null!;
    private RoomMapContextView roomMap = null!;
    private MapLinksDialog? mapLinks;
    private bool roomLinksOpen, roomLinksOpening, mapReloadNeeded;
    private bool mapReloadFailed;
    private int mapReloadsInFlight;
    private long mapGeneration;
    private string? observedMapState;
    private void LocalRoomMapChanged()
    {
        if (!Current || Catalogue.ConfirmedMapStateId is not { } state || state == observedMapState) return;
        _ = AdoptLocalRoomMapAsync();
    }
    private async Task AdoptLocalRoomMapAsync()
    {
        try { if (Current && !Archived) await ReloadRoomMapAsync(); }
        catch (OperationCanceledException) when (!Current) { }
        catch (Exception error)
        {
            if (Current)
            {
                mapReloadNeeded = mapReloadFailed = true;
                StateHasChanged();
                await Shell.ReportRoomErrorAsync(error, "The room map could not be refreshed. The preceding sampled map has been retained.");
            }
        }
    }
    private IJSObjectReference? mapHeightOwner;
    private Task? mapHeightFlight;
    // Visit-local associations only. Common storage owns the opaque witnesses and
    // all freshness decisions; these references follow existing operation/ack life.
    private sealed class RoomResultSource
    {
        public ConfirmedCatalogue.MutationReadContext? Execution;
        public ConfirmedCatalogue.MutationReadContext? ProjectionRead;
        public bool Usable;
        public ConfirmedCatalogue.MutationReadContext? Result;
    }
    private readonly Dictionary<RoomOperationIdentity, RoomResultSource> roomResultSources = [];
    private readonly Dictionary<Guid, (RoomOperationIdentity Operation, ConfirmedCatalogue.MutationReadContext Source)> roomAttemptSources = [];
    private RoomResultSource ResultSource(RoomOperationIdentity operation)
    {
        if (!roomResultSources.TryGetValue(operation, out var source))
            roomResultSources.Add(operation, source = new());
        return source;
    }
    private void RetainBatchSource(ConfirmedCatalogue.MutationReadContext source)
    {
        // Includes registered-but-unexecuted work resumed by this NEW batch, not
        // merely its new Items. This is its actual execution admission, not the
        // earlier registration that stopped at a blocked head.
        foreach (var operation in workspace.Queue.PendingIntent.Where(x => x.Sequence <= workspace.Queue.SubmittedThrough))
        {
            var retained = ResultSource(new(operation.Sequence, operation.OperationId));
            if (retained.Usable) continue;
            retained.Execution = source;
            retained.ProjectionRead = null;
        }
    }
    private void RetainResolutionSource(RoomResolveRequest request, ConfirmedCatalogue.MutationAdmission admission)
    {
        if (!roomAttemptSources.TryGetValue(request.ResolutionAttemptId, out var attempt))
        {
            attempt = (new(request.Sequence, request.OperationId), admission.CaptureReadContext());
            roomAttemptSources.Add(request.ResolutionAttemptId, attempt);
        }
        var retained = ResultSource(attempt.Operation);
        if (!retained.Usable) retained.Execution = attempt.Source;
        // An immutable attempt retransmission reuses its handle, even after a
        // transport failure. A genuinely never-admitted send has no handle yet.
    }
    private void RetainProjectionReadSources(ConfirmedCatalogue.MutationAdmission admission)
    {
        ConfirmedCatalogue.MutationReadContext? source = null;
        foreach (var operation in workspace.Queue.PendingIntent.Where(x => x.Sequence <= workspace.Queue.SubmittedThrough))
        {
            var retained = ResultSource(new(operation.Sequence, operation.OperationId));
            if (retained.Usable || retained.ProjectionRead is not null) continue;
            // Only a candidate for missing-evidence recovery. It is NEVER used
            // for a usable original. Keep the first unconfirmed read on loss:
            // the server may already have retained that read's projection.
            retained.ProjectionRead = source ??= admission.CaptureProjectionReadContext();
        }
    }
    private IReadOnlyList<ConfirmedCatalogue.MutationReadContext?> RoomPublicationSources(RoomTrackingResponse response)
    {
        // Called by the workspace only AFTER queue envelope/receipt/body validation.
        var sources = new List<ConfirmedCatalogue.MutationReadContext?>();
        foreach (var result in response.Results.OrderBy(x => x.Receipt.Sequence))
        {
            var retained = ResultSource(new(result.Receipt.Sequence, result.Receipt.OperationId));
            if (result.ProjectionPending)
            {
                // A decoded pending result proves this read supplied no projection;
                // a subsequent explicit recovery may capture its own later source.
                retained.ProjectionRead = null;
                continue;
            }
            if (!retained.Usable)
            {
                // Existing recovery carries complete documents, not mutation
                // deltas. All-deleted recovery has no document: its incomplete
                // shared evidence distinguishes it from original commit coverage.
                var recovered = !result.RecoveryDocuments.IsEmpty || result.Changes.IsEmpty
                    && !result.DeletedRoomIds.IsEmpty && result.Shared?.Rooms is { Complete: false };
                var execution = retained.Execution;
                if (response.ResolutionAttempt is { TerminalReceipt: { } receipt } attempt
                    && receipt.Sequence == result.Receipt.Sequence && receipt.OperationId == result.Receipt.OperationId
                    && roomAttemptSources.TryGetValue(attempt.AttemptId, out var original))
                    execution = original.Source;
                retained.Result = recovered ? retained.ProjectionRead : execution;
                retained.Usable = true;
                retained.ProjectionRead = null;
            }
            // Same ordered/filter correspondence as RoomWorkspace.Incorporate.
            // Unknown provenance is a null ENTRY, never a newly sampled original.
            if (result.Receipt.Sequence > workspace.Queue.IncorporatedThrough
                && result.Receipt.Sequence <= response.Progress.TerminalThrough && result.Shared is not null)
                sources.Add(retained.Result);
        }
        return sources;
    }
    private void ReleaseAcknowledgedRoomSources()
    {
        var through = workspace.Queue.AcknowledgedThrough;
        foreach (var operation in roomResultSources.Keys.Where(x => x.Sequence <= through).ToArray())
            roomResultSources.Remove(operation);
        foreach (var id in roomAttemptSources.Where(x => x.Value.Operation.Sequence <= through).Select(x => x.Key).ToArray())
            roomAttemptSources.Remove(id);
    }
    private Task ReconcileMapHeightAsync() => mapHeightFlight is { IsCompleted: false } ? mapHeightFlight : mapHeightFlight = ReconcileMapHeightCoreAsync();
    private async Task ReconcileMapHeightCoreAsync()
    {
        if (mapHeightOwner is not null && (Archived || !showMapContext || externalRoomDeleted))
        {
            var previous = mapHeightOwner; mapHeightOwner = null;
            try { await previous.InvokeVoidAsync("dispose"); }
            finally { await previous.DisposeAsync(); }
        }
        if (Current && mapHeightOwner is null && !Archived && showMapContext && !externalRoomDeleted)
        {
            var candidate = await JS.InvokeAsync<IJSObjectReference>("createV2MapContextHeightOwner", root);
            if (!Current || Archived || !showMapContext || externalRoomDeleted)
            {
                try { await candidate.InvokeVoidAsync("dispose"); }
                finally { await candidate.DisposeAsync(); }
            }
            else mapHeightOwner = candidate;
        }
    }
    private bool RoomLinksCanUse => Current && roomLinksOpen && Shell.CanUseRoom && !workspace.Queue.TrackingUnavailable;
    private void DepartLinkedRoom(AreaMapSurface.LinkedMapRoomIntent intent)
    {
        var generation = intent.HostGeneration;
        var target = intent.TargetRoomId;
        if (intent.SourceRoomId != Entry.RoomId) return;
        Shell.NavigateRoomAction(this, $"/rooms/{target:D}", () => Current && mapGeneration == generation
            && Catalogue.Rooms.TryGetValue(target, out var room) && !room.Header.Observation.IsArchived);
    }
    private void InstallRoomMap(RoomMapContextView value)
    {
        if (value.RoomId != Entry.RoomId || (value.State == RoomMapContextState.Ready) != (value.Surface is not null)
            || value.Surface is { } surface && (surface.Context.CurrentRoomId != value.RoomId || !surface.Context.IsRoomContext))
            throw new System.Text.Json.JsonException("Wrong room map context.");
        roomMap = value; mapGeneration++;
        mapReloadFailed = false;
    }
    private async Task OpenRoomLinksAsync(AreaMapSurface.MapLinkContextIntent intent)
    {
        if (!Current || AuthoringBlocked || mapLinks is null) return;
        roomLinksOpening = roomLinksOpen = true;
        try
        {
            await Shell.RememberLandingInitiatorAsync();
            if (Current && Shell.CanInteract && !await mapLinks.OpenAsync(intent)) roomLinksOpen = false;
        }
        catch (Exception exception) { roomLinksOpen = false; if (Current) await Shell.ReportRoomErrorAsync(exception, "Map links could not be loaded."); }
        finally { roomLinksOpening = false; if (Current) { StateHasChanged(); await Shell.DrainRoomRefreshAsync(); } }
    }
    private async Task ReloadRoomMapAsync()
    {
        var targetState = Catalogue.ConfirmedMapStateId;
        mapReloadNeeded = true;
        mapReloadsInFlight++;
        if (Current) StateHasChanged();
        try
        {
            var value = await Catalogue.LoadRoomMapAsync(Entry.RoomId, () => Current, lifetime.Token);
            if (Current && value is not null)
            { InstallRoomMap(value); observedMapState = targetState; mapReloadNeeded = false; }
        }
        catch
        {
            if (Current) mapReloadFailed = true;
            throw;
        }
        finally
        {
            mapReloadsInFlight--;
            if (Current) StateHasChanged();
        }
    }
    private async Task RetryRoomMapAsync(bool explicitRefresh = false)
    {
        if (!Current || !Shell.CanUseRoom || roomLinksOpen || imageBusy || explicitRefresh && mapReloadsInFlight != 0) return;
        mapReloadsInFlight++;
        StateHasChanged();
        try
        {
            var value = explicitRefresh
                ? await Catalogue.RefreshRoomMapAsync(Entry.RoomId, () => Current, lifetime.Token)
                : await Catalogue.LoadRoomMapAsync(Entry.RoomId, () => Current, lifetime.Token);
            if (Current && value is not null) { InstallRoomMap(value); mapReloadNeeded = false; }
        }
        catch (Exception exception)
        {
            if (Current)
            {
                mapReloadFailed = true;
                await Shell.ReportRoomErrorAsync(exception, "The room map could not be refreshed. The preceding sampled map has been retained.");
            }
        }
        finally { mapReloadsInFlight--; if (Current) StateHasChanged(); }
    }
    private async Task RoomLinksClosedAsync(bool changed)
    {
        roomLinksOpen = false;
        if (Current) { StateHasChanged(); await Shell.DrainRoomRefreshAsync(); }
    }
    private void IncorporateRoomResponse(ReceivedCacheResult<RoomTrackingResponse> received, ConfirmedCatalogue.MutationAdmission admission)
    {
        var response = received.Result;
        observingRoomResponse = true;
        try
        {
        var before = workspace.Queue.Confirmed.Document?.Header;
        var beforeDocument = workspace.Queue.Confirmed.Document;
        var incorporatedBefore = workspace.Queue.IncorporatedThrough;
        var reorders = workspace.Queue.PendingIntent.Where(x => x.Operation is ReorderRoomConnection).ToArray();
        workspace.Incorporate(response, nativeOwner!, segments => admission.PreparePublication(segments, RoomPublicationSources(response)));
        if (workspace.Queue.Confirmed.Document is { } admittedDocument && !ReferenceEquals(beforeDocument, admittedDocument))
            admission.ObserveRoomImage(admittedDocument);
        ObserveKeyboardResponse(response);
        ReleaseAcknowledgedRoomSources();
        var incorporatedReorders = reorders.Where(x => response.Results.Any(r => r.Receipt.OperationId == x.OperationId
                && r.Receipt.Sequence > incorporatedBefore && r.Receipt.Sequence <= workspace.Queue.IncorporatedThrough
                && !r.ProjectionPending && r.Receipt.Disposition is RoomTerminalDisposition.Committed or RoomTerminalDisposition.Satisfied))
            .Select(x => ((ReorderRoomConnection)x.Operation).EntityId).ToHashSet();
        if (beforeDocument is not null && workspace.Queue.Confirmed.Document is { } afterDocument && incorporatedReorders.Count != 0)
            TransferCanonicalConnectionSelection(beforeDocument, afterDocument, incorporatedReorders);
        ReconcileSceneSelectionAfterPull();
        foreach (var result in response.Results.Where(x => x.Receipt.Sequence <= workspace.Queue.IncorporatedThrough))
        {
            if (operationReturnFocus.Remove(result.Receipt.OperationId, out var focus))
            {
                pendingBoundaryFocus = focus;
                returnFocusFallback = operationFallbackInitiators.TryGetValue(result.Receipt.OperationId, out var fallback) ? (focus.Id, fallback, focus.Interaction) : null;
            }
            operationInitiators.Remove(result.Receipt.OperationId);
            operationFallbackInitiators.Remove(result.Receipt.OperationId);
        }
        ObserveImageResponse(response);
        var after = workspace.Queue.Confirmed.Document?.Header;
        if (MapContextChanged(before, after)) mapReloadNeeded = true;
        roomResponseObservationFailed = false;
        workspace.ClearAcceptanceFailure();
        }
        catch { roomResponseObservationFailed = true; throw; }
        finally
        {
            observingRoomResponse = false; NotifyRoomActions();
        }
    }
    private void PublishReceivedRoomCache(ReceivedCacheResult<RoomTrackingResponse> received, ConfirmedCatalogue.MutationAdmission admission)
    {
        // Called by each receiving request's finally, after its native/I and ACK
        // attempt, even if this visit departed or feature incorporation failed.
        // Cache handling never turns a lost ACK into another business execution.
        admission.PublishCache(received);
    }
    private static bool MapContextChanged(RoomHeaderSnapshot? before, RoomHeaderSnapshot? after) => before is not null && after is not null
        && (before.Values.ReferenceId != after.Values.ReferenceId || before.Observation.IsArchived != after.Observation.IsArchived || before.RoomGroupId != after.RoomGroupId);
}
