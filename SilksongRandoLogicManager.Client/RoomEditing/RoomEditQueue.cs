using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.RoomEditing;

/// <summary>
/// One mounted room visit's immutable intent/progress ledger. Native live values,
/// optimistic presentation, focus and modal ownership remain separate. Preparing
/// transport is not submission; MarkBatchSent is called at actual HTTP dispatch.
/// </summary>
public sealed class RoomEditQueue
{
    private readonly List<RoomQueuedOperation> operations = [];
    private Dictionary<long, RoomTerminalReceipt> knownReceipts = [];
    private RoomBatchRequest? activeBatch;
    private PreparedResult? preparedResult;
    private bool batchSent;
    private long? unconfirmedBatchThrough;
    private RoomResolveRequest? unconfirmedResolution;
    // Transport evidence only; drafts, preparation, reads and temporary uploads
    // cannot create it. Typed recovery can settle it while this visit is owned.
    public bool HasUnresolvedSentOutcome => unconfirmedBatchThrough is not null || unconfirmedResolution is not null;
    public event Action? IntentAppended;
    private ImmutableArray<RoomOperationIdentity> lastObservedUnexecuted = [];
    public RoomTrackingIdentity Identity { get; }
    public RoomConfirmedState Confirmed { get; private set; }
    public long RegisteredThrough { get; private set; }
    public long TerminalThrough { get; private set; }
    public long IncorporatedThrough { get; private set; }
    public long AcknowledgedThrough { get; private set; }
    public long SubmittedThrough { get; private set; }
    public RoomBlockedHead? Blocked { get; private set; }
    public bool ProjectionPending { get; private set; }
    public bool RecoveryRequired { get; private set; }
    public bool TrackingUnavailable { get; private set; }
    public bool RequestOutstanding { get; private set; }
    public double? CompletedRequestMilliseconds { get; private set; }
    public double? CompletedSaveElapsedMilliseconds { get; private set; }
    public long QueuedThrough => operations.Count == 0 ? 0 : operations[^1].Sequence;
    public bool HasPendingIntent => operations.Count > IncorporatedThrough || AcknowledgedThrough < IncorporatedThrough;
    public ImmutableArray<RoomQueuedOperation> PendingIntent => operations
        .Where(x => x.Sequence > IncorporatedThrough).ToImmutableArray();

    public RoomEditQueue(RoomTrackingIdentity identity, RoomTrackingProgress progress, RoomCompleteDocument baseline)
    {
        if (identity is null || !RoomEditingIdentity.ValidOwner(new(identity.TabId, identity.VisitId))
            || identity.RoomId == Guid.Empty || identity.TrackerId == Guid.Empty
            || progress != new RoomTrackingProgress(0, 0, 0)
            || baseline.Observation.RoomId != identity.RoomId)
            throw Invalid("A room queue must start from an unused, owned tracking visit.");
        Identity = identity;
        Confirmed = RoomDocumentReconciliation.FromComplete(baseline);
    }

    public RoomQueuedOperation Enqueue(RoomEditOperation operation)
    {
        if (TrackingUnavailable || Blocked is not null || ProjectionPending || Confirmed.Document is null)
            throw new InvalidOperationException("Room authoring is blocked by the current corrective workflow.");
        if (!RoomEditingIdentity.ValidOperation(operation, Identity.RoomId)) throw Invalid("Unusable room intent.");
        var known = operations.Select(x => x.OperationId).ToHashSet();
        if (RoomEditingIdentity.Dependencies(operation).Any(x => !known.Contains(x)))
            throw Invalid("Projected intent must name preceding operations in this visit.");
        var item = new RoomQueuedOperation(operations.Count + 1L, Guid.NewGuid(), operation);
        operations.Add(item);
        IntentAppended?.Invoke();
        return item;
    }

    /// <summary>Freeze the entire currently available new suffix; never debounce or coalesce.</summary>
    public RoomBatchRequest? PrepareBatch()
    {
        if (RequestOutstanding || TrackingUnavailable || RecoveryRequired || Blocked is not null || ProjectionPending)
            return null;
        if (operations.Count <= RegisteredThrough && RegisteredThrough == TerminalThrough) return null;
        var document = Confirmed.Document ?? throw Invalid("The deleted room has no save continuation.");
        activeBatch = new(new(Identity.TabId, Identity.VisitId), Guid.NewGuid(), Claim(), document.Observation,
            operations.Where(x => x.Sequence > RegisteredThrough).ToImmutableArray());
        RequestOutstanding = true;
        batchSent = false;
        return activeBatch;
    }

    public void MarkBatchSent(Guid requestId)
    {
        if (activeBatch?.RequestId != requestId || !RequestOutstanding || batchSent)
            throw Invalid("This batch does not own the transport boundary.");
        batchSent = true;
        if (!activeBatch.Items.IsEmpty) SubmittedThrough = Math.Max(SubmittedThrough, activeBatch.Items[^1].Sequence);
        unconfirmedBatchThrough = SubmittedThrough;
    }

    public void MarkResolutionSent(RoomResolveRequest request)
    {
        if (!RequestOutstanding || activeBatch is not null) throw Invalid("This resolution does not own the transport boundary.");
        unconfirmedResolution = request;
    }

    public void CancelBeforeSend(Guid requestId)
    {
        if (activeBatch?.RequestId != requestId || batchSent) throw Invalid("A submitted batch cannot be cancelled as unsent.");
        activeBatch = null;
        RequestOutstanding = false;
    }

    /// <summary>Resolution, read recovery and ack share this same single transport owner.</summary>
    public void BeginControlRequest()
    {
        if (RequestOutstanding || TrackingUnavailable) throw new InvalidOperationException("The room tracker already owns a request or is unavailable.");
        RequestOutstanding = true;
        activeBatch = null;
        batchSent = false;
    }

    public RoomIncorporationClaim Claim() => new(IncorporatedThrough,
        operations.Where(x => x.Sequence > AcknowledgedThrough && x.Sequence <= IncorporatedThrough)
            .Select(x => new RoomOperationIdentity(x.Sequence, x.OperationId)).ToImmutableArray());

    // A decoded save interval is independent of fallible native installation and
    // terminal progress. Reads/acks never publish one, nor does a lost response.
    public void RecordCompletedSave(RoomTrackingResponse response, double elapsedMilliseconds)
    {
        if (!RequestOutstanding || TrackingUnavailable || response.Identity != Identity
            || !double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
            throw Invalid("The completed save interval does not belong to this room request.");
        CompletedSaveElapsedMilliseconds = elapsedMilliseconds;
    }

    /// <summary>
    /// Validate and prepare privately. Neither receipt delivery nor this method
    /// advances I. The workspace must reconcile native generations and stage its
    /// complete confirmed/live/presentation projection before CommitInstallation.
    /// </summary>
    public PreparedResult PrepareIncorporation(RoomTrackingResponse response)
    {
        if (!RequestOutstanding || preparedResult is not null) throw Invalid("No unprepared current visit request owns this response.");
        ValidateEnvelope(response);
        var confirmed = Confirmed;
        var incorporated = IncorporatedThrough;
        var receipts = new Dictionary<long, RoomTerminalReceipt>(knownReceipts);
        var expected = IncorporatedThrough + 1;
        var paused = false;
        foreach (var result in response.Results)
        {
            if (result is null || result.Receipt is not { } receipt || receipt.Sequence != expected
                || receipt.Sequence > response.Progress.TerminalThrough || receipt.Sequence > operations.Count
                || operations[(int)receipt.Sequence - 1].OperationId != receipt.OperationId
                || !Enum.IsDefined(receipt.Disposition) || result.Changes.IsDefault
                || result.RecoveryDocuments.IsDefault || result.DeletedRoomIds.IsDefault)
                throw Invalid("Terminal outcomes do not form the owned contiguous result prefix.");
            if (receipt.AffectedRoomIds.IsDefault || receipt.AffectedRoomIds.Any(x => x == Guid.Empty)
                || receipt.AffectedRoomIds.Distinct().Count() != receipt.AffectedRoomIds.Length)
                throw Invalid("Unusable terminal affected-room account.");
            if (receipts.TryGetValue(receipt.Sequence, out var known) && !SameReceipt(known, receipt))
                throw Invalid("A retained terminal receipt changed identity or disposition.");
            receipts[receipt.Sequence] = receipt;
            var createdId = operations[(int)receipt.Sequence - 1].Operation switch
            {
                CreateRoomSubroom x => (Guid?)x.EntityId,
                CreateRoomTransition x => x.EntityId,
                CreateRoomConnection x => x.EntityId,
                ScaffoldRoomConnectionInverse x => x.NewEntityId,
                CreateRoomCheck x => x.EntityId,
                _ => null
            };
            if (receipt.CreatedEntityId is { } returnedId && returnedId != createdId
                || createdId is not null && receipt.Disposition == RoomTerminalDisposition.Committed && receipt.CreatedEntityId != createdId)
                throw Invalid("Creation did not retain its permanent client identity.");
            expected++;
            if (result.ProjectionPending)
            {
                paused = true;
                if (!result.Changes.IsEmpty || !result.RecoveryDocuments.IsEmpty || !result.DeletedRoomIds.IsEmpty)
                    throw Invalid("A projection-pending receipt cannot publish a partial room replacement.");
                continue;
            }
            if (paused) throw Invalid("Execution cannot overtake an unusable authoritative projection.");
            var observedRooms = new HashSet<Guid>();
            foreach (var recovery in result.RecoveryDocuments)
            {
                var replacement = RoomDocumentReconciliation.FromComplete(recovery);
                if (!observedRooms.Add(recovery.Observation.RoomId)
                    || !receipt.AffectedRoomIds.Contains(recovery.Observation.RoomId))
                    throw Invalid("Unexpected or duplicate affected recovery document.");
                if (recovery.Observation.RoomId == Identity.RoomId)
                {
                    confirmed = replacement;
                }
            }
            foreach (var deleted in result.DeletedRoomIds)
            {
                if (deleted == Guid.Empty || !observedRooms.Add(deleted) || !receipt.AffectedRoomIds.Contains(deleted))
                    throw Invalid("Unexpected or duplicate affected-room tombstone.");
                // Absence has no new CacheFlagUtc. Do not fabricate a timestamp
                // or suppress a deletion because an old observation is retained.
                if (deleted == Identity.RoomId)
                {
                    confirmed = new(null, false);
                }
            }
            foreach (var change in result.Changes)
            {
                if (change is null || change.Observed is null) throw Invalid("Missing affected room projection.");
                RoomDocumentReconciliation.ValidateProjection(change);
                if (!observedRooms.Add(change.Observed.RoomId) || !receipt.AffectedRoomIds.Contains(change.Observed.RoomId))
                    throw Invalid("Unexpected or duplicate affected room projection.");
                if (change.Observed.RoomId != Identity.RoomId) continue; // Cross-room facts belong to the common repository.
                confirmed = RoomDocumentReconciliation.Apply(confirmed, change);
            }
            if (!observedRooms.SetEquals(receipt.AffectedRoomIds)) throw Invalid("Incomplete affected room result coverage.");
            ValidateSharedEvidence(result, observedRooms);
            incorporated = receipt.Sequence;
        }
        if (expected - 1 != response.Progress.TerminalThrough || paused != response.ProjectionPending)
            throw Invalid("Progress and retained terminal projections disagree.");

        // Validated retained outcomes precede fallible native/shared installation.
        // An incomplete registration read cannot settle a late original batch.
        if (unconfirmedBatchThrough is { } sent && response.Progress.RegisteredThrough >= sent
            && (response.Progress.TerminalThrough >= sent || response.Blocked is not null || response.ProjectionPending))
            unconfirmedBatchThrough = null;
        if (unconfirmedResolution is { } attempt
            && (response.ResolutionAttempt?.AttemptId == attempt.ResolutionAttemptId
                || response.Progress.TerminalThrough >= attempt.Sequence)) unconfirmedResolution = null;
        return preparedResult = new(this, response, confirmed, incorporated, receipts);
    }

    private static void ValidateSharedEvidence(RoomTerminalResult result, HashSet<Guid> affected)
    {
        if (affected.Count == 0) return; // Supported terminal no-write outcomes may have no affected facts.
        if (result.Shared?.Rooms is not { } patch || patch.Replacements is null || patch.Removals is null)
            throw Invalid("Missing affected shared room evidence.");
        var supplied = new HashSet<Guid>();
        foreach (var replacement in patch.Replacements)
        {
            if (replacement?.After?.Observation is not { } observation || !supplied.Add(observation.RoomId)
                || !result.Changes.Any(x => x.Observed == observation)
                    && !result.RecoveryDocuments.Any(x => x.Observation == observation))
                throw Invalid("Shared room facts do not match the retained affected outcome.");
        }
        foreach (var removal in patch.Removals)
            if (removal?.Before is not { } before || !supplied.Add(before.RoomId) || !result.DeletedRoomIds.Contains(before.RoomId))
                throw Invalid("Shared room removal does not match the retained affected outcome.");
        if (!supplied.SetEquals(affected)) throw Invalid("Incomplete shared affected room evidence.");
        if (result.Receipt.SharedTimestamps is { } timestamps && patch.Timestamps != timestamps.Rooms)
            throw Invalid("Shared room evidence changed the original execution bracket.");
        // This proves carried evidence, NOT eligibility to replace current shared
        // facts. An original historical result may be accounted against the visit's
        // adopted baseline while verified newer repository observations stay put.
    }

    /// <summary>
    /// Opaque, visit-owned installation candidate. Pending is read at use time:
    /// boundaries accepted while native interop is outstanding must also reproject.
    /// No operation payload or origin is rebased by preparing or installing results.
    /// </summary>
    public sealed class PreparedResult
    {
        internal RoomEditQueue Owner { get; }
        internal RoomTrackingResponse Response { get; }
        internal Dictionary<long, RoomTerminalReceipt> Receipts { get; }
        public RoomConfirmedState Confirmed { get; }
        public long IncorporatedThrough { get; }
        public ImmutableArray<RoomQueuedOperation> PendingIntent => Owner.operations
            .Where(x => x.Sequence > IncorporatedThrough).ToImmutableArray();

        internal PreparedResult(RoomEditQueue owner, RoomTrackingResponse response, RoomConfirmedState confirmed,
            long incorporatedThrough, Dictionary<long, RoomTerminalReceipt> receipts) =>
            (Owner, Response, Confirmed, IncorporatedThrough, Receipts) = (owner, response, confirmed, incorporatedThrough, receipts);
    }

    /// <summary>
    /// Final synchronous publication boundary. The workspace callback installs its
    /// already prepared state, including ALL current later intent/native generations,
    /// without awaiting, rendering or dispatching callbacks. Only then can I advance.
    /// A native/staging failure before this call leaves the entire prefix unackable.
    /// </summary>
    public void CommitInstallation(PreparedResult installation, Action installWorkspace)
    {
        if (!RequestOutstanding || !ReferenceEquals(preparedResult, installation) || !ReferenceEquals(installation.Owner, this))
            throw Invalid("The prepared room result no longer owns installation.");
        installWorkspace();
        var response = installation.Response;
        Confirmed = installation.Confirmed;
        IncorporatedThrough = installation.IncorporatedThrough;
        RegisteredThrough = response.Progress.RegisteredThrough;
        TerminalThrough = response.Progress.TerminalThrough;
        AcknowledgedThrough = response.Progress.AcknowledgedThrough;
        Blocked = response.Blocked;
        lastObservedUnexecuted = response.RegisteredUnexecuted;
        ProjectionPending = response.ProjectionPending;
        knownReceipts = installation.Receipts;
        if (response.CompletedRequestMilliseconds is { } timing) CompletedRequestMilliseconds = timing;
        RecoveryRequired = false;
        RequestOutstanding = false;
        activeBatch = null;
        batchSent = false;
        preparedResult = null;
    }

    internal void InstallExternal(RoomConfirmedState confirmed, Action installWorkspace)
    {
        if (HasPendingIntent || RequestOutstanding || RecoveryRequired || Blocked is not null || ProjectionPending || TrackingUnavailable)
            throw Invalid("External replacement requires an idle room queue.");
        if (confirmed.Document is { } document && document.Observation.RoomId != Identity.RoomId)
            throw Invalid("Wrong external replacement room.");
        // All validation, native installation and typed staging precede these
        // assignments. A read never changes tracker progress or intent history.
        installWorkspace();
        Confirmed = confirmed;
    }
    public void RequestFailed()
    {
        if (!RequestOutstanding) return;
        // A batch cancelled before dispatch is definitely unsent. Every sent batch
        // remains in SubmittedThrough even if no registration response was seen.
        RecoveryRequired = activeBatch is null || batchSent;
        RequestOutstanding = false;
        activeBatch = null;
        batchSent = false;
        preparedResult = null;
    }

    public void LoseTracking()
    {
        TrackingUnavailable = true;
        RecoveryRequired = false;
        RequestOutstanding = false;
        activeBatch = null;
        preparedResult = null;
    }

    public string CopyableOperationAccount()
    {
        var text = new StringBuilder();
        text.AppendLine($"Room: {Identity.RoomId:D}; visit: {Identity.VisitId:D}; tracker: {Identity.TrackerId:D}");
        text.AppendLine("Some submitted work may already be saved. Refresh reloads durable data; it cannot undo saved changes.");
        var historicalIds = lastObservedUnexecuted.Where(x => x.Sequence > IncorporatedThrough)
            .Select(x => x.OperationId).ToHashSet();
        text.AppendLine("Last server-observed registered/unexecuted (historical; may since have executed):");
        AppendAccount(text, operations.Where(x => x.Sequence > IncorporatedThrough && x.Sequence <= SubmittedThrough
            && historicalIds.Contains(x.OperationId)));
        text.AppendLine("Other submitted, execution/result unconfirmed:");
        AppendAccount(text, operations.Where(x => x.Sequence > IncorporatedThrough && x.Sequence <= SubmittedThrough
            && !historicalIds.Contains(x.OperationId)));
        text.AppendLine("Definitely never submitted:");
        AppendAccount(text, operations.Where(x => x.Sequence > SubmittedThrough));
        // Live fields/tails are separately owned and appended by the workspace,
        // never misrepresented as submitted operations by this queue ledger.
        return text.ToString();
    }

    private void ValidateEnvelope(RoomTrackingResponse response)
    {
        if (response is null || response.Identity != Identity || response.Progress is not { } p
            || p.AcknowledgedThrough < AcknowledgedThrough || p.AcknowledgedThrough > IncorporatedThrough
            || p.TerminalThrough < TerminalThrough || p.TerminalThrough > p.RegisteredThrough
            || p.RegisteredThrough < RegisteredThrough || p.RegisteredThrough > SubmittedThrough
            || response.Results.IsDefault || response.RegisteredUnexecuted.IsDefault
            || response.CompletedRequestMilliseconds is { } ms && (!double.IsFinite(ms) || ms < 0))
            throw Invalid("Unusable or wrong-visit room progress.");
        var firstUnexecuted = p.TerminalThrough + 1;
        if (response.Blocked is { } blocked)
        {
            if (blocked.Sequence != firstUnexecuted || blocked.Sequence > p.RegisteredThrough
                || blocked.Sequence > operations.Count || blocked.OperationId != operations[(int)blocked.Sequence - 1].OperationId
                || blocked.EvidenceId == Guid.Empty || blocked.Revision <= 0 || blocked.Evidence is null
                || !Enum.IsDefined(blocked.Disposition)) throw Invalid("Blocked evidence does not identify the current head.");
            firstUnexecuted++;
        }
        var expected = firstUnexecuted;
        foreach (var identity in response.RegisteredUnexecuted)
        {
            if (identity is null || identity.Sequence != expected || identity.Sequence > p.RegisteredThrough
                || identity.OperationId != operations[(int)expected - 1].OperationId)
                throw Invalid("Registered suffix identities do not match submitted intent.");
            expected++;
        }
        if (expected != p.RegisteredThrough + 1) throw Invalid("Incomplete registered suffix account.");
        if (response.ResolutionAttempt is { } attempt
            && (attempt.AttemptId == Guid.Empty || attempt.Sequence <= 0 || attempt.Sequence > p.RegisteredThrough
                || attempt.OperationId != operations[(int)attempt.Sequence - 1].OperationId
                || attempt.ReviewedEvidenceId == Guid.Empty || attempt.ReviewedRevision <= 0
                || attempt.TerminalDisposition is { } disposition && (!Enum.IsDefined(disposition) || attempt.Sequence > p.TerminalThrough)
                || (attempt.ResultingEvidenceId is null) != (attempt.ResultingRevision is null)
                || attempt.ResultingEvidenceId == Guid.Empty || attempt.ResultingRevision is <= 0))
            throw Invalid("Unusable retained resolution-attempt evidence.");
        if (response.ResolutionAttempt is { } retainedAttempt)
        {
            bool QuestionMatches(RoomBlockedHead question, Guid evidenceId, long revision) =>
                question.Sequence == retainedAttempt.Sequence && question.OperationId == retainedAttempt.OperationId
                && question.EvidenceId == evidenceId && question.Revision == revision
                && Enum.IsDefined(question.Disposition) && question.Evidence is not null;
            if (!retainedAttempt.ReviewSuperseded && retainedAttempt.ReviewedQuestion is null
                || retainedAttempt.ReviewedQuestion is { } reviewed
                    && !QuestionMatches(reviewed, retainedAttempt.ReviewedEvidenceId, retainedAttempt.ReviewedRevision)
                || retainedAttempt.ResultingEvidenceId.HasValue != (retainedAttempt.ResultingQuestion is not null)
                || retainedAttempt.ResultingQuestion is { } resulting
                    && !QuestionMatches(resulting, retainedAttempt.ResultingEvidenceId!.Value, retainedAttempt.ResultingRevision!.Value)
                || retainedAttempt.TerminalDisposition.HasValue != (retainedAttempt.TerminalReceipt is not null)
                || retainedAttempt.TerminalReceipt is { } receipt
                    && (receipt.Sequence != retainedAttempt.Sequence || receipt.OperationId != retainedAttempt.OperationId
                        || receipt.Disposition != retainedAttempt.TerminalDisposition)
                || retainedAttempt.TerminalResult is { } result
                    && (retainedAttempt.TerminalReceipt is not { } terminal || !SameReceipt(terminal, result.Receipt)))
                throw Invalid("Retained attempt bodies do not match their original identities.");
            // Deliberately do not compare historical questions with response.Blocked:
            // only that separately validated CURRENT head controls review/authoring.
        }
    }

    private static bool SameReceipt(RoomTerminalReceipt left, RoomTerminalReceipt right) =>
        left.Sequence == right.Sequence && left.OperationId == right.OperationId && left.Disposition == right.Disposition
        && left.CreatedEntityId == right.CreatedEntityId && left.CleanupWarning == right.CleanupWarning
        && left.HistoryWarning == right.HistoryWarning
        && left.AffectedRoomIds.SequenceEqual(right.AffectedRoomIds)
        && left.SharedTimestamps == right.SharedTimestamps && left.SharedOriginalRooms.SequenceEqual(right.SharedOriginalRooms);
    private static void AppendAccount(StringBuilder target, IEnumerable<RoomQueuedOperation> items)
    {
        foreach (var item in items) target.AppendLine(JsonSerializer.Serialize(item));
    }
    private static JsonException Invalid(string message) => new(message);
}
