using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    private object? sceneOperationOwner;
    private void OpenScenePage() { if (CanAuthor && !Locked && !errorOpen && !disposed) Navigation.NavigateTo("/scene-import"); }
    public bool CanUseScene(object owner) => !RoomEntryPreparing && CanInspectScene(owner);
    public bool CanInspectScene(object owner) => CanInteract && !errorOpen && !busy && editor is null && editorLoading is null &&
        requirementPageLock is null && landingOwner is null && (sceneOperationOwner is null || ReferenceEquals(sceneOperationOwner, owner));
    public bool LockScene(object owner)
    {
        if (!CanAuthor || !CanUseScene(owner)) return false;
        sceneOperationOwner = owner; StateHasChanged(); return true;
    }
    public void ReleaseScene(object owner)
    {
        if (!ReferenceEquals(sceneOperationOwner, owner)) return;
        sceneOperationOwner = null;
        if (!disposed)
        {
            StateHasChanged();
            _ = DrainRefreshAsync();
        }
    }
    public async Task ReportSceneErrorAsync(Exception exception, string message, string? trace = null)
    {
        Console.Error.WriteLine(exception);
        await FailAsync(new ReadFailure(message, trace, exception), false);
        if (!disposed) StateHasChanged();
    }
    private readonly object sceneCompletionOwner = new();
    private readonly Queue<Guid> sceneExitQueue = new();
    private SceneInverseProposal? sceneInverse;
    private ElementReference sceneInverseDialog;
    private bool sceneInversePending, sceneInverseBusy;
    private string? sceneCompletionFeedback;
    private sealed record SceneCompletionNavigation(string Origin, string Target, string State);
    private SceneCompletionNavigation? sceneCompletionNavigation;
    private Action? retireSceneCompletionNavigation;

    private void NavigateToCommittedSceneRoom(Guid room)
    {
        retireSceneCompletionNavigation?.Invoke();
        var intent = new SceneCompletionNavigation(Navigation.Uri,
            Navigation.ToAbsoluteUri($"/rooms/{room}").AbsoluteUri, Guid.NewGuid().ToString("N"));
        CancellationTokenRegistration disposal = default;
        EventHandler<LocationChangedEventArgs>? departure = null;
        void Retire()
        {
            if (!ReferenceEquals(sceneCompletionNavigation, intent)) return;
            sceneCompletionNavigation = null;
            retireSceneCompletionNavigation = null;
            Navigation.LocationChanged -= departure;
            disposal.Dispose();
        }
        departure = (_, _) => Retire();
        sceneCompletionNavigation = intent;
        retireSceneCompletionNavigation = Retire;
        Navigation.LocationChanged += departure;
        disposal = lifetime.Token.Register(Retire);
        try
        {
            // NavigateTo may dispatch admission later. Only its matching attempt
            // consumes this intent; returning from NavigateTo is not arrival.
            Navigation.NavigateTo(intent.Target, new NavigationOptions { HistoryEntryState = intent.State });
        }
        catch { Retire(); throw; }
    }

    private bool TryAdmitSceneCompletionNavigation(LocationChangingContext context)
    {
        if (sceneCompletionNavigation is not { } intent) return false;
        // Consume even if another guard rejects or another handler cancels later.
        // History/back/forward can never reuse an admitted completion intent.
        retireSceneCompletionNavigation?.Invoke();
        return context.HistoryEntryState == intent.State && CanInteract && !context.CancellationToken.IsCancellationRequested && !errorOpen &&
            Navigation.Uri == intent.Origin && Navigation.ToAbsoluteUri(context.TargetLocation).AbsoluteUri == intent.Target &&
            // This private intent was issued by committed completion while it owned
            // the scene. Optional inverse processing may release that owner before
            // NavigateTo dispatches admission; it does not revoke this exact intent.
            (sceneOperationOwner is null || ReferenceEquals(sceneOperationOwner, sceneCompletionOwner)) && !busy && editor is null && editorLoading is null &&
            requirementPageLock is null && landingOwner is null && !RoomDepartureBlocked
            && roomBlocksUnattendedReplacement?.Invoke() != true;
    }

    public async Task CompleteSceneAsync(object owner, SceneAppliedReceipt receipt, Exception? publicationFailure, string? historyWarning)
    {
        if (!CanInteract) return;
        using var timing = OperationTiming.Start("scene.completion");
        sceneOperationOwner = sceneCompletionOwner;
        sceneCompletionFeedback = null;
        StateHasChanged();
        try
        {
        if (publicationFailure is not null)
        {
            timing.End(TimingOutcome.KnownCommitFollowupFailed);
            await ReportSceneErrorAsync(publicationFailure, "Scene import committed, but its updated room facts could not be installed. Do not repeat the import. Close this message and use Refresh to read saved facts; optional inverse setup was skipped.");
            return;
        }
        if (!CanInteract) return;
        try { NavigateToCommittedSceneRoom(receipt.RoomId); }
        catch (Exception exception) { await ReportSceneErrorAsync(exception, "Scene import committed; room selection failed. Do not repeat the import."); }
        foreach (var id in receipt.AppliedExitIds) sceneExitQueue.Enqueue(id);
        await AdvanceSceneInverseAsync();
        timing.End(disposed ? TimingOutcome.Retired : errorOpen ? TimingOutcome.KnownCommitFollowupFailed : sceneInverse is not null ? TimingOutcome.ReviewRequired : TimingOutcome.Complete);
        }
        finally
        {
            if (sceneInverse is null && sceneExitQueue.Count == 0) ReleaseScene(sceneCompletionOwner);
            await ReportHistoryWarningAsync(historyWarning);
        }
    }

    private async Task RenderSceneCompletionAsync()
    {
        if (!CanInteract || module is null) return;
        if (sceneInversePending && sceneInverse is not null && !errorOpen)
        {
            if (await module.InvokeAsync<bool>("showDialog", sceneInverseDialog, errorDialog)) sceneInversePending = false;
        }
        await module.InvokeVoidAsync("freezeDialogs", sceneInverseBusy, sceneInverseDialog);
    }

    private async Task AdvanceSceneInverseAsync()
    {
        sceneInverse = null;
        try
        {
            while (CanInteract && sceneExitQueue.TryDequeue(out var id))
            {
                using var response = await Http.PostAsJsonAsync("/api/v3/scene-import/inverse/prepare", new SceneInversePrepare(id), JsonOptions, lifetime.Token);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Inverse proposal could not be read.");
                sceneInverse = await response.Content.ReadFromJsonAsync<SceneInverseProposal>(JsonOptions, lifetime.Token);
                if (!CanInteract) return;
                if (sceneInverse is null) continue;
                sceneInversePending = true;
                if (!disposed) StateHasChanged();
                return;
            }
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception exception)
        {
            sceneExitQueue.Clear();
            if (!disposed) await ReportSceneErrorAsync(exception, "Import committed; optional inverse follow-up could not continue. No import rollback or replay was attempted.");
        }
        finally
        {
            if (sceneInverse is null) ReleaseScene(sceneCompletionOwner);
        }
    }

    private async Task ChooseSceneInverseAsync(bool update)
    {
        if (!CanAuthor || sceneInverseBusy || errorOpen || sceneInverse is not { } proposal) return;
        using var outcome = Runtime.ObserveMutation();
        sceneInverseBusy = true;
        string? historyWarning = null;
        try
        {
            if (update)
            {
                await Catalogue.MutateAsync(async admission =>
                {
                var source = admission.CaptureReadContext();
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/scene-import/inverse/confirm")
                    { Content = JsonContent.Create(new SceneInverseConfirm(proposal.SourceId, proposal.Baseline), options: JsonOptions) };
                var cacheRequest = admission.BindCache(request);
                outcome.Dispatched();
                using var response = await Http.SendAsync(request, CancellationToken.None);
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    outcome.OutcomeKnown();
                    sceneCompletionFeedback = "Import committed; inverse proposal changed or is no longer eligible. No inverse fields were written.";
                }
                else
                {
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Inverse confirmation outcome is unconfirmed.");
                    var received = await LogicBrowserCache.DecodeAsync<SceneInverseResult>(response, cacheRequest);
                    var result = received.Result;
                    historyWarning = result.HistoryWarning;
                    outcome.OutcomeKnown();
                    try
                    {
                    sceneCompletionFeedback = result.Applied ? null : result.Detail ?? "Import committed; inverse target was not updated.";
                    if (result.Applied)
                    {
                        sceneInverse = null; // Retire confirmed proposal before fallible publication.
                        try
                        {
                            if (result.Patch is null) throw new InvalidOperationException("Committed inverse facts are unavailable.");
                            admission.Publish(result.Patch, source);
                        }
                        catch (Exception exception)
                        {
                            sceneExitQueue.Clear();
                            await ReportSceneErrorAsync(exception, "Import and inverse update committed, but updated room facts are unavailable. Do not repeat either operation. Close this message and use Refresh to read saved facts.");
                        }
                    }
                    }
                    finally { admission.PublishCache(received); }
                }
                }, lifetime.Token);
            }
            // Both decline labels intentionally write nothing and advance.
        }
        catch (Exception exception)
        {
            if (!disposed) await ReportSceneErrorAsync(exception, "Import remains committed. Optional inverse outcome is unconfirmed; no automatic retry was attempted.");
        }
        finally
        {
            sceneInverseBusy = false;
            try
            {
            if (!disposed)
            {
                if (module is not null) await module.InvokeVoidAsync("closeDialogs", sceneInverseDialog);
                sceneInverse = null;
                await AdvanceSceneInverseAsync();
                if (sceneInverse is null && module is not null) await module.InvokeVoidAsync("restoreInitiator");
                StateHasChanged();
            }
            }
            finally { await ReportHistoryWarningAsync(historyWarning); }
        }
    }
}
