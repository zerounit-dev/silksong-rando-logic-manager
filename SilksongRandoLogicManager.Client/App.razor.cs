using System.Net.Http.Json;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
    private Sidebar snapshot => Catalogue.Sidebar;
    private SidebarPresentation? presentation;
    private SidebarSearchIndex? searchIndex => Catalogue.Search;
    private SidebarSearchIndex? appliedSearchIndex;
    private ImmutableArray<SidebarGroupView> displayGroups = [];
    private SidebarTerms filterTerms = SidebarTerms.Prepare("", "", "");
    public Sidebar Snapshot => snapshot!;
    private bool ready, initialFailed, disposed, busy, refreshing, refreshQueued, refreshForceQueued, errorOpen, advanced, archive, editorMissing, confirming;
    private string nameTerm = "", locationTerm = "", requirementTerm = "", draftName = "";
    private string? draftZone, pending, treeFeedback, editorFeedback, errorMessage, errorTrace;
    private string errorDetails = "Diagnostic metadata unavailable.";
    private string? errorCopyFeedback;
    private object? errorInstance;
    private bool errorCopyPending, errorFallbackFocusPending;
    private ElementReference errorClose;
    private bool draftVirtual;
    private GroupEditor? editor;
    private Guid? editorLoading, lastSelected;
    private Task? refreshFlight;
    private long view, editorSequence;
    private readonly HashSet<Guid> expanded = [];
    private readonly CancellationTokenSource lifetime = new();
    private ElementReference sidebarRoot, groupDialog, deleteDialog, errorDialog, nameInput;
    private IJSObjectReference? module, browserOwner, dragModule, dragOwner;
    private DotNetObjectReference<App>? callback;
    private readonly string ownerId = Guid.NewGuid().ToString();
    private bool mountPending, dialogPending, errorNativeUnavailable, restoreInitiatorPending;
    private bool initialLoadTimingActive, initialLoadTimingRetired;
    private bool initialCacheCheckPending;
    private object? requirementPageLock;
    private ConfirmedManager? initialRequirements;
    private IDisposable? navigationAdmission;
    private bool SidebarFeatureLocked => !CanInteract || busy || editor is not null || editorLoading is not null || sceneOperationOwner is not null || landingOwner is not null;
    private bool SidebarLocked => SidebarFeatureLocked || cacheReplacing;
    private bool Locked => SidebarLocked || requirementPageLock is not null || RoomDepartureBlocked || cacheOpen;
    public bool NavigationBlocked => Locked;

    public bool GeneralErrorOpen => errorOpen;
    public bool CanUseRequirements(object owner) => CanInteract && !errorOpen && !RoomEntryPreparing && !SidebarLocked && (requirementPageLock is null || ReferenceEquals(requirementPageLock, owner));
    public bool LockRequirements(object owner)
    {
        if (!CanUseRequirements(owner)) return false;
        requirementPageLock = owner; StateHasChanged(); return true;
    }
    public void ReleaseRequirements(object owner)
    {
        if (!ReferenceEquals(requirementPageLock, owner)) return;
        requirementPageLock = null;
        if (!disposed)
        {
            StateHasChanged();
            _ = DrainRefreshAsync();
        }
    }
    public ConfirmedManager? TakeInitialRequirements()
    { var result = initialRequirements; initialRequirements = null; return result; }
    public async Task ReportRequirementsErrorAsync(Exception exception, string message)
    {
        Console.Error.WriteLine(exception);
        await FailAsync(new ReadFailure(message, null, exception), false);
        if (!disposed) StateHasChanged();
    }
    public Task RunCatalogueMutationAsync(Func<ConfirmedCatalogue.MutationAdmission, Task> operation, CancellationToken token) => Catalogue.MutateAsync(operation, token);
    private void Requirements() { if (!Locked && !errorOpen) Navigation.NavigateTo("/requirements/predicates"); }

    protected override void OnInitialized()
    {
        Runtime.Changed += RuntimeChanged;
        Runtime.ObserveUpdateRisk += CaptureClientUpdateRisk;
        Access.Changed += AccessChanged;
        navigationAdmission = Navigation.RegisterLocationChangingHandler(async context =>
        {
            var attempt = BeginRoomEntryNavigation(context);
            var previousPreparation = roomEntryPreparation;
            // Supersession cancels the old read, but its sole lease must actually
            // drain before applying guards or starting another preparation.
            await previousPreparation;
            if (!ReferenceEquals(roomEntryNavigation, attempt) || context.CancellationToken.IsCancellationRequested)
            { context.PreventNavigation(); return; }
            if (!CanInteract) { context.PreventNavigation(); return; }
            var sceneCompletionNavigation = TryAdmitSceneCompletionNavigation(context);
            if (RoomDepartureBlocked || requirementPageLock is not null || landingOwner is not null)
            {
                context.PreventNavigation();
                return;
            }
            // NavigateTo callers can supply root- or base-relative targets;
            // ToBaseRelativePath requires an absolute URI, even for in-base routes.
            var absoluteTarget = Navigation.ToAbsoluteUri(context.TargetLocation).AbsoluteUri;
            var targetPath = absoluteTarget.StartsWith(Navigation.BaseUri, StringComparison.Ordinal)
                ? Navigation.ToBaseRelativePath(absoluteTarget) : absoluteTarget;
            if ((RoomRoute(targetPath) is not null || RoomRoute(Navigation.ToBaseRelativePath(Navigation.Uri)) is not null) && (Locked && !sceneCompletionNavigation || errorOpen) ||
                IsHomeRoute(targetPath) && (Locked || errorOpen) ||
                Pages.Requirements.IsManagerRoute(targetPath) && (SidebarLocked || errorOpen) ||
                (AuditRoom(targetPath) is not null || AuditRoute) && (Locked || errorOpen))
            {
                context.PreventNavigation();
                return;
            }
            await AdmitFrameworkRoomDepartureAsync(context);
            if (ready && roomWorkspaceOwner is null
                && RoomRoute(Navigation.ToBaseRelativePath(attempt.Source)) is null
                && absoluteTarget.StartsWith(Navigation.BaseUri, StringComparison.Ordinal)
                && RoomRoute(targetPath) is { } room)
            {
                if (!OwnsRoomEntryNavigation(attempt, context) || !RoomEntrySourceAvailable(sceneCompletionNavigation))
                { context.PreventNavigation(); return; }
                await (roomEntryPreparation = PrepareRoomEntryAsync(attempt, context, room, sceneCompletionNavigation));
            }
        });
        Navigation.LocationChanged += LocationChanged;
        Catalogue.Changed += CatalogueChanged;
        Catalogue.LocalSettled += CatalogueLocalSettled;
        Catalogue.BackgroundFailed += BackgroundCacheFailed;
        Browser.Failed += BackgroundCacheFailed;
    }

    private void CatalogueChanged()
    {
        if (disposed || Runtime.Retired || !ready) return;
        Apply(Catalogue.Sidebar);
        StateHasChanged();
        _ = ReconcileRoomFreshnessAsync();
    }

    private void CatalogueLocalSettled()
    {
        if (disposed || Runtime.Retired || !ready) return;
        _ = InvokeAsync(async () =>
        {
            if (!disposed && ready && !errorOpen) await ReconcileRoomFreshnessAsync();
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Runtime.Retired)
        {
            if (updateFocusPending)
            {
                updateFocusPending = false;
                if (!Runtime.AutomaticUpdateInProgress)
                    try { await updateHeading.FocusAsync(); } catch { }
            }
            if (!disposed && Runtime.CanAutomaticallyUpdate) await UpdateClientCoreAsync(true);
            return;
        }
        if (firstRender)
        {
            initialLoadTimingActive = true;
            var initialUri = Navigation.Uri;
            bool TimingOwns() => !disposed && !initialLoadTimingRetired && Navigation.Uri == initialUri;
            using var initialTiming = new InitialLoadTiming("managed pre-ready total (nested stages; inclusive progress/render waits)", owns: TimingOwns);
            using var phaseTiming = new InitialLoadTiming("browser module import", owns: TimingOwns);
            try
            {
                callback = DotNetObjectReference.Create(this);
                var bootstrapFailed = await JS.InvokeAsync<bool>("logicInitialLoading.claim", callback);
                if (bootstrapFailed) await BootstrapFailed();
                if (disposed || initialFailed || Runtime.Retired) return;
                var installRequired = await Runtime.ReadInstallIntentAsync();
                if (disposed || initialFailed || Runtime.Retired) return;
                updateTransport = await ApplicationModuleLoader.ImportAsync(JS, Runtime, "workspace-request.js");
                if (disposed || initialFailed || Runtime.Retired) return;
                await updateTransport.InvokeVoidAsync("initialize", callback, ApplicationBuild.Version);
                Runtime.RequireActive();
                if (disposed || initialFailed) return;
                var imported = await ApplicationModuleLoader.ImportAsync(JS, Runtime, "catalogue.js");
                if (disposed || initialFailed) { await imported.DisposeAsync(); return; }
                module = imported;
                PublishRoomWarning();
                phaseTiming.Next("preferences (inclusive progress/render waits)");
                await ReportInitialProgressAsync(new(InitialLoadingPhase.Neutral));
                if (disposed || initialFailed || Runtime.Retired) return;
                var preferences = await module.InvokeAsync<Preferences?>("loadPreferences");
                if (disposed || initialFailed || Runtime.Retired) return;
                phaseTiming.Next("initial catalogue (nested stages; inclusive progress/render waits)");
                if (installRequired) await Browser.InstallCurrentRuntimeAsync(lifetime.Token, ReportInitialProgressAsync);
                if (disposed || initialFailed || Runtime.Retired) return;
                initialCacheCheckPending = await Catalogue.InitializeAsync(ReportInitialProgressAsync, installRequired);
                if (disposed || initialFailed || Runtime.Retired) return;
                phaseTiming.Next("requirements manager handoff applicability check", Catalogue.Initialized);
                if (Pages.Requirements.IsManagerRoute(Navigation.ToBaseRelativePath(Navigation.Uri)))
                {
                    initialRequirements = Catalogue.Manager ?? throw new JsonException("Missing initialized requirement definitions.");
                }
                phaseTiming.Next("route map applicability/preparation check (inclusive progress/render waits)");
                await LoadInitialLandingAsync();
                if (disposed || initialFailed || Runtime.Retired) return;
                phaseTiming.Next("route room applicability/preparation check (inclusive progress/render waits; not native readiness)");
                await LoadInitialRoomAsync();
                if (disposed || initialFailed || Runtime.Retired) return;
                phaseTiming.Next("sidebar and preferences preparation");
                initialProgress = new(InitialLoadingPhase.FinalPreparation);
                Apply(Catalogue.Sidebar, preferences?.ShowArchivedRooms ?? false);
                if (preferences?.ExpandedGroupIds is { } ids) expanded.UnionWith(ids);
                Runtime.CompleteStartup();
                ReleaseBootstrapLoading();
                ready = true; mountPending = true; lastSelected = SelectedId();
                phaseTiming.Complete(Catalogue.Initialized);
                initialTiming.Complete(Catalogue.Initialized);
            }
            catch (Exception exception)
            {
                phaseTiming.Dispose();
                initialTiming.Dispose();
                await FailInitialLoadingAsync(exception);
            }
            finally { initialLoadTimingActive = false; }
            if (!disposed) StateHasChanged();
            return;
        }
        if (errorFallbackFocusPending && errorOpen && !disposed)
        {
            errorFallbackFocusPending = false;
            try { await errorClose.FocusAsync(); } catch { /* Markup and native autofocus remain available. */ }
        }
        if (disposed || Runtime.Retired || module is null) return;
        if (departureDialogPending && roomDeparture is not null && !errorOpen)
        {
            var request = roomDeparture;
            var replacement = replacementDeparture;
            var shown = await module.InvokeAsync<bool>("showDialog", departureDialog, errorDialog);
            if (shown && ReferenceEquals(roomDeparture, request) && ReferenceEquals(replacementDeparture, replacement))
                departureDialogPending = false;
        }
        // Progress renders must not mount or maintain ordinary application DOM.
        // Ordinary initial failures retain the loader; session access keeps its owner.
        if (!ready)
        {
            if (errorOpen) await ShowErrorAsync();
            return;
        }
        CompleteShellObservation();
        using var mountTiming = new InitialLoadTiming("post-render browser mount (not physical paint or room native readiness)", mountPending, () => !disposed && !errorOpen);
        if (mountPending)
        {
            mountPending = false;
            try
            {
                browserOwner = await module.InvokeAsync<IJSObjectReference>("mount", callback);
                if (disposed || Runtime.Retired) { await ReleaseOwnersAsync(); return; }
                mountTiming.Next("post-render sidebar owner applicability/reconciliation check");
            }
            catch (Exception exception)
            {
                mountTiming.Dispose();
                if (!disposed) { await FailAsync(exception, false); StateHasChanged(); }
            }
        }
        await ReconcileSidebarOwnerAsync();
        mountTiming.Complete(browserOwner is not null);
        if (initialCacheCheckPending && browserOwner is not null)
        {
            initialCacheCheckPending = false;
            _ = CheckInitialCacheAsync();
        }
        try
        {
            if (dialogPending && !busy && !errorOpen && editor is not null)
            {
                var editorId = editor.Group.Id;
                var confirmation = confirming;
                // The browser checks the error dialog again in the same native
                // operation as replacement, closing the interop-await race.
                var shown = await module.InvokeAsync<bool>("replaceDialog",
                    confirmation ? groupDialog : deleteDialog,
                    confirmation ? deleteDialog : groupDialog, errorDialog);
                if (disposed || Runtime.Retired) return;
                if (shown && !errorOpen && editor?.Group.Id == editorId && confirming == confirmation)
                {
                    dialogPending = false;
                    if (!confirmation) await module.InvokeVoidAsync("select", nameInput, errorDialog);
                }
            }
            if (disposed || Runtime.Retired) return;
            await RenderCacheManagementAsync();
            await RenderSceneCompletionAsync();
            if (disposed || Runtime.Retired) return;
            await module.InvokeVoidAsync("freezeDialogs", busy, groupDialog, deleteDialog);
            // Landing closes accepted drafts before its follow-up reads finish.
            // Retain the initiator until owner release renders the launcher enabled.
            if (restoreInitiatorPending && !busy && !errorOpen && landingOwner is null)
            {
                restoreInitiatorPending = false;
                await module.InvokeVoidAsync("restoreInitiator");
            }
            if (errorOpen) await ShowErrorAsync();
            if (auditErrorFocusPending && !errorOpen)
            {
                if (await module.InvokeAsync<bool>("restoreErrorFocus")) auditErrorFocusPending = false;
            }
        }
        catch (Exception exception)
        {
            if (!disposed && !errorOpen) { await FailAsync(exception, false); StateHasChanged(); }
            else Console.Error.WriteLine(exception);
        }
    }

    private async Task<T> ReadAsync<T>(string path)
    {
        using var response = await Http.GetAsync("/api/v3/" + path, lifetime.Token);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, lifetime.Token);
            throw new ReadFailure(error?.Message ?? "The requested data could not be loaded.", error?.TraceId, status: (int)response.StatusCode, code: error?.Code);
        }
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, lifetime.Token) ?? throw new JsonException("Missing response.");
    }

    private async Task CheckInitialCacheAsync()
    {
        try
        {
            await Browser.YieldAsync(lifetime.Token);
            if (!disposed && !Runtime.Retired) await Browser.StartupAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error) { if (!disposed) BackgroundCacheFailed(error); }
    }
    private void BackgroundCacheFailed(Exception error)
    {
        if (disposed || Runtime.Retired) return;
        _ = InvokeAsync(async () =>
        {
            if (disposed || Runtime.Retired) return;
            if (!ready) await FailInitialLoadingAsync(error);
            else await FailAsync(error, false);
            if (!disposed) StateHasChanged();
        });
    }

    private Task RefreshAsync(bool routine)
    {
        if (!CanInteract || !ready || routine && errorOpen) return Task.CompletedTask;
        var force = !routine || refreshForceQueued;
        if (busy || roomSwitch is not null || Catalogue.MutationActive)
        {
            refreshQueued = true; refreshForceQueued |= force;
            return Task.CompletedTask;
        }
        refreshQueued = refreshForceQueued = false;
        if (refreshing)
        {
            // The existing RefreshCore owns this shared flight's error host.
            _ = Catalogue.RefreshAsync(force);
            return refreshFlight ?? Task.CompletedTask;
        }
        return refreshFlight = RefreshCoreAsync(force);
    }

    private async Task DrainRefreshAsync()
    {
        if (!CanInteract || busy || roomSwitch is not null || Catalogue.MutationActive || errorOpen) return;
        if (refreshQueued || Catalogue.HasQueuedRefresh) await RefreshAsync(true);
        if (CanInteract && !errorOpen) await ReconcileRoomFreshnessAsync();
    }

    private async Task RefreshCoreAsync(bool force)
    {
        using var timing = OperationTiming.Start("shared.explicit-refresh.combined-read-decode-prepare");
        refreshing = true;
        StateHasChanged();
        try
        {
            await Catalogue.RefreshAsync(force);
            timing.End(disposed ? TimingOutcome.Retired : TimingOutcome.Returned);
        }
        catch (Exception exception) { if (!disposed) await FailAsync(exception, false); }
        finally
        {
            refreshing = false;
            if (!disposed)
            {
                StateHasChanged();
            }
        }
    }

    private void Apply(Sidebar value, bool? showArchive = null)
    {
        if (Runtime.Retired) return;
        using var timing = OperationTiming.Start("shell.apply", enabled: ready);
        // Callers have validated the whole result and admitted its read/mutation
        // owner. Prepare without altering any published snapshot or move baseline.
        var next = Catalogue.Presentation!;
        var visible = ReferenceEquals(next, presentation) && ReferenceEquals(searchIndex, appliedSearchIndex) && (showArchive ?? archive) == archive
            ? displayGroups : next.Filter(searchIndex, filterTerms, showArchive ?? archive, displayGroups);
        presentation = next; displayGroups = visible; appliedSearchIndex = searchIndex;
        archive = showArchive ?? archive; view++;
        timing.End();
    }

    private async Task MutateAsync(string action, HttpMethod method, string path, object body, Func<MutationResult, Task> apply, bool modal = false)
    {
        if (!CanAuthor || disposed || busy || !modal && Locked || errorOpen) return;
        using var timing = OperationTiming.Start("shell.mutation");
        using var outcome = Runtime.ObserveMutation();
        busy = true; pending = action; treeFeedback = null;
        var outcomeConfirmed = false;
        var diagnosticCommitKnown = false;
        // The immutable body is captured by each caller before the first await.
        StateHasChanged();
        try
        {
            if (action == "create-group") await module!.InvokeVoidAsync("rememberInitiator");
            if (module is not null) await module.InvokeVoidAsync("freezeDialogs", true, groupDialog, deleteDialog);
            // Retire publication immediately, then drain the sole sidebar read
            // before sending a write. Queued refreshes wait for mutation cleanup.
            using var admissionTiming = OperationTiming.Start("shell.mutation.admission");
            await Catalogue.MutateAsync(async admission =>
            {
            admissionTiming.End();
            if (disposed) return;
            using var admittedTiming = OperationTiming.Start("shell.mutation.admitted", parent: timing.Id);
            var source = admission.CaptureReadContext();
            using var request = new HttpRequestMessage(method, "/api/v3/" + path) { Content = JsonContent.Create(body, body.GetType()) };
            var cacheRequest = admission.BindCache(request);
            using var sendTiming = OperationTiming.Start("shell.mutation.buffered-send");
            outcome.Dispatched();
            using var response = await Http.SendAsync(request, lifetime.Token);
            sendTiming.End();
            using var decodeTiming = OperationTiming.Start("shell.mutation.decode");
            if (response.IsSuccessStatusCode)
            {
                var received = await LogicBrowserCache.DecodeAsync<MutationResult>(response, cacheRequest, lifetime.Token);
                var result = received.Result;
                try
                {
                decodeTiming.End(TimingOutcome.ResponseDecoded);
                if (result.Shared is null) throw new JsonException();
                if (action is "create-group" or "save")
                {
                    Validate(result.GroupEditor ?? throw new JsonException());
                    if (result.Shared.Rooms is not { } patch || !patch.Groups.Any(x => x.After?.Id == result.GroupEditor.Group.Id)
                        || action == "save" && result.GroupEditor.Group.Id != editor?.Group.Id) throw new JsonException();
                }
                if (action.StartsWith("create-room:") && (result.CreatedRoomId is not { } created || result.Shared.Rooms is not { } roomPatch
                    || !roomPatch.Replacements.Any(x => x.After.Observation.RoomId == created))) throw new JsonException();
                if (action == "auto" && result.MatchedRoomCount is not >= 0) throw new JsonException();
                diagnosticCommitKnown = true;
                outcome.OutcomeKnown();
                if (disposed) return;
                admission.Publish(result.Shared, source);
                outcomeConfirmed = true;
                await apply(result);
                admittedTiming.End(disposed ? TimingOutcome.Retired : TimingOutcome.Complete);
                timing.End(disposed ? TimingOutcome.Retired : TimingOutcome.Complete);
                }
                finally { admission.PublishCache(received); }
            }
            else
            {
                var error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, lifetime.Token) ?? throw new JsonException();
                if (!((int)response.StatusCode == 404 && error.Code == "missing"
                    || (int)response.StatusCode == 409 && error.Code == "conflict"))
                    throw new ReadFailure("The mutation outcome could not be confirmed.", error.TraceId, status: (int)response.StatusCode, code: error.Code);
                if (error.Shared is null) throw new JsonException();
                if (string.IsNullOrWhiteSpace(error.Message)) throw new JsonException();
                if (action == "save" && (int)response.StatusCode == 409)
                {
                    Validate(error.GroupEditor ?? throw new JsonException());
                    if (error.GroupEditor.Group.Id != editor?.Group.Id) throw new JsonException();
                }
                outcome.OutcomeKnown();
                if (disposed) return;
                admission.Publish(error.Shared, source);
                outcomeConfirmed = true;
                if (modal)
                {
                    editorFeedback = error.Message;
                    if (error.Code == "missing") editorMissing = true;
                    else if (action == "save") editor = error.GroupEditor;
                }
                else treeFeedback = error.Message;
                decodeTiming.End(TimingOutcome.ResponseDecoded);
                admittedTiming.End(TimingOutcome.Rejected);
                timing.End(TimingOutcome.Rejected);
            }
            }, lifetime.Token);
        }
        catch (Exception exception) { timing.End(disposed ? TimingOutcome.Retired : diagnosticCommitKnown ? TimingOutcome.KnownCommitFollowupFailed : TimingOutcome.Uncertain); if (!disposed) await FailAsync(exception, !outcomeConfirmed); }
        finally
        {
            busy = false; pending = null;
            if (!disposed)
            {
                try { if (module is not null) await module.InvokeVoidAsync("freezeDialogs", false, groupDialog, deleteDialog); }
                catch (Exception exception) { await FailAsync(exception, false); }
                StateHasChanged();
                await DrainRefreshAsync();
            }
        }
    }

    private Task CreateGroupAsync() => MutateAsync("create-group", HttpMethod.Post, "room-groups", new { }, async result =>
    {
        InstallEditor(result.GroupEditor!); expanded.Add(editor!.Group.Id); dialogPending = true;
        await PersistAsync();
    });
    private async Task CreateRoomAsync(Guid? id)
    {
        var sourceNavigation = creationNavigationOwner;
        Guid? created = null;
        await MutateAsync($"create-room:{id}", HttpMethod.Post, "rooms", new CreateRoom(id), result =>
        { if (id is { } group) expanded.Add(group); created = result.CreatedRoomId; return Task.CompletedTask; });
        // Creation is already committed. Only this exact returned target departs;
        // canceling its navigation never repeats or undoes creation.
        if (!disposed && ReferenceEquals(sourceNavigation, creationNavigationOwner)
            && created is { } target && Catalogue.Rooms.ContainsKey(target)) Navigation.NavigateTo($"/rooms/{target}");
    }
    private Task AutoMatchAsync() => MutateAsync("auto", HttpMethod.Post, "rooms/auto-group", new { }, _ => Task.CompletedTask);

    private async Task OpenEditorAsync(Guid id)
    {
        if (!CanAuthor || disposed || Locked || errorOpen) return;
        using var timing = OperationTiming.Start("shell.group-editor.read-prepare");
        editorLoading = id; var sequence = ++editorSequence;
        try
        {
            await module!.InvokeVoidAsync("rememberInitiator");
            var result = await ReadAsync<GroupEditor>($"room-groups/{id}"); Validate(result);
            if (result.Group.Id != id) throw new JsonException();
            if (disposed || sequence != editorSequence || editorLoading != id) return;
            InstallEditor(result); dialogPending = true;
            timing.End();
        }
        catch (Exception exception) { if (!disposed && sequence == editorSequence) await FailAsync(exception, false); }
        finally { if (sequence == editorSequence) editorLoading = null; }
    }
    private void InstallEditor(GroupEditor value)
    {
        editor = value; draftName = value.Group.FriendlyName; draftZone = value.Group.ZoneReferenceText;
        draftVirtual = value.Group.IsVirtual; editorFeedback = null; editorMissing = false; confirming = false;
    }
    private Task SaveGroupAsync()
    {
        if (editor is null || editorMissing) return Task.CompletedTask;
        var id = editor.Group.Id;
        return MutateAsync("save", HttpMethod.Patch, $"room-groups/{id}", new SaveGroup(editor.Group, new(draftName, draftZone, draftVirtual)), result => FinishEditorAsync(), true);
    }
    private async Task FinishEditorAsync()
    {
        editor = null; confirming = false; editorFeedback = null;
        restoreInitiatorPending = true;
        await module!.InvokeVoidAsync("closeDialogs", groupDialog, deleteDialog);
    }
    private Task CloseEditorAsync() => ModalActionAsync(async () => { if (!busy && !errorOpen) await FinishEditorAsync(); });
    private async Task ModalActionAsync(Func<Task> action)
    {
        if (!CanInteract) return;
        try { await action(); }
        catch (Exception exception) { if (!disposed) await FailAsync(exception, false); }
    }
    private Task OpenDeleteAsync()
    {
        if (CanInteract && !busy && !errorOpen && !editorMissing && editor is not null)
        { confirming = true; dialogPending = true; }
        return Task.CompletedTask;
    }
    private Task CloseDeleteAsync()
    {
        if (CanInteract && !busy && !errorOpen && editor is not null)
        { confirming = false; dialogPending = true; }
        return Task.CompletedTask;
    }
    private Task DeleteGroupAsync()
    {
        if (editor is null || editorMissing) return Task.CompletedTask;
        var id = editor.Group.Id;
        return MutateAsync("delete", HttpMethod.Delete, $"room-groups/{id}", new { }, async result => { expanded.Remove(id); await FinishEditorAsync(); await PersistAsync(); }, true);
    }

    public sealed record MoveIntent(string Owner, long ViewVersion, string Family, Guid SourceId, Guid? GroupId, int Index);
    [JSInvokable]
    public Task SubmitSidebarMoveAsync(MoveIntent intent)
    {
        if (!CanInteract || Locked || Searching || intent.Owner != ownerId || intent.ViewVersion != view || intent.Index < 0) return Task.CompletedTask;
        if (intent.Family == "group")
        {
            if (!presentation!.ByGroup.ContainsKey(new(intent.SourceId)) || intent.Index > presentation.GroupIds.Length) return Task.CompletedTask;
            return MutateAsync($"group:{intent.SourceId}", HttpMethod.Post, $"room-groups/{intent.SourceId}/move",
                new MoveGroup(intent.Index, presentation.GroupIds), _ => Task.CompletedTask);
        }
        var room = presentation!.ByRoom.GetValueOrDefault(intent.SourceId)?.Room;
        if (intent.Family != "room" || room is null || room.IsArchived || intent.GroupId is { } id && !presentation.ByGroup.ContainsKey(new(id))
            || intent.Index > ActiveIds(intent.GroupId).Length) return Task.CompletedTask;
        return MutateAsync($"room:{room.Id}", HttpMethod.Post, $"rooms/{room.Id}/move",
            new MoveRoom(intent.GroupId, intent.Index, room.GroupId, ActiveIds(room.GroupId), ActiveIds(intent.GroupId)), _ => Task.CompletedTask);
    }

    private async Task FailAsync(Exception exception, bool mutation, bool initialLoading = false)
    {
        Console.Error.WriteLine(exception);
        if (Runtime.Retired) return;
        if (errorOpen || disposed || Runtime.Retired) return;
        errorMessage = mutation ? "The mutation outcome could not be confirmed. Review current data before taking another action."
            : exception is SessionAccessException denied ? denied.Denial.Message : exception is ReadFailure failure ? failure.Message : "An unexpected error occurred. Consult the logs when contacting the developer.";
        errorTrace = ErrorDiagnostics.Trace((exception as ReadFailure)?.Trace);
        errorDetails = ErrorDiagnostics.Render(ErrorDiagnostics.Capture(exception));
        if (initialLoading)
        {
            Console.Error.WriteLine(errorDetails);
            return; // Keep the bounded diagnostic in console output, not a general dialog.
        }
        errorInstance = new object();
        errorCopyPending = false; errorCopyFeedback = null;
        errorFallbackFocusPending = module is null || errorNativeUnavailable;
        errorOpen = true;
        await ShowErrorAsync();
    }
    private async Task ShowErrorAsync()
    {
        if (module is null || errorNativeUnavailable || disposed || Runtime.Retired) return;
        try { await module.InvokeVoidAsync("showError", errorDialog); }
        catch (Exception exception) { Console.Error.WriteLine(exception); errorNativeUnavailable = true; errorFallbackFocusPending = true; if (!disposed) StateHasChanged(); }
    }
    private async Task CopyErrorDetailsAsync()
    {
        if (!errorOpen || disposed || errorCopyPending || errorInstance is null) return;
        var instance = errorInstance;
        var text = errorDetails;
        errorCopyPending = true; errorCopyFeedback = null;
        var copied = false;
        try { if (module is not null && !errorNativeUnavailable) copied = await module.InvokeAsync<bool>("copyDiagnosticDetails", text); }
        catch { /* Clipboard/module failure is local feedback, never another FailAsync. */ }
        if (disposed || !errorOpen || !ReferenceEquals(errorInstance, instance)) return;
        errorCopyPending = false;
        errorCopyFeedback = copied ? "Copied." : "Copy unavailable; select the details to copy.";
    }
    private async Task CloseError()
    {
        errorInstance = null; errorCopyPending = false; errorCopyFeedback = null; errorFallbackFocusPending = false;
        try
        {
            if (module is not null) await module.InvokeVoidAsync("closeError", errorDialog, AuditRoute);
            auditErrorFocusPending = AuditRoute;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); }
        // Keep local opening/focus blocked until native dismissal has finished.
        if (!disposed)
        {
            errorOpen = false;
            await DrainRoomRefreshAsync();
        }
    }

    private async Task ToggleSearchAsync()
    {
        if (!CanInteract) return;
        BeginShellObservation(advanced ? "shell.search.close.observed-render" : "shell.search.open.observed-render");
        using var timing = OperationTiming.Start(advanced ? "shell.search.close.handler" : "shell.search.open.handler", parent: PerformanceObservation);
        advanced = !advanced;
        if (!advanced)
        {
            var terms = SidebarTerms.Prepare(nameTerm, "", "");
            var visible = presentation!.Filter(searchIndex, terms, archive, displayGroups);
            locationTerm = requirementTerm = "";
            displayGroups = visible; filterTerms = terms;
        }
        await Task.CompletedTask;
        timing.End();
    }
    private void LocationChanged(object? sender, LocationChangedEventArgs args)
    {
        if (Runtime.Retired) return;
        var preparedArrival = ArriveRoomEntry(args);
        creationNavigationOwner = new();
        CancelRoomDeparture();
        PublishRoomWarning();
        if (!preparedArrival)
        {
            roomEntryObservation?.End(TimingOutcome.Retired);
            roomEntryObservation = null;
        }
        shellRenderObservation?.End(TimingOutcome.Retired);
        shellRenderObservation = null;
        // Retire before dispatch: away/back must not regain timing ownership while
        // this existing application callback is waiting on the renderer dispatcher.
        if (initialLoadTimingActive) initialLoadTimingRetired = true;
        _ = InvokeAsync(() =>
        {
            if (disposed) return;
            // Also cover an event dispatched before timing began whose queued
            // callback now retires the actual initial route owners during startup.
            if (initialLoadTimingActive) initialLoadTimingRetired = true;
            initialRequirements = null;
            initialLanding = null;
            initialRoom = null;
            var next = SelectedId(); lastSelected = next; view++;
            if (!disposed) StateHasChanged();
        });
    }
    private Guid? SelectedId()
    {
        var parts = Navigation.ToBaseRelativePath(Navigation.Uri).Split('?', '#')[0].Trim('/').Split('/');
        return (parts is ["rooms", var text] && Guid.TryParse(text, out var id)) ? id : AuditRoom(Navigation.ToBaseRelativePath(Navigation.Uri));
    }
    private void SelectRoom(Guid id) { if (!Locked) Navigation.NavigateTo($"/rooms/{id}"); }
    private void Home() { if (!Locked) Navigation.NavigateTo("/"); }
    private async Task ToggleArchiveAsync()
    {
        BeginShellObservation("shell.archive.observed-render");
        using var timing = OperationTiming.Start("shell.archive.handler", parent: PerformanceObservation);
        var visible = presentation!.Filter(searchIndex, filterTerms, !archive, displayGroups);
        archive = !archive; displayGroups = visible; await PersistAsync();
        timing.End(disposed ? TimingOutcome.Retired : TimingOutcome.Complete);
    }
    private async Task ToggleGroupAsync(Guid id)
    {
        if (!Searching)
        {
            BeginShellObservation("shell.group.observed-render");
            using var timing = OperationTiming.Start("shell.group.handler", parent: PerformanceObservation);
            if (!expanded.Add(id)) expanded.Remove(id); await PersistAsync();
            timing.End(disposed ? TimingOutcome.Retired : TimingOutcome.Complete);
        }
    }
    private async Task PersistAsync()
    {
        try { if (module is not null && CanInteract) await module.InvokeVoidAsync("savePreferences", new Preferences(archive, expanded.ToArray())); }
        catch (JSException exception) { Console.Error.WriteLine(exception); }
    }
    public sealed record Preferences(bool ShowArchivedRooms, Guid[] ExpandedGroupIds);
    private sealed class ReadFailure : Exception, IDiagnosticFailure
    {
        public string? Trace { get; }
        public DiagnosticSnapshot Diagnostic { get; }
        public ReadFailure(string message, string? trace, Exception? inner = null, int? status = null, string? code = null) : base(message, inner)
        {
            Trace = ErrorDiagnostics.Trace(trace);
            Diagnostic = inner is null ? ErrorDiagnostics.HttpFailure(GetType(), trace, status, code)
                : ErrorDiagnostics.Capture(inner, "feature", trace, status, code);
        }
    }

    private bool Searching => filterTerms.Searching;
    private string NameTerm { get => nameTerm; set => SetTerms(value, locationTerm, requirementTerm); }
    private string LocationTerm { get => locationTerm; set => SetTerms(nameTerm, value, requirementTerm); }
    private string RequirementTerm { get => requirementTerm; set => SetTerms(nameTerm, locationTerm, value); }
    private void SetTerms(string name, string location, string requirement)
    {
        if (name == nameTerm && location == locationTerm && requirement == requirementTerm) return;
        var terms = SidebarTerms.Prepare(name, location, requirement);
        var visible = presentation!.Filter(searchIndex, terms, archive, displayGroups);
        nameTerm = name; locationTerm = location; requirementTerm = requirement;
        displayGroups = visible; filterTerms = terms;
    }
    private ImmutableArray<Guid> ActiveIds(Guid? id) => presentation!.ByGroup[new(id)].ActiveIds;
    private Guid? SelectionFor(SidebarGroupKey key) => lastSelected is { } id
        && presentation!.ByRoom.TryGetValue(id, out var row) && new SidebarGroupKey(row.Room.GroupId) == key ? id : null;
    private string? PendingFor(SidebarGroupKey key)
    {
        if (pending == $"group:{key.Id}" || pending == $"create-room:{key.Id}" || key.Id is null && pending == "auto") return pending;
        return pending is not null && pending.StartsWith("room:") && Guid.TryParse(pending.AsSpan(5), out var id)
            && presentation!.ByRoom.TryGetValue(id, out var row) && new SidebarGroupKey(row.Room.GroupId) == key ? pending : null;
    }
    public static string? ImageReason(ImageState state) => state switch
    { ImageState.Disabled => "Scene image intentionally disabled", ImageState.NeedsRecapture => "Needs recapture", ImageState.Missing => "Scene image missing", ImageState.NonstandardScale => "Nonstandard image scale", _ => null };

    private static void Validate(Sidebar value)
    {
        if (value is null || value.Groups is null || value.Rooms is null || value.Groups.Any(x => x is null || x.Id == Guid.Empty || x.FriendlyName is null)
            || value.Rooms.Any(x => x is null || x.Id == Guid.Empty || x.FriendlyName is null || !Enum.IsDefined(x.Status) || !Enum.IsDefined(x.ImageState))
            || value.Groups.Select(x => x.Id).Distinct().Count() != value.Groups.Count || value.Rooms.Select(x => x.Id).Distinct().Count() != value.Rooms.Count) throw new JsonException("Unusable sidebar.");
        var groupIds = value.Groups.Select(x => x.Id).ToHashSet();
        if (value.Rooms.Any(x => x.GroupId is { } id && !groupIds.Contains(id))) throw new JsonException("Unusable sidebar.");
    }
    private static void Validate(GroupEditor value)
    {
        if (value.Group is null || value.Group.Id == Guid.Empty || value.Group.FriendlyName is null || value.Group.UpdatedUtc.Kind != DateTimeKind.Utc
            || value.ZoneIds is null || value.ZoneIds.Any(x => x is null)) throw new JsonException("Unusable editor.");
    }
    private static void Validate(SearchData value)
    {
        if (value.Locations is null || value.Requirements is null || value.Requirements.Transitions is null || value.Requirements.Connections is null || value.Requirements.Checks is null
            || value.Locations.Any(x => x is null || x.RoomId == Guid.Empty || x.FriendlyName is null)
            || value.Requirements.Transitions.Concat(value.Requirements.Connections).Concat(value.Requirements.Checks).Any(x => x is null || x.RoomId == Guid.Empty || x.Requirements is null)) throw new JsonException("Unusable search data.");
    }
    private async Task ReleaseOwnersAsync()
    {
        var owners = new[] { browserOwner, dragOwner };
        browserOwner = dragOwner = null;
        foreach (var owner in owners)
        {
            if (owner is null) continue;
            try { await owner.InvokeVoidAsync("dispose"); }
            catch (JSException exception) { Console.Error.WriteLine(exception); }
            finally { await owner.DisposeAsync(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Runtime.Changed -= RuntimeChanged;
        Runtime.ObserveUpdateRisk -= CaptureClientUpdateRisk;
        Access.Changed -= AccessChanged;
        roomEntryObservation?.End(TimingOutcome.Retired);
        roomEntryObservation = null;
        shellRenderObservation?.End(TimingOutcome.Retired);
        shellRenderObservation = null;
        navigationAdmission?.Dispose();
        RetireRoomEntryNavigation();
        CancelRoomDeparture();
        disposed = true; refreshQueued = refreshForceQueued = false; editorSequence++;
        ReleaseBootstrapLoading();
        RetireCacheManagement();
        PublishRoomWarning();
        initialLanding = null; landingOwner = null;
        Catalogue.Changed -= CatalogueChanged;
        Catalogue.LocalSettled -= CatalogueLocalSettled;
        Catalogue.BackgroundFailed -= BackgroundCacheFailed;
        Browser.Failed -= BackgroundCacheFailed;
        Catalogue.Dispose();
        Navigation.LocationChanged -= LocationChanged;
        await lifetime.CancelAsync();
        await Browser.DisposeAsync();
        await ReleaseOwnersAsync();
        callback?.Dispose();
        if (dragModule is not null) await dragModule.DisposeAsync();
        if (module is not null) await module.DisposeAsync();
        if (updateTransport is not null) await updateTransport.DisposeAsync();
        lifetime.Dispose();
    }
}
