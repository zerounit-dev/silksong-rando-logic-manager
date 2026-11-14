using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private bool entryAdmissionAttempted, entryCheckAttempted, entryAdmissionActive, entryAdmissionFailed, entryAdmissionUnavailable;
    private bool entryCheckWaiting, entryCheckActive, entryCheckFailed;
    private long entryAdmissionGeneration, entryCheckGeneration;
    private RoomConfirmedState? targetedMissingFrom;

    private void EnsureEntryRequests()
    {
        if (Entry.AdmissionRequest is null || !Current || Shell.GeneralErrorOpen) return;
        // Both calls yield at short catalogue admission; neither awaits the other.
        if (!entryAdmissionAttempted) _ = AdmitEntryAsync();
        if (!entryCheckAttempted) _ = CheckEntryAsync();
    }

    private async Task AdmitEntryAsync()
    {
        if (!Current || workspace.IsAdmitted || entryAdmissionActive || entryAdmissionUnavailable
            || Entry.AdmissionRequest is not { } request || Shell.GeneralErrorOpen) return;
        var attempt = ++entryAdmissionGeneration;
        entryAdmissionAttempted = true;
        bool Owns() => Current && attempt == entryAdmissionGeneration;
        entryAdmissionActive = true;
        entryAdmissionFailed = false;
        StateHasChanged();
        try
        {
            var acknowledgement = await Catalogue.AdmitRoomBaselineAsync(Entry.RoomId, request, Owns, lifetime.Token);
            if (!Owns() || acknowledgement is null) return;
            workspace.AttachAdmission(acknowledgement);
            workspace.Queue.IntentAppended += ObserveAppendedRoomIntent;
            NotifyRoomActions();
            // The attachment changes enabled attributes, never workspace/native identity.
            initialEnabledRenderCompleted = false;
            StateHasChanged();
        }
        catch (ConfirmedCatalogue.RoomTrackingUnavailableException)
        { if (Owns()) { entryAdmissionUnavailable = true; FailSwitchSetup(); StateHasChanged(); } }
        catch (OperationCanceledException) when (!Owns()) { }
        catch (Exception exception)
        {
            if (Owns())
            {
                entryAdmissionFailed = true;
                FailSwitchSetup();
                await Shell.ReportRoomErrorAsync(exception, "Room tracking could not be admitted. Local presentation is retained and editing remains disabled. Retry refresh retries the same admission; no write is repeated.");
            }
        }
        finally
        {
            if (Owns()) { entryAdmissionActive = false; StateHasChanged(); }
        }
    }

    private async Task CheckEntryAsync()
    {
        if (!Current || entryCheckWaiting || entryCheckActive || Shell.GeneralErrorOpen) return;
        var attempt = ++entryCheckGeneration;
        entryCheckAttempted = true;
        var confirmedAtRequest = workspace.Confirmed;
        bool Owns() => Current && attempt == entryCheckGeneration;
        entryCheckWaiting = true;
        entryCheckFailed = false;
        StateHasChanged();
        try
        {
            var check = await Catalogue.CheckRoomEntryAsync(Entry.RoomId, Owns, active =>
            {
                if (!Owns()) return;
                entryCheckWaiting = !active;
                entryCheckActive = active;
                StateHasChanged();
            }, lifetime.Token);
            if (!Owns() || check is null) return;
            var target = check.Observation;
            var current = workspace.Confirmed;
            if (current.Document is not { } document) return; // Incorporated local deletion.
            if (target is RoomPresentObservation present)
            {
                // Equal check is strictly a no-op. A save during checking does not
                // retire ownership, but only complete coverage can supersede its sample.
                if (RoomEntryProtocol.Matches(present, document)) return;
                if (!current.ExternallyStale && present.Observation.CacheFlagUtc < document.Observation.CacheFlagUtc) return;
                // Comparing the original captured baseline must not certify a newer
                // partial projection or clear an unrelated pre-existing stale signal.
                targetedMissingFrom = null;
                deferredRoomPull = true;
            }
            else
            {
                // A Missing sample has no timestamp that could dominate an in-flight
                // save. Only the unchanged complete receiver can adopt it directly.
                targetedMissingFrom = check.RequestedCatalogueEpoch == check.CompletedCatalogueEpoch
                    && ReferenceEquals(current, confirmedAtRequest) ? current : null;
                deferredRoomPull = true;
            }
        }
        catch (OperationCanceledException) when (!Owns()) { }
        catch (Exception exception)
        {
            if (Owns())
            {
                entryCheckFailed = true;
                await Shell.ReportRoomErrorAsync(exception, deferredRoomPull || roomPullFailed
                    ? "Room freshness could not be checked. Existing values and local work have been retained. Retry refresh reads the changed room; it does not repeat a saved operation."
                    : "Room freshness could not be checked. Existing values and local work have been retained. Retry refresh repeats only the check; it does not repeat a saved operation.");
            }
        }
        finally
        {
            if (Owns())
            {
                entryCheckWaiting = entryCheckActive = false;
                StateHasChanged();
                if (!entryCheckFailed) await ReconcileFreshnessAsync();
            }
        }
    }

    private Task RetryEntryRefreshAsync()
    {
        if (!Current || Shell.GeneralErrorOpen) return Task.CompletedTask;
        if (entryAdmissionFailed && !workspace.IsAdmitted) return AdmitEntryAsync();
        if (deferredRoomPull || roomPullFailed)
        {
            // An established stale obligation needs the stronger coherent read,
            // not another check. Explicit Retry acknowledges the failed check;
            // existing local-work guards can still defer that read safely.
            entryCheckFailed = false;
            return RetryRoomPullAsync();
        }
        if (entryCheckFailed) return CheckEntryAsync();
        return RetryRoomPullAsync();
    }

    private void RetireEntryRequests()
    {
        ++entryAdmissionGeneration;
        ++entryCheckGeneration;
        ++roomReadGeneration;
        entryCheckWaiting = entryCheckActive = entryAdmissionActive = false;
    }

    private bool EntryRefreshFailed => entryAdmissionFailed || entryCheckFailed || roomPullFailed;
    private string? EntryFreshnessIcon => !Current ? null : EntryRefreshFailed || entryAdmissionUnavailable
        ? "fa-triangle-exclamation" : entryCheckActive || roomPullActive ? "fa-rotate fa-spin"
        : !workspace.IsAdmitted || entryCheckWaiting || deferredRoomPull ? "fa-rotate" : null;
    private string EntryFreshnessLabel
    {
        get
        {
            if (!Current) return "Room freshness inactive";
            var states = new List<string>();
            if (entryAdmissionUnavailable) states.Add("Room tracking unavailable; copy pending work before refreshing");
            else if (entryAdmissionFailed) states.Add("Room admission failed; Retry refresh retries the same admission");
            else if (!workspace.IsAdmitted) states.Add("Waiting for room tracking admission");
            if (entryCheckFailed) states.Add(deferredRoomPull || roomPullFailed
                ? "Room freshness check failed; Retry refresh reads the changed room"
                : "Room freshness check failed; Retry refresh repeats the check");
            else if (entryCheckActive) states.Add("Checking room freshness");
            else if (entryCheckWaiting) states.Add("Waiting to check room freshness");
            if (roomPullFailed) states.Add("Room refresh failed; Retry refresh repeats the coherent read");
            else if (roomPullActive) states.Add("Refreshing room");
            else if (deferredRoomPull) states.Add("Room changed; refresh deferred while local work is retained");
            return states.Count == 0 ? "Room freshness idle" : string.Join(". ", states);
        }
    }
}
