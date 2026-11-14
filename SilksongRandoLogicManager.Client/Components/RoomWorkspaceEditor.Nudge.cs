using System.Text.Json;
using Microsoft.JSInterop;
using SilksongRandoLogicManager.Client.RoomEditing;
using SilksongRandoLogicManager.Contracts;
using ScenePane = SilksongRandoLogicManager.Client.Components.RoomEditorV2.SceneContextPanePresentation;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    // A capture pins typed concurrency evidence, not merely SVG coordinates. It
    // survives failed admission and is never recaptured by Retry or a later key.
    private sealed class KeyboardCapture(RoomTrackingIdentity identity, long owner, ScenePane.NudgeDraft first, RoomEditOperation origin)
    {
        public RoomTrackingIdentity Identity { get; } = identity;
        public long Owner { get; } = owner;
        public ScenePane.NudgeDraft Draft { get; set; } = first;
        public RoomEditOperation Origin { get; } = origin;
        public RoomEditOperation? Submitted { get; set; }
        public RoomQueuedOperation? Queued { get; set; }
        public string State { get; set; } = "local";
    }
    private readonly Dictionary<(long Owner, long Burst), KeyboardCapture> keyboardCaptures = [];
    private bool keyboardUnsentFailure;
    private bool HasManagedKeyboardDraft => keyboardCaptures.Values.Any(x => x.Queued is null);
    private bool KeyboardGeometryBlocked => keyboardCaptures.Count != 0 && (keyboardUnsentFailure || workspace.Queue.RecoveryRequired
        || workspace.Queue.TrackingUnavailable || workspace.Queue.ProjectionPending || workspace.Queue.Blocked is not null);

    private bool CaptureKeyboardNudge(Guid room, long owner, ScenePane.NudgeDraft draft)
    {
        try
        {
            var capture = PrepareKeyboardCapture(room, owner, draft);
            if (capture is null) return false;
            // No rendering, notification or other application work after publication.
            keyboardCaptures.Add((owner, draft.Burst), capture);
            return true;
        }
        catch (Exception exception)
        {
            _ = ReportKeyboardErrorAsync(exception);
            return false; // No publication or accepted displacement exists.
        }
    }
    private KeyboardCapture? PrepareKeyboardCapture(Guid room, long owner, ScenePane.NudgeDraft draft)
    {
        if (!OwnsScene(room, owner) || AuthoringBlocked || workspace.Queue.RecoveryRequired || keyboardUnsentFailure
            || workspace.Queue.Confirmed.Document?.Scene?.HasValidBounds != true
            || workspace.Queue.PendingIntent.Any(x => x.Operation is SetRoomArchive or DeleteRoom)
            || hiddenSceneLayers.Contains(SceneLayer(draft.Kind)) || draft.Revision != 0 || draft.Burst <= 0
            || keyboardCaptures.ContainsKey((owner, draft.Burst)) || TrySceneRow(draft.Kind, draft.EntityId) is not { } row
            || row.IsArchived || row.LastConfirmed is null || row.Attention) return null;
        // Only OUR own outstanding geometry permits keyboard continuation. Pointer
        // actions and unrelated pending rows keep their existing readiness contract.
        if (row.Pending && row.Dependencies.Any(id => !keyboardCaptures.Values.Any(x => x.Queued?.OperationId == id && x.State == "queued"))) return null;
        RoomEditOperation origin = row switch
        {
            RoomSubroomRow r => new SaveRoomSubroomGeometry(draft.EntityId, RowOrigin(r, r.Geometry), new(r.Geometry.X, r.Geometry.Y, r.Geometry.Width, r.Geometry.Height)),
            RoomTransitionRow r => new ChangeRoomTransitionAnnotation(draft.EntityId, RowOrigin(r, Annotation(r.Metadata, r.EnableAnnotation)), RoomDirectAnnotationAction.Move, new(draft.X, draft.Y)),
            RoomCheckRow r => new ChangeRoomCheckAnnotation(draft.EntityId, RowOrigin(r, Annotation(r.Metadata, r.EnableAnnotation)), RoomDirectAnnotationAction.Move, new(draft.X, draft.Y)),
            RoomConnectionRow r when r.CanAnnotate && r.AnnotationMarkerId == draft.EntityId => new ChangeRoomConnectionAnnotation(draft.EntityId, ConnectionOrigin(r), RoomConnectionAnnotationAction.Move, new(draft.X, draft.Y)),
            _ => throw new InvalidOperationException("The selected annotation is unavailable.")
        };
        var (x, y, width, height) = origin switch
        {
            SaveRoomSubroomGeometry r => (r.Origin.Original.X, r.Origin.Original.Y, r.Origin.Original.Width, r.Origin.Original.Height),
            ChangeRoomTransitionAnnotation r => (r.Origin.Original.X, r.Origin.Original.Y, (double?)null, (double?)null),
            ChangeRoomCheckAnnotation r => (r.Origin.Original.X, r.Origin.Original.Y, (double?)null, (double?)null),
            ChangeRoomConnectionAnnotation r => (r.Origin.Members.Single(x => x.EntityId == r.Origin.CanonicalEntityId).X, r.Origin.Members.Single(x => x.EntityId == r.Origin.CanonicalEntityId).Y, (double?)null, (double?)null),
            _ => throw new InvalidOperationException()
        };
        if (!FinitePair(x, y) || x != draft.X || y != draft.Y || width != draft.Width || height != draft.Height) return null;
        return new(Opened.Identity, owner, draft, origin);
    }

    private ScenePane.NudgeAdmission AdmitKeyboardNudge(Guid room, long owner, ScenePane.NudgeDraft draft)
    {
        if (!Shell.CanInteract) throw new ObjectDisposedException(nameof(App));
        var capture = ExactKeyboardCapture(room, owner, draft);
        ValidateKeyboardAttempt(capture, draft);
        if (capture.Queued is { } owned) return new(capture.State, owned.Sequence, owned.OperationId);
        Exception? failure = null;
        ScenePane.NudgeAdmission receipt;
        try
        {
            capture.Draft = draft;
            if (!OwnsScene(room, owner) || AuthoringBlocked || workspace.Queue.RecoveryRequired || keyboardUnsentFailure
                || !FinitePair(draft.X, draft.Y) || draft.Revision <= 0 || TrySceneRow(draft.Kind, draft.EntityId) is not { IsArchived: false, LastConfirmed: not null })
                throw new InvalidOperationException("Movement was not saved. The retained attempt needs explicit recovery.");
            capture.Submitted ??= capture.Origin switch
            {
                SaveRoomSubroomGeometry r => r with { Submitted = new(draft.X, draft.Y, r.Origin.Original.Width, r.Origin.Original.Height) },
                ChangeRoomTransitionAnnotation r => r with { Position = new(draft.X, draft.Y) },
                ChangeRoomCheckAnnotation r => r with { Position = new(draft.X, draft.Y) },
                ChangeRoomConnectionAnnotation r => r with { Position = new(draft.X, draft.Y) },
                _ => throw new InvalidOperationException()
            };
            capture.Queued = workspace.Enqueue(capture.Submitted);
            capture.State = "queued";
            receipt = new(capture.State, capture.Queued.Sequence, capture.Queued.OperationId);
        }
        catch (Exception exception)
        {
            // Enqueue appends BEFORE Stage/Install. Reference identity is the exact
            // ownership proof even when no return value reached this caller.
            capture.Queued ??= workspace.Queue.PendingIntent.SingleOrDefault(x => ReferenceEquals(x.Operation, capture.Submitted));
            capture.State = capture.Queued is null ? "prequeueFailed" : "unsent";
            if (capture.Queued is not null) keyboardUnsentFailure = true;
            receipt = new(capture.State, capture.Queued?.Sequence, capture.Queued?.OperationId);
            failure = exception;
        }
        // Ownership/result is already recorded. Secondary presentation/logging or
        // drain-observation errors cannot convert an appended operation to prequeue.
        try { StateHasChanged(); }
        catch (Exception exception) { _ = ReportKeyboardErrorAsync(exception); }
        // Rendering/logging must not own whether an admitted queue can continue.
        // Observe the existing drain, including its asynchronous secondary faults;
        // never use such a fault to retry admission or replace this receipt.
        if (receipt.State == "queued") _ = DrainKeyboardNudgeAsync();
        if (failure is not null)
        {
            try { Console.Error.WriteLine(failure); }
            catch (Exception exception) { _ = ReportKeyboardErrorAsync(exception); }
        }
        return receipt;
    }

    private async Task DrainKeyboardNudgeAsync()
    {
        try { await DrainAsync(); }
        catch (Exception exception) { await ReportKeyboardErrorAsync(exception); }
    }

    private async Task ReportKeyboardErrorAsync(Exception exception)
    {
        try
        {
            if (Current) await Shell.ReportRoomErrorAsync(exception, "The room boundary could not be accepted. Native text and pending work have been retained.");
        }
        catch (Exception reportingFailure) { Console.Error.WriteLine(reportingFailure); }
    }
    private KeyboardCapture ExactKeyboardCapture(Guid room, long owner, ScenePane.NudgeDraft draft)
    {
        if (room != Opened.Identity.RoomId || owner != sceneOwnerGeneration
            || !keyboardCaptures.TryGetValue((owner, draft.Burst), out var capture) || capture.Identity != Opened.Identity
            || capture.Draft.Kind != draft.Kind || capture.Draft.EntityId != draft.EntityId)
            throw new InvalidOperationException("The keyboard draft has no matching typed origin. Pending work has been retained.");
        return capture;
    }
    private static void ValidateKeyboardAttempt(KeyboardCapture capture, ScenePane.NudgeDraft draft)
    {
        if (draft.Revision < capture.Draft.Revision || draft.Width != capture.Draft.Width || draft.Height != capture.Draft.Height
            || capture.Submitted is not null && (draft.Revision != capture.Draft.Revision || draft.X != capture.Draft.X || draft.Y != capture.Draft.Y))
            throw new InvalidOperationException("The retained keyboard attempt cannot be replaced.");
    }
    // Independent of the scene DotNetObjectReference. This path only inspects local
    // typed/queue evidence and acknowledges exact root revisions; it NEVER enqueues.
    private ScenePane.NudgeSnapshot? ReconcileKeyboardOwnership()
    {
        var snapshot = NativeKeyboardSnapshot();
        if (snapshot is null) return null;
        if (snapshot.RoomId != Opened.Identity.RoomId || snapshot.OwnerGeneration != sceneOwnerGeneration)
            throw new InvalidOperationException("The scene draft snapshot does not own this room.");
        foreach (var draft in snapshot.Drafts)
        {
            if (draft.Revision > 0 && draft.Sequence is null && draft.State != "incorporated")
                ExactKeyboardCapture(snapshot.RoomId, snapshot.OwnerGeneration, draft);
            if (draft.State is not ("capturing" or "admitting" or "unknown")) continue;
            ScenePane.NudgeAdmission receipt;
            if (draft.Revision == 0 && draft.State == "capturing")
            {
                if (keyboardCaptures.TryGetValue((snapshot.OwnerGeneration, draft.Burst), out var zero))
                {
                    zero = ExactKeyboardCapture(snapshot.RoomId, snapshot.OwnerGeneration, draft);
                    if (zero.Draft != draft || zero.Submitted is not null || zero.Queued is not null)
                        throw new InvalidOperationException("An accepted keyboard capture cannot be discarded.");
                }
                receipt = new("unaccepted");
            }
            else
            {
                var capture = ExactKeyboardCapture(snapshot.RoomId, snapshot.OwnerGeneration, draft);
                ValidateKeyboardAttempt(capture, draft);
                if (capture.Queued is null && capture.Submitted is not null)
                {
                    capture.Queued = workspace.Queue.PendingIntent.SingleOrDefault(x => ReferenceEquals(x.Operation, capture.Submitted));
                    if (capture.Queued is not null)
                    {
                        capture.State = workspace.Queue.ProjectionPending ? "projection"
                            : workspace.Queue.Blocked is not null ? "blocked"
                            : workspace.Queue.TrackingUnavailable || capture.Queued.Sequence <= workspace.Queue.SubmittedThrough ? "unknown" : "unsent";
                        if (capture.State == "unsent") keyboardUnsentFailure = true;
                    }
                }
                if (capture.Queued is null)
                {
                    capture.Draft = draft;
                    capture.State = "prequeueFailed";
                }
                // A returned admission may have been lost after append or even
                // after result observation. Retain that actual operation/state.
                receipt = new(capture.State, capture.Queued?.Sequence, capture.Queued?.OperationId);
            }
            if (!FinishKeyboardDraft(snapshot, draft, receipt))
                throw new InvalidOperationException("The keyboard ownership acknowledgement no longer matches the scene draft.");
            if (receipt.State == "unaccepted") keyboardCaptures.Remove((snapshot.OwnerGeneration, draft.Burst));
        }
        return NativeKeyboardSnapshot();
    }
    private bool FinishKeyboardDraft(ScenePane.NudgeSnapshot snapshot, ScenePane.NudgeDraft draft, ScenePane.NudgeAdmission result) =>
        nativeOwner!.Invoke<bool>("finishRoomSceneDraft", snapshot.RoomId.ToString("D"), snapshot.OwnerGeneration, draft.Burst, draft.Revision, result);
    private async Task RetryKeyboardNudgeAsync(Guid room, long owner)
    {
        if (!OwnsScene(room, owner) || AuthoringBlocked || nativeOwner is null) return;
        try
        {
            var snapshot = ReconcileKeyboardOwnership();
            if (snapshot is null || snapshot.RoomId != room || snapshot.OwnerGeneration != owner) return;
            var take = nativeOwner.Invoke<ScenePane.NudgeTake>("takeRoomSceneDraft", true);
            if (take is not { State: "draft", Draft: { } draft }) return;
            var result = AdmitKeyboardNudge(room, owner, draft);
            if (!FinishKeyboardDraft(snapshot, draft, result))
                throw new InvalidOperationException("The keyboard retry acknowledgement no longer matches the scene draft.");
        }
        catch (Exception exception) { await ReportKeyboardErrorAsync(exception); }
    }

    private ScenePane.NudgeSnapshot? NativeKeyboardSnapshot() => nativeOwner?.Invoke<ScenePane.NudgeSnapshot?>("snapshotRoomSceneDraft");
    private bool KeyboardBlocksDeparture()
    {
        var snapshot = ReconcileKeyboardOwnership();
        if (snapshot is not null && (snapshot.RoomId != Opened.Identity.RoomId || snapshot.OwnerGeneration != sceneOwnerGeneration))
            throw new InvalidOperationException("The scene draft snapshot does not own this room.");
        // A successfully incorporated/reverted revision is only waiting for its
        // DOM reconciliation, not outstanding authoring intent. Keeping that
        // visual bridge dirty can strand the existing deferred refresh drain.
        return HasManagedKeyboardDraft || snapshot?.Drafts.Any(x => x.State != "incorporated") == true;
    }
    private bool TransferKeyboardBeforeAction()
    {
        if (!Current || !Shell.CanInteract || nativeOwner is null) return false;
        var snapshot = ReconcileKeyboardOwnership();
        if (snapshot is null) return !HasManagedKeyboardDraft;
        if (snapshot.RoomId != Opened.Identity.RoomId || snapshot.OwnerGeneration != sceneOwnerGeneration) return false;
        var take = nativeOwner.Invoke<ScenePane.NudgeTake>("takeRoomSceneDraft");
        if (take.State is "blocked" or "unavailable") return false;
        if (take is { State: "draft", Draft: { } draft })
        {
            var result = AdmitKeyboardNudge(snapshot.RoomId, snapshot.OwnerGeneration, draft);
            if (!FinishKeyboardDraft(snapshot, draft, result) || result.State != "queued") return false;
        }
        else if (take.State != "clean") return false;
        var current = NativeKeyboardSnapshot();
        return !HasManagedKeyboardDraft && !keyboardUnsentFailure
            && current?.Drafts.Any(x => x.Sequence is null && x.State != "incorporated") != true;
    }
    private void ObserveKeyboardResponse(RoomTrackingResponse response)
    {
        foreach (var capture in keyboardCaptures.Values.Where(x => x.Queued is not null).ToArray())
        {
            if (capture.State is "settled" or "resolved") continue;
            var operation = capture.Queued!;
            var result = response.Results.SingleOrDefault(x => x.Receipt.Sequence == operation.Sequence && x.Receipt.OperationId == operation.OperationId);
            if (result is not null && !result.ProjectionPending && operation.Sequence <= workspace.Queue.IncorporatedThrough
                && result.Receipt.Disposition is RoomTerminalDisposition.Committed or RoomTerminalDisposition.Satisfied)
            {
                capture.State = "settled";
            }
            else if (result is not null && !result.ProjectionPending && operation.Sequence <= workspace.Queue.IncorporatedThrough
                && result.Receipt.Disposition == RoomTerminalDisposition.UserReverted)
            {
                // Explicit keep-server/revert is resolution, not save success or
                // permission to resubmit the discarded immutable operation.
                capture.State = "resolved";
            }
            else if (response.ProjectionPending) capture.State = "projection";
            else if (response.Blocked is not null) capture.State = "blocked";
            else if (!workspace.Queue.RecoveryRequired) capture.State = "queued";
        }
        PublishKeyboardStates();
        foreach (var key in keyboardCaptures.Where(x => x.Value.State is "settled" or "resolved").Select(x => x.Key).ToArray()) keyboardCaptures.Remove(key);
    }
    private void ObserveKeyboardFailure()
    {
        foreach (var capture in keyboardCaptures.Values.Where(x => x.Queued is not null))
        {
            if (capture.State == "settled") continue;
            capture.State = workspace.Queue.ProjectionPending ? "projection" : workspace.Queue.TrackingUnavailable || capture.Queued!.Sequence <= workspace.Queue.SubmittedThrough ? "unknown" : "unsent";
            if (capture.State == "unsent") keyboardUnsentFailure = true;
        }
        PublishKeyboardStates();
    }
    private void PublishKeyboardStates()
    {
        if (!Current || nativeOwner is null) return;
        var observations = keyboardCaptures.Values.Where(x => x.Owner == sceneOwnerGeneration && x.Queued is not null)
            .Select(x => x.Draft with { State = x.State, Sequence = x.Queued!.Sequence, OperationId = x.Queued.OperationId }).ToArray();
        var gate = workspace.Queue.Blocked is not null ? "blocked"
            : workspace.Queue.TrackingUnavailable || workspace.Queue.RecoveryRequired || workspace.Queue.ProjectionPending ? "unknown"
            : keyboardUnsentFailure ? "unsent" : "";
        nativeOwner.InvokeVoid("observeRoomSceneDrafts", observations, gate);
    }
    private string KeyboardAccount => JsonSerializer.Serialize(keyboardCaptures.Values.Select(x => new { x.Identity, x.Owner, x.Draft, x.State, x.Origin, x.Submitted, x.Queued }));
}
