using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Client.RoomEditing;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    [Inject] public ClientRuntime Runtime { get; set; } = default!;
    [Parameter, EditorRequired] public RoomVisitEntry Entry { get; set; } = null!;
    // Only admitted authoring/recovery paths consume genuine tracker evidence.
    private RoomOpenResponse Opened => workspace.Admission ?? throw new InvalidOperationException("Room admission is pending.");
    [CascadingParameter] public App Shell { get; set; } = null!;
    private RoomWorkspace workspace = null!;
    private ElementReference root, reviewDialog;
    private IJSInProcessObjectReference? nativeOwner;
    private RoomTables? tables;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed, draining, controlPending, showArchived;
    private bool browserReady;
    private bool browserSetupActive;
    private Task? drainFlight;
    private (Guid Evidence, long Revision)? displayedReview;
    private RoomResolveRequest? resolutionRequest;
    private DotNetObjectReference<RoomWorkspaceEditor>? nativeReference;
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
    private bool editingComments, focusComments, showMapContext = true;
    private string? commentsInitial;
    private (string Id, long Interaction)? pendingBoundaryFocus;
    private readonly Dictionary<Guid, string> operationInitiators = [];
    private readonly Dictionary<Guid, string> operationFallbackInitiators = [];
    private readonly Dictionary<Guid, (string Id, long Interaction)> operationReturnFocus = [];
    private (string Primary, string Fallback, long Interaction)? returnFocusFallback;
    private bool Archived => workspace.Confirmed.Document?.Header.Observation.IsArchived == true;
    private RoomStatus AppliedStatus => Catalogue.Sidebar.Rooms.SingleOrDefault(x => x.Id == Entry.RoomId)?.Status ?? RoomStatus.Neutral;
    private string StatusIcon => AppliedStatus switch { RoomStatus.Success => "fa-circle-check", RoomStatus.Warning => "fa-triangle-exclamation", RoomStatus.Danger => "fa-circle-exclamation", _ => "fa-circle-minus" };
    private string StatusClass => AppliedStatus switch { RoomStatus.Success => "success", RoomStatus.Warning => "warning", RoomStatus.Danger => "danger", _ => "secondary" };
    private string StatusLabel => $"Room status: {AppliedStatus}";
    private bool Attention => workspace.IsAdmitted && (roomResponseObservationFailed || workspace.AcceptanceFailed || keyboardUnsentFailure || workspace.Queue.Blocked is not null || workspace.Queue.ProjectionPending || workspace.Queue.RecoveryRequired || workspace.Queue.TrackingUnavailable);
    private string? SaveIcon => Attention ? "fa-triangle-exclamation" : draining || controlPending || workspace.AdmittedQueue?.HasPendingIntent == true ? "fa-spinner fa-spin" : null;
    private string SaveLabel => !workspace.IsAdmitted ? "No room changes submitted" : workspace.Queue.ProjectionPending ? "Saved; authoritative refresh failed" : workspace.Queue.TrackingUnavailable || workspace.Queue.RecoveryRequired ? "Room changes need attention" : keyboardUnsentFailure ? "Movement was not saved. Retry continues the original operation." : Attention ? "Room changes need attention" : draining || controlPending || workspace.Queue.HasPendingIntent ? "Saving room changes" : "Saved";
    private bool DepartureBlocked => !Current || !Shell.CanUseRoom || Shell.RoomSwitchPending;
    private bool Current => !disposed && Shell.CanInteract && !presentationRetired && ReferenceEquals(workspace.Entry, Entry) && App.RoomRoute(Navigation.ToBaseRelativePath(Navigation.Uri)) == Entry.RoomId;
    // The keyed parent retains this confirmed presentation through destination loading.
    private string DocumentTitle => !externalRoomDeleted && workspace.Confirmed.Document is { } document
        ? $"{(string.IsNullOrWhiteSpace(document.Header.Values.FriendlyName) ? "Unnamed room" : document.Header.Values.FriendlyName)} | Silksong Logic Manager"
        : "Room | Silksong Logic Manager";
    private bool InspectionBlocked => !Current || !EntryControlsReady || workspace.AuthoringBlocked || !Shell.CanUseRoom || localModal is not null || guide is not null || guideLoading || imageDraft is not null || imageBusy || roomLinksOpen || roomLinksOpening || workspace.Queue.PendingIntent.Any(x => x.Operation is DeleteRoom);
    private bool AuthoringBlocked => !Shell.CanAuthor || InspectionBlocked;
    private static IReadOnlyList<CheckLocationTypeDefinition> CheckTypes => CheckLocationTypeCatalogue.Definitions;
    private IReadOnlyList<string> RoomReferences
    {
        get { ObserveShellOperation(); using var work = observedRoomWork.Measure(ObservedWork.RoomReferences); return Catalogue.Rooms.Values.Where(x => !x.Header.Observation.IsArchived).Select(x => x.Header.Values.ReferenceId).Distinct().Order().ToArray(); }
    }
    private IReadOnlyList<string> SubroomReferences
    {
        get { ObserveShellOperation(); using var work = observedRoomWork.Measure(ObservedWork.SubroomReferences); return workspace.Subrooms.Where(x => !x.IsArchived && x.EntityId is not null).Select(x => x.Values.ReferenceId).Distinct().Order().ToArray(); }
    }
    private string HeaderDiagnostic(string field)
    {
        ObserveShellOperation(); using var work = observedRoomWork.Measure(ObservedWork.Header);
        var values = Catalogue.Rooms.Values.Where(x => !x.Header.Observation.IsArchived && x.Observation.RoomId != Entry.RoomId).Select(x => x.Header.Values);
        var severity = field switch
        {
            "name" => RoomPresentationRules.Required(workspace.Header.FriendlyName),
            "reference" => RoomPresentationRules.Duplicate(workspace.Header.ReferenceId, values.Select(x => (string?)x.ReferenceId).Concat(Archived ? [] : [workspace.Header.ReferenceId])),
            "game" => RoomPresentationRules.Text(workspace.Header.InGameId) ? RoomPresentationRules.Duplicate(workspace.Header.InGameId, values.Select(x => x.InGameId).Concat(Archived ? [] : [workspace.Header.InGameId])) : Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts.V2Severity.Neutral,
            _ => throw new InvalidOperationException()
        };
        return severity switch { Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts.V2Severity.Warning => "warning", Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts.V2Severity.Danger => "danger", _ => "" };
    }
    protected override void OnInitialized()
    {
        readinessTiming = Shell.TakeRoomEntryTiming(EntryStarted);
        workspace = new(Entry);
        workspace.AcceptanceObserved += ObservePointerAcceptancePause;
        workspace.AcceptanceObserved += NotifyRoomActions;
        if (workspace.AdmittedQueue is { } queue) queue.IntentAppended += ObserveAppendedRoomIntent;
        InstallRoomMap(MapContext);
        Catalogue.Changed += LocalRoomMapChanged;
        Shell.RegisterRoomWorkspace(this, WorkspaceBlocksDeparture, ReconcileFreshnessAsync, RoomQueueActionState);
        Shell.RegisterClientUpdateRisk(this, ClientUpdateWorkAtRisk);
    }
    protected override void OnParametersSet()
    {
        if (workspace is not null && !ReferenceEquals(workspace.Entry, Entry))
            throw new InvalidOperationException("A room workspace must be keyed by its mounted visit.");
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!Current) return;
        // Capture at entry: setup below only requests the enabled DOM rerender.
        var enabledRender = EntryControlsReady && !Shell.GeneralErrorOpen;
        EnsureEntryRequests();
        try
        {
            // Complete same-render DOM identity transfers before an awaited modal
            // or preference operation can admit another browser interaction.
            if (nativeOwner is not null)
            {
                using var work = observedRoomWork.Measure(ObservedWork.NativeSnapshot);
                workspace.Observe(nativeOwner.Invoke<RoomNativeField[]>("snapshotRoomNative").ToImmutableArray());
                if (workspace.IsAdmitted) ReconcileKeyboardOwnership();
            }
            InstallParserContext();
            if (switchSetupFailed && !Shell.GeneralErrorOpen) { switchSetupFailed = false; StateHasChanged(); }
            if (!browserReady && !browserSetupActive && !Shell.GeneralErrorOpen)
            {
                browserSetupActive = true;
                using var setupTiming = OperationTiming.Start("room.browser.initial-setup", parent: readinessTiming?.ActiveId ?? 0);
                try
                {
                if (nativeOwner is null)
                {
                    var reference = await JS.InvokeAsync<IJSObjectReference>("createV2ChildInteractionOwner", root, null);
                    if (!Current) { try { await reference.InvokeVoidAsync("dispose"); } finally { await reference.DisposeAsync(); } return; }
                    nativeOwner = reference as IJSInProcessObjectReference ?? throw new InvalidOperationException("The browser room editor requires in-process native installation.");
                }
                nativeOwner.Invoke<RoomNativeField[]>("enableRoomProjection");
                nativeReference ??= DotNetObjectReference.Create(this);
                nativeOwner.Invoke<RoomNativeField[]>("registerRoomHeader", nativeReference);
                (tables ?? throw new InvalidOperationException("The complete room tables are not mounted.")).RegisterNative(nativeOwner);
                await RegisterFollowAsync();
                if (!Current) return;
                showMapContext = await JS.InvokeAsync<bool>("loadRoomMapContextVisibility");
                if (!Current) return;
                InstallParserContext();
                SettleInitialReadinessContext();
                browserReady = true;
                StateHasChanged();
                setupTiming.End();
                }
                finally { browserSetupActive = false; }
            }
            RestoreHistoryReceiverLossFocus();
            if (focusComments && nativeOwner is not null) { focusComments = false; nativeOwner.InvokeVoid("focus", "room-comments-editor"); }
            if (workspace.IsAdmitted && pendingBoundaryFocus is { } focus && nativeOwner is not null && !Shell.GeneralErrorOpen)
            {
                pendingBoundaryFocus = null;
                if (historyOpening is null && localModal is null && guide is null && workspace.Queue.Blocked is null && !workspace.Queue.TrackingUnavailable)
                {
                    var focused = nativeOwner.Invoke<bool>("focusRoomBoundary", focus.Id, focus.Interaction);
                    if (!focused && returnFocusFallback is { } fallback && fallback.Primary == focus.Id && fallback.Interaction == focus.Interaction)
                        nativeOwner.Invoke<bool>("focusRoomBoundary", fallback.Fallback, focus.Interaction);
                }
                returnFocusFallback = null;
            }
            if (guideFocusPending && guide is not null && !Shell.GeneralErrorOpen)
            {
                if (Shell.FocusRoomDialog("v3-room-syntax-guide")) guideFocusPending = false;
            }
            if (localModal is not null && localModalPending && !Shell.GeneralErrorOpen)
            {
                if (Shell.FocusRoomDialog("v3-room-local-dialog")) localModalPending = false;
            }
            FocusHistory();
            if (imageDraft is not null && imageFocusPending && !Shell.GeneralErrorOpen)
            { if (Shell.FocusRoomDialog("v3-room-image-dialog")) imageFocusPending = false; }
            if (imageReleasePending && !draining && !controlPending) await ReleaseImageAsync();
            if (!Current) return;
            await ReconcileMapHeightAsync();
            if (!Current) return;
            if (EntryControlsReady && !enabledRender && !Shell.GeneralErrorOpen) StateHasChanged();
            var blocked = workspace.AdmittedQueue?.Blocked;
            var review = workspace.AdmittedQueue?.TrackingUnavailable == true || entryAdmissionUnavailable ? (Guid.Empty, 0L) : blocked is null || ImageCorrection ? ((Guid, long)?)null : (blocked.EvidenceId, blocked.Revision);
            if (review is { } owner && displayedReview != owner && !Shell.GeneralErrorOpen)
            {
                if (Shell.FocusRoomDialog("v3-room-review-dialog") && Current) displayedReview = owner;
            }
            if (enabledRender && Current)
            {
                initialEnabledRenderCompleted = true;
                if (TryCompleteInitialReadiness()) StateHasChanged();
            }
            if (workspace.IsAdmitted && !entryCheckFailed) await ReconcileFreshnessAsync();
        }
        catch (Exception exception) { readinessTiming?.End(TimingOutcome.FailedOrRetired); initialReadinessFailed = true; if (Current) { FailSwitchSetup(); StateHasChanged(); await Shell.ReportRoomErrorAsync(exception, "The room editor could not update its browser controls. Pending work has been retained."); } }
        finally { ReportObservedRoomWork(); }
    }
    [JSInvokable]
    public Task<bool> DispatchRoomHeaderBoundary(string field, RoomNativeField[] values)
    {
        if (AuthoringBlocked || field is not ("name" or "reference" or "game" or "contributors" or "comments")) return Task.FromResult(false);
        try
        {
            if (!TransferKeyboardBeforeAction()) return Task.FromResult(false);
            var previousRows = workspace.Subrooms;
            var operation = workspace.AcceptHeader(values.ToImmutableArray());
            if (operation is not null) operationInitiators[operation.OperationId] = field == "name" ? "room-friendly-name" : $"room-{field}-editor";
            var closeComments = field == "comments" && editingComments;
            if (field == "comments") editingComments = false;
            if (!ReferenceEquals(previousRows, workspace.Subrooms) || closeComments) StateHasChanged();
            ContinueBoundaryWork(); return Task.FromResult(true);
        }
        catch (Exception exception) { return BoundaryFailureAsync(exception); }
    }
    private void BeginComments()
    {
        if (AuthoringBlocked || editingComments) return;
        commentsInitial = workspace.Header.Comments; editingComments = focusComments = true;
    }
    private async Task ToggleMapContext()
    {
        if (!Current || !Shell.CanInteract) return;
        if (showMapContext && workspace.IsAdmitted && !TransferKeyboardBeforeAction()) return;
        showMapContext = !showMapContext;
        await JS.InvokeVoidAsync("saveRoomMapContextVisibility", showMapContext);
    }
    private void Depart(string target) { if (!DepartureBlocked) Navigation.NavigateTo(target); }
    private Task RecheckAsync()
    {
        if (AuthoringBlocked || Archived) return Task.CompletedTask;
        if (!TransferKeyboardBeforeAction()) return Task.CompletedTask;
        workspace.Enqueue(new RecheckRoomRequirements()); return DrainAsync();
    }
    private Task ArchiveAsync(bool archived)
    {
        if (AuthoringBlocked) return Task.CompletedTask;
        if (!TransferKeyboardBeforeAction()) return Task.CompletedTask;
        workspace.Enqueue(new SetRoomArchive(Archived, archived)); return DrainAsync();
    }
    private Task<bool> BoundaryAsync(RoomTableBoundaryIntent boundary)
    {
        if (AuthoringBlocked || nativeOwner is null) return Task.FromResult(false);
        try
        {
            if (!TransferKeyboardBeforeAction()) return Task.FromResult(false);
            var previousRows = workspace.Subrooms;
            var previousFeedback = workspace.BoundaryFeedback;
            workspace.Observe(boundary.Fields);
            var kind = boundary.Kind switch { RoomTableKind.Subroom => "subroom", RoomTableKind.Transition => "transition", RoomTableKind.Connection => "connection", RoomTableKind.Check => "check", _ => throw new InvalidOperationException() };
            if (boundary is RoomDropBoundaryIntent drop)
            {
                var row = workspace.Find(kind, drop.ClientId);
                if (row.IsArchived || row.EntityId is null || row.Attention) return Task.FromResult(false);
                var id = row.EntityId!.Value;
                var original = workspace.Placement(row);
                if (drop.Section != original.Section || drop.TargetIndex > original.Membership.Length) return Task.FromResult(false);
                var target = drop.TargetIndex;
                workspace.Enqueue(row switch
                {
                    RoomSubroomRow => new ReorderRoomSubroom(id, original, target), RoomTransitionRow => new ReorderRoomTransition(id, original, target),
                    RoomConnectionRow => new ReorderRoomConnection(id, original, target), RoomCheckRow r => new ReorderRoomCheck(id, original, r.Enemies, target),
                    _ => throw new InvalidOperationException()
                });
            }
            else if (boundary is RoomRowBoundaryIntent edit)
            {
                // Native control blur is deliberately a no-op; change alone owns
                // select/checkbox saves. Text blur/Arrow freezes the complete row.
                if (edit.Trigger == RoomNativeTrigger.ControlBlur) return Task.FromResult(true);
                var operation = workspace.AcceptRow(kind, edit.ClientId, boundary.Fields, nativeOwner);
                if (operation is not null)
                {
                    var field = edit.Column switch
                    {
                        RoomNativeColumn.Name => "name", RoomNativeColumn.Reference => "reference", RoomNativeColumn.Notes => "notes", RoomNativeColumn.Alias => "alias",
                        RoomNativeColumn.Source => "source", RoomNativeColumn.DestinationRoom => "destination-room", RoomNativeColumn.DestinationAlias => "destination-alias",
                        RoomNativeColumn.UnsafeArrival => "unsafe-arrival", RoomNativeColumn.Requirements => "requirements", RoomNativeColumn.Todo => "todo", RoomNativeColumn.Verification => "verification",
                        RoomNativeColumn.Destination => "destination", RoomNativeColumn.Type => "type", RoomNativeColumn.Subroom => "subroom", _ => throw new InvalidOperationException()
                    };
                    operationInitiators[operation.OperationId] = edit.FocusTarget ?? $"editor-row-{edit.ClientId}-{field}";
                    if (edit.FocusTarget is not null) operationFallbackInitiators[operation.OperationId] = $"editor-row-{edit.ClientId}-{field}";
                }
                if (!workspace.LastBoundaryAccepted) { StateHasChanged(); return Task.FromResult(false); }
                if (edit.Direction == RoomNativeDirection.Down) workspace.AdvanceEmptyTail(kind, edit.ClientId);
                if (edit.Trigger == RoomNativeTrigger.BoundaryArrow && edit.FocusTarget is { } focusTarget) pendingBoundaryFocus = (focusTarget, edit.Interaction);
            }
            else return Task.FromResult(false);
            if (!ReferenceEquals(previousRows, workspace.Subrooms) || previousFeedback != workspace.BoundaryFeedback || pendingBoundaryFocus is not null) StateHasChanged();
            ContinueBoundaryWork();
            return Task.FromResult(true);
        }
        catch (Exception exception) { return BoundaryFailureAsync(exception); }
    }
    private void ContinueBoundaryWork()
    {
        // An unchanged boundary must still allow earlier queue/explicit-refresh
        // obligations to continue, without manufacturing an empty save/render cycle.
        if (workspace.Queue.HasPendingIntent) _ = DrainAsync();
        else _ = Shell.DrainRoomRefreshAsync();
    }
    private async Task<bool> BoundaryFailureAsync(Exception exception)
    {
        await Shell.ReportRoomErrorAsync(exception, "The room boundary could not be accepted. Native text and pending work have been retained.");
        return false;
    }
    private Task DrainAsync() => draining ? drainFlight ?? Task.CompletedTask : drainFlight = DrainCoreAsync();
    private async Task DrainCoreAsync()
    {
        if (!Shell.CanAuthor || !Current || !workspace.IsAdmitted || draining || controlPending || roomResponseObservationFailed || workspace.AcceptanceFailed || nativeOwner is null || !Shell.CanUseRoom) return;
        using var timing = OperationTiming.Start("save.drain");
        draining = true;
        string? historyWarning = null;
        try
        {
            while (Shell.CanAuthor && Current && !Shell.GeneralErrorOpen && !workspace.Queue.RecoveryRequired && !workspace.AuthoringBlocked)
            {
                RoomBatchRequest? batch;
                using (var prepareTiming = OperationTiming.Start("save.batch.prepare"))
                { batch = workspace.Queue.PrepareBatch(); prepareTiming.End(batch is null ? TimingOutcome.Skipped : TimingOutcome.Complete); }
                if (batch is null) break;
                StateHasChanged();
                using var admissionTiming = OperationTiming.Start("save.batch.admission");
                await Catalogue.MutateAsync(async admission =>
                {
                    admissionTiming.End();
                    if (!Current || !Shell.CanAuthor) { workspace.Queue.CancelBeforeSend(batch.RequestId); return; }
                    var source = admission.CaptureReadContext();
                    var response = await admission.SubmitRoomBatchAsync(workspace.Queue.Identity, batch, () =>
                    {
                        workspace.Queue.MarkBatchSent(batch.RequestId);
                        RetainReleasedRoomUncertainty();
                        ObservePointerDropDispatch();
                        RetainBatchSource(source);
                    });
                    try
                    {
                        if (!Current) return;
                        IncorporateRoomSaveResponse(response, admission, warning => historyWarning ??= warning);
                        StateHasChanged();
                        await AcknowledgeAsync(admission, warning => historyWarning ??= warning);
                    }
                    finally { PublishReceivedRoomCache(response.Received, admission); }
                }, lifetime.Token);
            }
            timing.End(QueueTimingOutcome);
        }
        catch (ConfirmedCatalogue.RoomTrackingUnavailableException) { timing.End(TimingOutcome.Uncertain); if (Current) { workspace.Queue.LoseTracking(); ObservePointerDropFailure(); ObserveRoomRequestFailure(); ObserveKeyboardFailure(); StateHasChanged(); } }
        catch (Exception exception)
        {
            workspace.Queue.RequestFailed();
            ObservePointerDropFailure();
            ObserveRoomRequestFailure();
            ObserveKeyboardFailure();
            timing.End(QueueTimingOutcome == TimingOutcome.Complete ? TimingOutcome.FailedOrRetired : QueueTimingOutcome);
            if (Current) await Shell.ReportRoomErrorAsync(exception, PointerRoomFailureMessage(workspace.Queue.ProjectionPending
                ? "Saved; authoritative refresh failed. Retry reads the retained result and does not repeat the write."
                : "The room request could not be completed. Pending work has been retained; Retry recovers progress before any further write."));
        }
        finally
        {
            try { draining = false; if (Current) { StateHasChanged(); await Shell.DrainRoomRefreshAsync(); } }
            finally { if (Current) await Shell.ReportHistoryWarningAsync(historyWarning); }
        }
        NavigateAfterRoomDelete();
    }
    private async Task AcknowledgeAsync(ConfirmedCatalogue.MutationAdmission admission, Action<string> retainHistoryWarning)
    {
        if (!Current || nativeOwner is null || workspace.Queue.IncorporatedThrough == workspace.Queue.AcknowledgedThrough) return;
        using var timing = OperationTiming.Start("save.acknowledgement");
        workspace.Queue.BeginControlRequest();
        var response = await admission.AcknowledgeRoomAsync(workspace.Queue.Identity, new(new(workspace.Queue.Identity.TabId, workspace.Queue.Identity.VisitId), workspace.Queue.Claim()));
        try { if (Current) IncorporateRoomResponse(response, admission, retainHistoryWarning); }
        finally { PublishReceivedRoomCache(response, admission); }
        timing.End(Current ? TimingOutcome.Acknowledged : TimingOutcome.Retired);
    }
    private async Task RetryAsync()
    {
        if (!Current || !workspace.IsAdmitted || draining || controlPending || nativeOwner is null || workspace.Queue.TrackingUnavailable || !Shell.CanUseRoom) return;
        using var timing = OperationTiming.Start("save.read-only-recovery");
        controlPending = true;
        string? historyWarning = null;
        try
        {
            workspace.Queue.BeginControlRequest();
            using var admissionTiming = OperationTiming.Start("save.recovery.admission");
            await Catalogue.MutateAsync(async admission =>
            {
                admissionTiming.End();
                // A lost-before-admission attempt is not an unknown tracker.
                RetainProjectionReadSources(admission);
                var response = await admission.RecoverRoomAsync(workspace.Queue.Identity, workspace.Queue.IncorporatedThrough);
                ReceivedCacheResult<RoomTrackingResponse>? resolved = null;
                try
                {
                    if (!Current) return;
                    IncorporateRoomResponse(response, admission, warning => historyWarning ??= warning);
                    keyboardUnsentFailure = false;
                    PublishKeyboardStates();
                    if (!workspace.Queue.ProjectionPending && resolutionRequest is { } request
                        && workspace.Queue.Blocked is { } head && head.Sequence == request.Sequence
                        && head.OperationId == request.OperationId && head.EvidenceId == request.EvidenceId
                        && head.Revision == request.EvidenceRevision)
                    {
                        // Reuse the entire immutable attempt only against its reviewed
                        // head. The tracker serializes/deduplicates a late original POST.
                        workspace.Queue.BeginControlRequest();
                        RetainResolutionSource(request, admission);
                        var saved = await admission.ResolveRoomHeadAsync(workspace.Queue.Identity, request, () =>
                        {
                            ObserveResolutionDispatch(request);
                            ObservePointerDropDispatch(request);
                        });
                        resolved = saved.Received;
                        if (!Current) return;
                        IncorporateRoomSaveResponse(saved, admission, warning => historyWarning ??= warning);
                    }
                    resolutionRequest = null;
                    await AcknowledgeAsync(admission, warning => historyWarning ??= warning);
                    if (workspace.Queue.Blocked is null && displayedReview is not null)
                    { displayedReview = null; }
                }
                finally
                {
                    PublishReceivedRoomCache(response, admission);
                    if (resolved is not null) PublishReceivedRoomCache(resolved, admission);
                }
            }, lifetime.Token);
            timing.End(QueueTimingOutcome);
        }
        catch (ConfirmedCatalogue.RoomTrackingUnavailableException) { timing.End(TimingOutcome.Uncertain); if (Current) { workspace.Queue.LoseTracking(); ObservePointerDropFailure(); ObserveRoomRequestFailure(); ObserveKeyboardFailure(); } }
        catch (Exception exception) { workspace.Queue.RequestFailed(); ObservePointerDropFailure(); ObserveRoomRequestFailure(); ObserveKeyboardFailure(); timing.End(QueueTimingOutcome == TimingOutcome.Complete ? TimingOutcome.FailedOrRetired : QueueTimingOutcome); if (Current) await Shell.ReportRoomErrorAsync(exception, PointerRoomFailureMessage("Room progress could not be recovered. No write was repeated.")); }
        finally
        {
            try { controlPending = false; if (Current) { StateHasChanged(); await Shell.DrainRoomRefreshAsync(); } }
            finally { if (Current) await Shell.ReportHistoryWarningAsync(historyWarning); }
        }
        if (Current && !workspace.Queue.RecoveryRequired) await DrainAsync();
    }
    private async Task ResolveAsync(RoomHeadResolution choice)
    {
        if (!Shell.CanAuthor || !Current || !workspace.IsAdmitted || draining || controlPending || workspace.Queue.RecoveryRequired || nativeOwner is null || !Shell.CanUseRoom || workspace.Queue.Blocked is not { } head) return;
        using var timing = OperationTiming.Start("save.resolution");
        var attempt = Guid.NewGuid(); controlPending = true;
        string? historyWarning = null;
        if (operationInitiators.TryGetValue(head.OperationId, out var initiator))
            operationReturnFocus[head.OperationId] = (initiator, nativeOwner.Invoke<long>("roomInteractionGeneration"));
        var identity = workspace.Queue.Identity;
        var request = new RoomResolveRequest(new(identity.TabId, identity.VisitId), attempt, head.Sequence, head.OperationId, head.EvidenceId, head.Revision,
            choice, workspace.Queue.Confirmed.Document?.Observation ?? throw new InvalidOperationException());
        resolutionRequest = request;
        try
        {
            workspace.Queue.BeginControlRequest();
            using var admissionTiming = OperationTiming.Start("save.resolution.admission");
            await Catalogue.MutateAsync(async admission =>
            {
                admissionTiming.End();
                RetainResolutionSource(request, admission);
                var response = await admission.ResolveRoomHeadAsync(identity, request, () =>
                {
                    ObserveResolutionDispatch(request);
                    ObservePointerDropDispatch(request);
                });
                try
                {
                    if (!Current) return;
                    IncorporateRoomSaveResponse(response, admission, warning => historyWarning ??= warning);
                    resolutionRequest = null;
                    await AcknowledgeAsync(admission, warning => historyWarning ??= warning);
                    if (workspace.Queue.Blocked is null) displayedReview = null;
                }
                finally { PublishReceivedRoomCache(response.Received, admission); }
            }, lifetime.Token);
            timing.End(QueueTimingOutcome);
        }
        catch (ConfirmedCatalogue.RoomTrackingUnavailableException) { timing.End(TimingOutcome.Uncertain); if (Current) { workspace.Queue.LoseTracking(); ObservePointerDropFailure(); ObserveRoomRequestFailure(); ObserveKeyboardFailure(); } }
        catch (Exception exception) { workspace.Queue.RequestFailed(); ObservePointerDropFailure(); ObserveRoomRequestFailure(); ObserveKeyboardFailure(); timing.End(QueueTimingOutcome == TimingOutcome.Complete ? TimingOutcome.FailedOrRetired : QueueTimingOutcome); if (Current) await Shell.ReportRoomErrorAsync(exception, PointerRoomFailureMessage("The reviewed room choice could not be confirmed. Retry recovers progress and reuses the exact same attempt only if still required.")); }
        finally
        {
            try
            {
                controlPending = false;
                if (Current) { StateHasChanged(); await Shell.DrainRoomRefreshAsync(); }
            }
            finally { if (Current) await Shell.ReportHistoryWarningAsync(historyWarning); }
        }
        if (Current && !workspace.Queue.RecoveryRequired) await DrainAsync();
    }
    private Task DismissProposalAsync() => workspace.Queue.Blocked?.Disposition == RoomBlockedDisposition.ProposalAwaitingChoice && !controlPending
         ? ResolveAsync(new ChooseRoomProposal(RoomProposalChoice.Revert)) : Task.CompletedTask;
    private void IncorporateRoomSaveResponse(ConfirmedCatalogue.RoomSaveResponse saved, ConfirmedCatalogue.MutationAdmission admission,
        Action<string> retainHistoryWarning)
    {
        if (!Current) return;
        workspace.Queue.RecordCompletedSave(saved.Received.Result, saved.ElapsedMilliseconds);
        IncorporateRoomResponse(saved.Received, admission, retainHistoryWarning);
    }
    private Task ReviewKeyDownAsync(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs args) => args.Key == "Escape" ? DismissProposalAsync() : Task.CompletedTask;
    private async Task RowActionAsync(RoomRowActionIntent intent)
    {
        try { await RowActionCoreAsync(intent); }
        catch (Exception exception) { if (Current) await Shell.ReportRoomErrorAsync(exception, "The room action could not be accepted. Existing values and pending work have been retained."); }
    }
    private Task RowActionCoreAsync(RoomRowActionIntent intent)
    {
        if (intent.Action == RoomRowAction.History) return OpenHistoryAsync(intent);
        // Durable lifecycle buttons use the same room queue as native boundaries.
        if (intent.Action == RoomRowAction.FollowInverse ? InspectionBlocked || nativeOwner is null : AuthoringBlocked) return Task.CompletedTask;
        if (intent.Action is not (RoomRowAction.Annotation or RoomRowAction.FollowInverse) && !TransferKeyboardBeforeAction()) return Task.CompletedTask;
        var row = CurrentPresentationRow(intent.Section switch { RoomOrderSection.Subrooms => "subroom", RoomOrderSection.Transitions => "transition", RoomOrderSection.Connections => "connection", _ => "check" }, intent.ClientId);
        if (row.EntityId is null || row.Attention) return Task.CompletedTask;
        if (intent.Action == RoomRowAction.FollowInverse && (intent.Section != RoomOrderSection.Transitions
            || row is not RoomTransitionRow { IsArchived: false, CanPrepareInverse: false, InverseRoomId: not null })) return Task.CompletedTask;
        if (row.IsArchived && intent.Action is not (RoomRowAction.Restore or RoomRowAction.Delete)
            || !row.IsArchived && intent.Action is RoomRowAction.Restore or RoomRowAction.Delete
            || intent.Action == RoomRowAction.Inverse && (row switch
            { RoomTransitionRow r => !r.CanPrepareInverse, RoomConnectionRow r => !r.CanScaffoldInverse, _ => true }))
        { workspace.SetBoundaryFeedback("This row action is no longer available for the current receiver."); StateHasChanged(); return Task.CompletedTask; }
        if (intent.Action == RoomRowAction.Delete && row.LastConfirmed is not { IsArchived: true })
        { workspace.SetBoundaryFeedback("Permanent deletion requires a confirmed archived receiver. Other valid row actions remain available."); StateHasChanged(); return Task.CompletedTask; }
        if (intent.Action is RoomRowAction.Archive or RoomRowAction.Restore)
        {
            var archived = intent.Action == RoomRowAction.Archive;
            var id = row.EntityId ?? throw new InvalidOperationException();
            workspace.Enqueue(row switch
            {
                RoomSubroomRow => new SetRoomSubroomArchive(id, row.IsArchived, archived), RoomTransitionRow => new SetRoomTransitionArchive(id, row.IsArchived, archived),
                RoomConnectionRow => new SetRoomConnectionArchive(id, row.IsArchived, archived), RoomCheckRow => new SetRoomCheckArchive(id, row.IsArchived, archived), _ => throw new InvalidOperationException()
            });
            StateHasChanged(); return DrainAsync();
        }
        return OpenRowActionAsync(row, intent.Action);
    }
    private string OperationAccount
    {
        get
        {
            var text = new StringBuilder(workspace.AdmittedQueue?.CopyableOperationAccount() ?? "Room admission has not completed; no operations have been queued."); text.AppendLine("Live native fields and tails (not submitted as operations):");
            text.AppendLine("Retained keyboard geometry captures/attempts:"); text.AppendLine(KeyboardAccount);
            if (workspace.IsAdmitted && nativeOwner is not null) text.AppendLine(JsonSerializer.Serialize(NativeKeyboardSnapshot()));
             if (nativeOwner is not null) text.AppendLine(JsonSerializer.Serialize(nativeOwner.Invoke<RoomNativeField[]>("snapshotRoomNative")));
              if (imageDraft is not null) { text.AppendLine("Retained image dialog numeric/source draft:"); text.AppendLine(JsonSerializer.Serialize(imageDraft)); }
             if (localModal is not null) { text.AppendLine("Retained local action/coordinate draft:"); text.AppendLine(localModal.Name); text.AppendLine(JsonSerializer.Serialize(coordinateDraft)); }
            return text.ToString();
        }
    }
    private Task RefreshDocument() => Current ? Shell.RefreshRoomDocumentAsync(this) : Task.CompletedTask;
    private void NavigateAfterRoomDelete()
    {
        if (Current && workspace.Queue.Confirmed.Document is null && RoomQueueActionState() is { Unfinished: false, Failed: false } && !Shell.HasRequestedRoomAction(this))
            Navigation.NavigateTo("/");
    }
    private static string ReviewTitle(RoomBlockedHead blocked) => blocked.Disposition switch
    { RoomBlockedDisposition.ConflictAwaitingChoice => "Review concurrent room changes", RoomBlockedDisposition.ProposalAwaitingChoice => "Review related changes", RoomBlockedDisposition.ProposalStale => "Related changes have changed", _ => "Room changes need attention" };
    private IReadOnlyList<ConflictField>? ConflictRows(RoomHeadEvidence evidence) => evidence switch
    {
        RoomHeaderConflictEvidence x => Review(x.Reviewed, x.Submitted, x.DivergentFields), RoomSubroomConflictEvidence x => Review(x.Reviewed, x.Submitted, x.DivergentFields),
        RoomTransitionConflictEvidence x => Review(x.Reviewed, x.Submitted, x.DivergentFields), RoomConnectionConflictEvidence x => Review(x.Reviewed, x.Submitted, x.DivergentFields),
        RoomCheckConflictEvidence x => Review(x.Reviewed, x.Submitted, x.DivergentFields), RoomMetadataConflictEvidence x => Review(x.Reviewed, x.Submitted, x.DivergentFields),
        RoomGeometryConflictEvidence x => GeometryConflictRows(x),
        RoomDirectAnnotationConflictEvidence x => DirectAnnotationReview(x),
        RoomConnectionAnnotationConflictEvidence x => ConnectionAnnotationReview(x), _ => null
    };
    private IReadOnlyList<ConflictField> GeometryConflictRows(RoomGeometryConflictEvidence evidence)
    {
        var fields = Review(evidence.Reviewed, evidence.Submitted, evidence.DivergentFields).ToList();
        var head = workspace.Queue.Blocked ?? throw new InvalidOperationException();
        if (workspace.Queue.PendingIntent.Single(x => x.Sequence == head.Sequence).Operation is ChangeRoomSubroomAnnotation operation)
        {
            var original = operation.Origin.Original.EnableAnnotation;
            var submitted = operation.Action == RoomSubroomAnnotationAction.Clear ? original : operation.Action == RoomSubroomAnnotationAction.Show;
            fields.Add(ConflictValue("Show annotation", original, submitted, evidence.Reviewed.EnableAnnotation));
        }
        return fields;
    }
    private IReadOnlyList<ConflictField> DirectAnnotationReview(RoomDirectAnnotationConflictEvidence evidence)
    {
        var head = workspace.Queue.Blocked ?? throw new InvalidOperationException();
        var operation = workspace.Queue.PendingIntent.Single(x => x.Sequence == head.Sequence).Operation;
        var (origin, action, position) = operation switch
        {
            ChangeRoomTransitionAnnotation x => (x.Origin.Original, x.Action, x.Position),
            ChangeRoomCheckAnnotation x => (x.Origin.Original, x.Action, x.Position), _ => throw new InvalidOperationException()
        };
        var reviewed = evidence.Reviewed;
        var submitted = action switch
        {
            RoomDirectAnnotationAction.Place or RoomDirectAnnotationAction.Move => origin with { X = position!.X, Y = position.Y, EnableAnnotation = true },
            RoomDirectAnnotationAction.Clear => origin with { X = null, Y = null },
            RoomDirectAnnotationAction.Remove => origin with { EnableAnnotation = false },
            RoomDirectAnnotationAction.Show or RoomDirectAnnotationAction.ShowAndSelect => origin with { EnableAnnotation = true },
            RoomDirectAnnotationAction.Reset => origin with { X = reviewed.InGamePositionX, Y = reviewed.InGamePositionY, EnableAnnotation = true }, _ => throw new InvalidOperationException()
        };
        return [ConflictValue("Annotation X", origin.X, submitted.X, reviewed.X), ConflictValue("Annotation Y", origin.Y, submitted.Y, reviewed.Y),
            ConflictValue("Show annotation", origin.EnableAnnotation, submitted.EnableAnnotation, reviewed.EnableAnnotation)];
    }
    private IReadOnlyList<ConflictField> ConnectionAnnotationReview(RoomConnectionAnnotationConflictEvidence evidence)
    {
        var head = workspace.Queue.Blocked ?? throw new InvalidOperationException();
        var operation = workspace.Queue.PendingIntent.Single(x => x.Sequence == head.Sequence).Operation as ChangeRoomConnectionAnnotation ?? throw new InvalidOperationException();
        var canonical = operation.Origin.Members.Single(x => x.EntityId == operation.Origin.CanonicalEntityId);
        var reviewedCanonical = evidence.Reviewed.Members.Single(x => x.EntityId == evidence.Reviewed.CanonicalEntityId);
        var desiredX = operation.Action is RoomConnectionAnnotationAction.Place or RoomConnectionAnnotationAction.Move ? operation.Position!.X : operation.Action == RoomConnectionAnnotationAction.Clear ? null : canonical.X;
        var desiredY = operation.Action is RoomConnectionAnnotationAction.Place or RoomConnectionAnnotationAction.Move ? operation.Position!.Y : operation.Action == RoomConnectionAnnotationAction.Clear ? null : canonical.Y;
        var desiredEnabled = operation.Action == RoomConnectionAnnotationAction.Clear ? canonical.EnableAnnotation : operation.Action != RoomConnectionAnnotationAction.Disable;
        // KeepSubmitted applies canonical intended differences, retaining current
        // canonical fields the operation never changed, then synchronizes siblings.
        var x = canonical.X == desiredX ? reviewedCanonical.X : desiredX;
        var y = canonical.Y == desiredY ? reviewedCanonical.Y : desiredY;
        var enabled = canonical.EnableAnnotation == desiredEnabled ? reviewedCanonical.EnableAnnotation : desiredEnabled;
        var result = new List<ConflictField>();
        foreach (var original in operation.Origin.Members)
        {
            var reviewed = evidence.Reviewed.Members.SingleOrDefault(x => x.EntityId == original.EntityId);
            if (reviewed is null) { result.Add(Field($"Connection {original.EntityId:D}", "Missing from the reviewed group", "Captured group member", true)); continue; }
            var canonicalMember = original.EntityId == canonical.EntityId;
            var xField = ConflictValue($"Connection {original.EntityId:D} — Annotation X (group result)", canonical.X, x, reviewed.X);
            var yField = ConflictValue($"Connection {original.EntityId:D} — Annotation Y (group result)", canonical.Y, y, reviewed.Y);
            var enabledField = ConflictValue($"Connection {original.EntityId:D} — Show annotation (group result)", canonical.EnableAnnotation, enabled, reviewed.EnableAnnotation);
            result.Add(xField with { Divergent = canonicalMember && xField.Divergent });
            result.Add(yField with { Divergent = canonicalMember && yField.Divergent });
            result.Add(enabledField with { Divergent = canonicalMember && enabledField.Divergent });
        }
        return result;
    }
    private static ConflictField ConflictValue<T>(string name, T original, T submitted, T current) => Field(name,
        current, submitted, !EqualityComparer<T>.Default.Equals(original, submitted)
        && !EqualityComparer<T>.Default.Equals(original, current) && !EqualityComparer<T>.Default.Equals(submitted, current));
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        Catalogue.Changed -= LocalRoomMapChanged;
        Shell.EndRoomSwitch(Switch, Entry.Owner);
        try { await RetirePresentationAsync(); }
        finally { lifetime.Dispose(); nativeReference?.Dispose(); }
    }
    private async Task RetireOwnersAsync()
    {
        roomResultSources.Clear(); roomAttemptSources.Clear();
        ReleaseRoomWorkspace();
        try
        {
            try { await RetireFollowAsync(); }
            finally
            {
                try { if (tables is not null) await tables.DisposeAsync(); }
                finally
                {
                    if (nativeOwner is not null)
                    {
                        try { nativeOwner.InvokeVoid("dispose"); }
                        finally { await nativeOwner.DisposeAsync(); nativeOwner = null; }
                    }
                }
            }
        }
        finally
        {
            try
            {
                try { if (mapHeightFlight is not null) await mapHeightFlight; }
                finally
                {
                    if (mapHeightOwner is not null)
                    {
                        var heightOwner = mapHeightOwner; mapHeightOwner = null;
                        try { await heightOwner.InvokeVoidAsync("dispose"); }
                        finally { await heightOwner.DisposeAsync(); }
                    }
                }
            }
            finally
            {
                try { if (imageServer is not null) { imageReleasePending = true; await ReleaseImageAsync(); } }
                finally { nativeReference?.Dispose(); nativeReference = null; }
            }
        }
    }
}
