using SilksongRandoLogicManager.Client.RoomEditing;
using SilksongRandoLogicManager.Contracts;
using ScenePane = SilksongRandoLogicManager.Client.Components.RoomEditorV2.SceneContextPanePresentation;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private sealed class PointerAttempt(RoomTrackingIdentity visit, long owner, ScenePane.PointerDrop drop)
    {
        public RoomTrackingIdentity Visit { get; } = visit;
        public long Owner { get; } = owner;
        public ScenePane.PointerDrop Drop { get; } = drop;
        public RoomEditOperation? Original { get; set; }
        public RoomQueuedOperation? Queued { get; set; }
        public string State { get; set; } = "prequeueFailed";
        public RoomTerminalDisposition? ValidatedDisposition { get; set; }
        public bool Terminal => State is "settled" or "resolved";
        public ScenePane.PointerReceipt Receipt => new(Owner, Drop.Revision, Drop.Kind, Drop.EntityId, State, Queued?.Sequence, Queued?.OperationId);
    }

    // Latest attempt per target of this tracked visit. A pane remount changes
    // the presentation claim, not the immutable owner/revision of the append.
    private readonly Dictionary<(string Kind, Guid Entity), PointerAttempt> pointerAttempts = [];

    private ScenePane.PointerReceipt? AdmitPointerDrop(Guid room, long owner, ScenePane.PointerDrop drop, bool inspectOnly)
    {
        if (!Current || !workspace.IsAdmitted || room != Opened.Identity.RoomId || owner != sceneOwnerGeneration
            || drop.Revision <= 0 || drop.Kind is not ("subroom" or "exit" or "check" or "connection")) return null;
        var key = (drop.Kind, drop.EntityId);
        pointerAttempts.TryGetValue(key, out var previous);
        if (previous is not null)
        {
            if (previous.Visit != Opened.Identity) previous = null;
            else
            {
                if (previous.Owner == owner && previous.Drop.Revision == drop.Revision)
                    return previous.Drop == drop ? previous.Receipt : null;
                if (previous.Owner > owner || previous.Owner == owner && previous.Drop.Revision > drop.Revision
                    || previous.Queued is not null && !previous.Terminal) return null;
            }
        }
        if (drop.RetryOf is { } retry && (drop.Kind == "subroom" || previous is not { State: "prequeueFailed" }
            || previous.Owner != owner || previous.Drop.Revision != retry
            || (previous.Drop with { Revision = drop.Revision, RetryOf = retry }) != drop)) return null;

        var attempt = new PointerAttempt(Opened.Identity, owner, drop) { Original = drop.RetryOf is null ? null : previous!.Original };
        pointerAttempts[key] = attempt;
        // This independent entry can observe or fence a missing synchronous
        // admission, never materialize an operation or enter the queue.
        if (inspectOnly) return attempt.Receipt;

        Exception? failure = null;
        try
        {
            if (OwnsScene(room, owner) && TransferKeyboardBeforeAction() && OwnsScene(room, owner)
                && !AuthoringBlocked && !workspace.AcceptanceFailed && !workspace.Queue.RecoveryRequired
                && !hiddenSceneLayers.Contains(SceneLayer(drop.Kind)) && FinitePair(drop.X, drop.Y)
                && TrySceneRow(drop.Kind, drop.EntityId) is { } row && SceneRowReady(row)
                && (row is not RoomConnectionRow connection || connection.AnnotationMarkerId == drop.EntityId)
                && (drop.Kind == "subroom" ? FinitePair(drop.Width, drop.Height) && drop.Width > 0 && drop.Height > 0
                    : drop.Width is null && drop.Height is null))
            {
                attempt.Original ??= CreatePointerOperation(row, drop);
                attempt.Queued = workspace.Enqueue(attempt.Original);
                attempt.State = "queued";
            }
        }
        catch (Exception exception)
        {
            // The queue appends before Stage/Install (and before the acceptance
            // observer returns). Recover the exact object synchronously, pre-drain.
            if (attempt.Original is not null)
                attempt.Queued ??= workspace.Queue.PendingIntent.SingleOrDefault(x => ReferenceEquals(x.Operation, attempt.Original));
            // The observer ran before this receipt was associated. Also cover
            // an exception from acceptance notification itself, which need not
            // set AcceptanceFailed, once the exact append has been recovered.
            if (attempt.Queued is not null) RestoreUnsentPointerSuffix();
            failure = exception;
        }

        // Secondary rendering or drain failures cannot turn an append into a
        // scene Retry. Snapshot render and independent inspection retain receipt.
        try { StateHasChanged(); }
        catch (Exception exception) { failure ??= exception; }
        if (attempt.State == "queued") _ = DrainPointerDropAsync();
        if (failure is not null) _ = ReportPointerDropErrorAsync(failure, attempt.Queued is not null);
        return attempt.Receipt;
    }

    private RoomEditOperation CreatePointerOperation(RoomTableRow row, ScenePane.PointerDrop drop) => row switch
    {
        RoomSubroomRow r => new SaveRoomSubroomGeometry(drop.EntityId, RowOrigin(r, r.Geometry), new(drop.X, drop.Y, drop.Width, drop.Height)),
        RoomTransitionRow r => new ChangeRoomTransitionAnnotation(drop.EntityId, RowOrigin(r, Annotation(r.Metadata, r.EnableAnnotation)), RoomDirectAnnotationAction.Move, new(drop.X, drop.Y)),
        RoomCheckRow r => new ChangeRoomCheckAnnotation(drop.EntityId, RowOrigin(r, Annotation(r.Metadata, r.EnableAnnotation)), RoomDirectAnnotationAction.Move, new(drop.X, drop.Y)),
        RoomConnectionRow r => new ChangeRoomConnectionAnnotation(drop.EntityId, ConnectionOrigin(r), RoomConnectionAnnotationAction.Move, new(drop.X, drop.Y)),
        _ => throw new InvalidOperationException("The annotation is not available for a pointer drop.")
    };

    private async Task DrainPointerDropAsync()
    {
        try { await DrainAsync(); }
        catch (Exception exception) { await ReportPointerDropErrorAsync(exception, appended: true); }
    }

    private async Task ReportPointerDropErrorAsync(Exception exception, bool appended)
    {
        try
        {
            if (appended) PublishDefinitePointerFailures();
            if (Current) await Shell.ReportRoomErrorAsync(exception, appended
                ? PointerRoomFailureMessage("The room request could not be completed. Pending work has been retained; Retry recovers progress before any further write.")
                : "Movement was not queued.");
        }
        catch (Exception reportingFailure) { Console.Error.WriteLine(reportingFailure); }
    }

    private ScenePane.PointerReceipt[] RenderPointerDropReceipts(long owner)
    {
        if (!Current || !workspace.IsAdmitted) return [];
        var queue = workspace.Queue;
        return pointerAttempts.Values.Where(x => x.Visit == queue.Identity && (x.Owner == owner || x.Queued is not null))
            .Select(x =>
            {
                var state = queue.TrackingUnavailable && x.Queued is not null && !x.Terminal ? PointerFailureState(x) : x.State;
                return x.Receipt with { State = state, Geometry = x.Queued is not null && state is ("queued" or "projection" or "installationPending" or "unknown")
                    ? new(x.Drop.X, x.Drop.Y, x.Drop.Width, x.Drop.Height) : null };
            }).ToArray();
    }

    // RoomWorkspace invokes its publication delegate only after the queue has
    // validated the entire response, and before any publication/native install.
    private void ObserveValidatedPointerDropResponse(RoomTrackingResponse response)
    {
        foreach (var attempt in pointerAttempts.Values)
        {
            if (attempt.Visit != response.Identity || attempt.Terminal || attempt.Queued is not { } queued) continue;
            var result = response.Results.FirstOrDefault(x => x.Receipt.Sequence == queued.Sequence && x.Receipt.OperationId == queued.OperationId);
            // Preparation has already accounted for which sent requests this
            // response resolves. A recovery read that did not settle a possible
            // later execution cannot revive the exact pause evidence it invalidated.
            var dispatchUnresolved = attempt.State == "unknown" && workspace.Queue.HasUnresolvedSentOutcome;
            if (result is not null)
            {
                attempt.ValidatedDisposition = result.Receipt.Disposition;
                attempt.State = result.ProjectionPending ? "projection" : "installationPending";
            }
            else if (attempt.ValidatedDisposition is not null) continue;
            else if (response.Blocked is { } head && head.Sequence == queued.Sequence && head.OperationId == queued.OperationId)
                attempt.State = dispatchUnresolved ? "unknown" : "blocked";
            else if (response.RegisteredUnexecuted.Any(x => x.Sequence == queued.Sequence && x.OperationId == queued.OperationId))
                attempt.State = dispatchUnresolved ? "unknown" : "unexecuted";
            else if (queued.Sequence > workspace.Queue.SubmittedThrough)
                attempt.State = response.Blocked is not null || response.ProjectionPending || attempt.State == "unsent" ? "unsent" : "queued";
            else if (workspace.Queue.HasUnresolvedSentOutcome) attempt.State = "unknown";
        }
    }

    private void SettleIncorporatedPointerDrops()
    {
        var queue = workspace.Queue;
        foreach (var attempt in pointerAttempts.Values)
        {
            if (attempt.Visit != queue.Identity || attempt.State != "installationPending"
                || attempt.Queued is not { } queued || queued.Sequence > queue.IncorporatedThrough) continue;
            if (attempt.ValidatedDisposition is RoomTerminalDisposition.Committed or RoomTerminalDisposition.Satisfied) attempt.State = "settled";
            else if (attempt.ValidatedDisposition == RoomTerminalDisposition.UserReverted) attempt.State = "resolved";
        }
    }

    private void ObservePointerDropDispatch(RoomResolveRequest? resolution = null)
    {
        var queue = workspace.Queue;
        foreach (var attempt in pointerAttempts.Values)
        {
            if (attempt.Visit != queue.Identity || attempt.Queued is not { } queued) continue;
            // Resolve may affect only its exact head; it does not execute suffix
            // items. A dispatched batch may resume registered suffix work.
            var mayExecute = resolution is null
                ? attempt.State == "unexecuted" || attempt.State == "unsent" && queued.Sequence <= queue.SubmittedThrough
                : attempt.State == "blocked" && queued.Sequence == resolution.Sequence && queued.OperationId == resolution.OperationId;
            if (mayExecute) attempt.State = "unknown";
        }
    }

    private string PointerRoomFailureMessage(string fallback)
    {
        if (workspace.AdmittedQueue is not { } queue || !pointerAttempts.Values.Any(x => x.Visit == queue.Identity
            && x.State is ("projection" or "installationPending") && x.ValidatedDisposition == RoomTerminalDisposition.Committed)) return fallback;
        const string saved = "A movement was saved, but its authoritative result is not available in the scene.";
        return saved + (queue.TrackingUnavailable ? " " + fallback : " Retry recovers the retained result and does not repeat the write.");
    }

    private void ObservePointerAcceptancePause()
    {
        if (workspace.AcceptanceFailed) RestoreUnsentPointerSuffix();
    }

    private void RestoreUnsentPointerSuffix()
    {
        // AcceptanceObserved also runs on success. The caller must have an
        // actual staging pause or an exact append with failed notification.
        // This listener must not mask the existing acceptance result or error.
        try
        {
            if (!Current || pointerAttempts.Count == 0 || workspace.AdmittedQueue is not { } queue || queue.Identity != Opened.Identity) return;
            var unsent = queue.PendingIntent.Where(x => x.Sequence > queue.SubmittedThrough)
                .Select(x => (x.Sequence, x.OperationId)).ToHashSet();
            var restored = false;
            foreach (var attempt in pointerAttempts.Values)
            {
                if (attempt.Visit != queue.Identity || attempt.Terminal || attempt.ValidatedDisposition is not null
                    || attempt.Queued is not { } queued || !unsent.Contains((queued.Sequence, queued.OperationId))) continue;
                attempt.State = "unsent";
                restored = true;
            }
            if (restored) PublishDefinitePointerFailures();
        }
        catch (Exception exception)
        {
            try { Console.Error.WriteLine(exception); }
            catch { /* A presentation diagnostic cannot interrupt acceptance notification. */ }
        }
    }

    private void PublishDefinitePointerFailures()
    {
        if (!SceneContextReady || scenePane is not { } pane) return;
        var receipts = RenderPointerDropReceipts(sceneOwnerGeneration)
            .Where(x => x.Sequence is not null && x.State is ("blocked" or "unexecuted" or "unsent")).ToArray();
        // Paint only; failure to reach a live pane must not interfere with Room
        // error handling or with the next ordinary authoritative render.
        if (receipts.Length == 0) return;
        try { pane.RestoreDefinitePointerFailures(Entry.RoomId, sceneOwnerGeneration, receipts); }
        catch (Exception exception) { Console.Error.WriteLine(exception); }
    }

    private void ObservePointerDropFailure()
    {
        // A secondary failure after queue installation cannot regress a result.
        SettleIncorporatedPointerDrops();
        foreach (var attempt in pointerAttempts.Values)
        {
            if (attempt.Visit != workspace.Queue.Identity || attempt.Terminal || attempt.Queued is null) continue;
            attempt.State = PointerFailureState(attempt);
        }
        PublishDefinitePointerFailures();
    }

    private string PointerFailureState(PointerAttempt attempt)
    {
        var queue = workspace.Queue;
        // A known terminal receipt stays known; never-sent remains provable even
        // when the tracker is lost. Last registered suffix evidence is historical
        // once execution could have resumed without an observed outcome.
        if (attempt.ValidatedDisposition is not null) return attempt.State;
        if (attempt.Queued!.Sequence > queue.SubmittedThrough) return "unsent";
        if (queue.TrackingUnavailable) return "unknown";
        return attempt.State is "blocked" or "unexecuted" ? attempt.State : "unknown";
    }
}
