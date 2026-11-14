using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Authentication;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed class RoomTrackingUnavailableException : Exception { }
internal sealed class RoomTrackingProtocolException(string message) : Exception(message);

/// <summary>
/// Process-local R3 visit directory. Directory admission pins an entry before
/// waiting for its single gate, so lazy retirement cannot expire executing or
/// queued requests. The gate covers reads, registration, execution and publication.
/// HTTP cancellation can cancel waiting; admitted mutation work receives no HTTP
/// cancellation token. No background expiry task or durable receipt is involved.
/// </summary>
internal sealed class RoomEditingTracker(TimeProvider clock, SceneImageFileService files, ILogger<RoomEditingTracker> logger)
{
    private static readonly TimeSpan IdleRetention = TimeSpan.FromHours(24);
    private readonly object directoryGate = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly Dictionary<(string SessionOwner, Guid Tab, Guid Room, Guid Visit), Guid> visits = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> retiredUploads = new();

    public async Task<Wire.RoomOpenResponse?> OpenAsync(LiveAccess access, Guid roomId, Wire.RoomOpenRequest request,
        Func<CancellationToken, Task<Wire.RoomCompleteDocument?>> readDocument, CancellationToken waitingToken)
    {
        if (roomId == Guid.Empty || request is null || !Wire.RoomEditingIdentity.ValidOwner(request.Owner)
            || request is not (Wire.RoomCompleteOpenRequest or Wire.RoomBaselineAdmissionRequest)
            || request is Wire.RoomBaselineAdmissionRequest warm && !Wire.RoomEntryProtocol.ValidBaseline(warm.Baseline, roomId))
            throw Invalid("Invalid room visit admission.");
        var sessionOwner = (await access.RequireAsync(AccessPolicies.Normal, waitingToken)).Owner;
        var owner = request.Owner;
        Entry entry;
        lock (directoryGate)
        {
            RetireIdleEntries();
            var key = (sessionOwner, owner.TabId, roomId, owner.VisitId);
            if (visits.TryGetValue(key, out var existing)) entry = entries[existing];
            else
            {
                var identity = new Wire.RoomTrackingIdentity(owner.TabId, owner.VisitId, roomId, Guid.NewGuid());
                entry = new(identity, clock.GetTimestamp(), request, sessionOwner);
                entries.Add(identity.TrackerId, entry);
                visits.Add(key, identity.TrackerId);
            }
            if (!Wire.RoomEntryProtocol.SameOpen(entry.OpenRequest, request)) throw Invalid("The room visit admission evidence changed.");
            if (entry.Invalidated) throw new RoomTrackingUnavailableException();
            entry.Pins++;
        }
        var entered = false;
        var admitted = false;
        try
        {
            await CollectRetiredUploadsAsync();
            await entry.Gate.WaitAsync(waitingToken);
            entered = true;
            await access.RequireOwnerAsync(entry.SessionOwner, token: waitingToken);
            if (entry.Invalidated) throw new RoomTrackingUnavailableException();
            if (!entry.AdmissionValid && request is Wire.RoomBaselineAdmissionRequest)
            {
                entry.AdmissionValid = true;
            }
            if (!entry.AdmissionValid && request is Wire.RoomCompleteOpenRequest)
            {
                var document = await readDocument(waitingToken);
                if (document is null) return null;
                if (document.Observation.RoomId != roomId || document.Header.Observation.EntityId != roomId)
                    throw Invalid("The initial document belongs to another room.");
                Wire.RoomDocumentReconciliation.Validate(document);
                entry.OpeningDocument = document;
                entry.AdmissionValid = true;
            }
            admitted = true;
            return request switch
            {
                Wire.RoomBaselineAdmissionRequest accepted => new Wire.RoomBaselineAdmissionResponse(entry.Identity, entry.Progress, accepted.Baseline),
                Wire.RoomCompleteOpenRequest => new Wire.RoomCompleteOpenResponse(entry.Identity, entry.Progress,
                    entry.OpeningDocument ?? throw Invalid("Missing complete-open replay evidence.")),
                _ => throw Invalid("Unknown admission branch.")
            };
        }
        finally
        {
            Release(entry, entered, admitted);
        }
    }

    public async Task<T> WithVisitAsync<T>(LiveAccess access, Wire.RoomTrackingIdentity identity,
        Func<Entry, Task<T>> admittedRequest, CancellationToken waitingToken, bool renewActivity = true)
    {
        var sessionOwner = (await access.RequireAsync(AccessPolicies.Normal, waitingToken)).Owner;
        Entry entry;
        lock (directoryGate)
        {
            RetireIdleEntries();
            if (!entries.TryGetValue(identity.TrackerId, out entry!) || entry.Identity != identity || entry.Invalidated)
                throw new RoomTrackingUnavailableException();
            if (entry.SessionOwner != sessionOwner) throw new AccessDeniedException("not_found", 404, "This room visit is not available to this account.");
            entry.Pins++;
        }
        var entered = false;
        var admitted = false;
        long activity = 0;
        try
        {
            await CollectRetiredUploadsAsync();
            await entry.Gate.WaitAsync(waitingToken);
            entered = true;
            await access.RequireOwnerAsync(entry.SessionOwner, token: waitingToken);
            activity = entry.ActivityVersion;
            if (entry.Invalidated || !entry.AdmissionValid) throw new RoomTrackingUnavailableException();
            var value = await admittedRequest(entry);
            admitted = true;
            if (entry.Invalidated) throw new RoomTrackingUnavailableException();
            return value;
        }
        catch (Exception failure) when (entry.Invalidated && failure is not RoomTrackingUnavailableException)
        {
            logger.LogError(failure, "Room tracker {TrackerId} was invalidated because mutation outcome could not be reliably recorded",
                entry.Identity.TrackerId);
            throw new RoomTrackingUnavailableException();
        }
        finally
        {
            Release(entry, entered, renewActivity && (admitted || entered && entry.ActivityVersion != activity));
        }
    }

    public Task<Wire.RoomCompleteDocument?> ReadDocumentAsync(LiveAccess access, Wire.RoomTrackingIdentity identity,
        Func<CancellationToken, Task<Wire.RoomCompleteDocument?>> readDocument, CancellationToken waitingToken) =>
        WithVisitAsync(access, identity, async entry =>
        {
            var document = await readDocument(waitingToken);
            if (document is not null)
            {
                if (document.Observation.RoomId != identity.RoomId) throw Invalid("The document belongs to another room.");
                Wire.RoomDocumentReconciliation.Validate(document);
            }
            return document;
        }, waitingToken);

    private void Release(Entry entry, bool entered, bool admitted)
    {
        lock (directoryGate)
        {
            if (admitted) entry.LastActivity = clock.GetTimestamp();
            entry.Pins--;
            // Release under directory admission so nobody observes an unpinned,
            // still executing entry between the two ownership boundaries.
            if (entered) entry.Gate.Release();
        }
    }

    private void RetireIdleEntries()
    {
        var now = clock.GetTimestamp();
        foreach (var entry in entries.Values.Where(x => x.Pins == 0
            && clock.GetElapsedTime(x.LastActivity, now) >= IdleRetention).ToArray())
        {
            entries.Remove(entry.Identity.TrackerId);
            visits.Remove((entry.SessionOwner, entry.Identity.TabId, entry.Identity.RoomId, entry.Identity.VisitId));
            foreach (var handle in entry.Images.Retire()) retiredUploads.Enqueue(handle);
            entry.Gate.Dispose();
        }
    }

    private async Task CollectRetiredUploadsAsync()
    {
        // Lazy admission-time cleanup only. No timer, restart adoption or orphan
        // deadline; the retained file service continues to own lifecycle admission.
        while (retiredUploads.TryDequeue(out var handle))
            try { await files.DiscardUploadInternalAsync(handle); }
            catch (Exception failure) { logger.LogError(failure, "Expired room image input {Handle} could not be discarded", handle); }
    }

    private static RoomTrackingProtocolException Invalid(string message) => new(message);

    /// <summary>Access only while the directory's owned visit gate is held.</summary>
    internal sealed class Entry(Wire.RoomTrackingIdentity identity, long openedAt, Wire.RoomOpenRequest openRequest, string sessionOwner)
    {
        // Historical member name; value is the account (protected browser owner
        // in bypass), never the required-mode session credential.
        public string SessionOwner { get; } = sessionOwner;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal int Pins { get; set; }
        internal long LastActivity { get; set; } = openedAt;
        internal Wire.RoomCompleteDocument? OpeningDocument { get; set; }
        internal Wire.RoomOpenRequest OpenRequest { get; } = openRequest;
        internal bool AdmissionValid { get; set; }
        public Wire.RoomTrackingIdentity Identity { get; } = identity;
        public bool Invalidated { get; private set; }
        internal long ActivityVersion { get; private set; }
        public Wire.RoomTrackingProgress Progress => new(registeredThrough, terminalThrough, acknowledgedThrough);
        public Wire.RoomBlockedHead? Blocked { get; private set; }
        public RoomEditingImageState Images { get; } = new();
        // Reviewed decisions are separate from the immutable operation. Preserve
        // them across proposal re-preparation; never rebase a queued original.
        public Wire.RoomMergeReview<Wire.RoomHeaderValues>? HeaderReview { get; set; }
        public Wire.RoomMergeReview<Wire.RoomSubroomValues>? SubroomReview { get; set; }
        public Wire.RoomMergeReview<Wire.RoomTransitionValues>? TransitionReview { get; set; }
        public RoomInverseSourceObservation? InverseSource { get; set; }
        public Wire.RoomMergeReview<Wire.RoomConnectionValues>? ConnectionReview { get; set; }
        public Wire.RoomMergeReview<Wire.RoomCheckValues>? CheckReview { get; set; }
        public Wire.RoomMergeReview<Wire.RoomInGameMetadata>? MetadataReview { get; set; }
        public Wire.RoomMergeReview<Wire.RoomSubroomGeometry>? GeometryReview { get; set; }
        public Wire.RoomMergeReview<Wire.RoomDirectAnnotation>? DirectAnnotationReview { get; set; }
        public Wire.RoomMergeReview<Wire.RoomConnectionAnnotationOrigin>? ConnectionAnnotationReview { get; set; }
        public Wire.RoomQueuedOperation? CurrentHead => terminalThrough < registeredThrough
            ? operations[terminalThrough + 1] : null;
        public bool ProjectionPending => results.Values.Any(x => x.ProjectionPending);
        private long registeredThrough, terminalThrough, acknowledgedThrough;
        private readonly SortedDictionary<long, Wire.RoomQueuedOperation> operations = [];
        private readonly Dictionary<Guid, long> operationSequences = [];
        private readonly Dictionary<Guid, Wire.RoomBatchRequest> batches = [];
        private sealed record ResolutionAttempt(Wire.RoomResolveRequest Request, Wire.RoomResolutionAttemptSummary Summary);
        private readonly Dictionary<Guid, ResolutionAttempt> attempts = [];
        private readonly SortedDictionary<long, Wire.RoomTerminalReceipt> receipts = [];
        private readonly SortedDictionary<long, Wire.RoomTerminalResult> results = [];

        /// <summary>
        /// Validates the entire envelope/suffix before changing registration or
        /// acknowledgement. False means duplicate request: publish progress only.
        /// True permits the caller's explicit successful-prefix execution loop.
        /// </summary>
        public bool RegisterBatch(Wire.RoomBatchRequest request)
        {
            Owner(request.Owner);
            if (request.RequestId == Guid.Empty || request.Items.IsDefault
                || !Wire.RoomEditingIdentity.ValidObservation(request.LastCompleteRead, Identity.RoomId))
                throw Invalid("Invalid batch envelope.");
            ValidateClaim(request.Incorporated);
            if (batches.TryGetValue(request.RequestId, out var known))
            {
                if (!Wire.RoomEditingIdentity.SameBatch(known, request)) throw Invalid("The batch request identity was reused with different content.");
                ActivityVersion++;
                return false;
            }

            var expectedNew = registeredThrough + 1;
            long? previous = null;
            var newItems = new List<Wire.RoomQueuedOperation>();
            var knownOperations = new Dictionary<Guid, long>(operationSequences);
            foreach (var item in request.Items)
            {
                if (item is null || item.Sequence <= 0 || item.OperationId == Guid.Empty
                    || previous is { } prior && item.Sequence != prior + 1
                    || !Wire.RoomEditingIdentity.ValidOperation(item.Operation, Identity.RoomId))
                    throw Invalid("Invalid operation sequence or payload.");
                previous = item.Sequence;
                if (operations.TryGetValue(item.Sequence, out var existing))
                {
                    if (existing.OperationId != item.OperationId
                        || !Wire.RoomEditingIdentity.SameOperation(existing.Operation, item.Operation))
                        throw Invalid("The operation identity was reused with different content.");
                }
                else
                {
                    if (item.Sequence != expectedNew || knownOperations.ContainsKey(item.OperationId))
                        throw Invalid("The operation sequence has a gap or reused identity.");
                    foreach (var dependency in Wire.RoomEditingIdentity.Dependencies(item.Operation))
                        if (!knownOperations.TryGetValue(dependency, out var sequence) || sequence >= item.Sequence)
                            throw Invalid("A projected origin names an unknown or later operation.");
                    Images.Validate(item.Operation);
                    newItems.Add(item);
                    knownOperations.Add(item.OperationId, item.Sequence);
                    expectedNew++;
                }
            }

            // No code above this line mutates admitted tracker state. Registered
            // tails remain recorded even if execution subsequently blocks at head.
            foreach (var item in newItems)
            {
                operations.Add(item.Sequence, item);
                operationSequences.Add(item.OperationId, item.Sequence);
                registeredThrough = item.Sequence;
                Images.Pin(item);
            }
            AcceptClaim(request.Incorporated);
            batches.Add(request.RequestId, request);
            ActivityVersion++;
            // Registration may proceed while older outcomes await incorporation,
            // but new execution may not. The caller returns retained progress;
            // a later explicit resume carries the incorporated terminal prefix.
            return (request.Items.IsEmpty || newItems.Count > 0)
                && request.Incorporated.IncorporatedThrough >= terminalThrough;
        }

        public Wire.RoomQueuedOperation? NextOperation => Blocked is null && !ProjectionPending
            && terminalThrough < registeredThrough ? operations[terminalThrough + 1] : null;

        public void RecordBlocked(Wire.RoomBlockedHead blocked)
        {
            var head = operations[terminalThrough + 1];
            if (blocked.Sequence != head.Sequence || blocked.OperationId != head.OperationId
                || blocked.EvidenceId == Guid.Empty || blocked.Revision <= 0 || blocked.Evidence is null
                || Blocked is { } previous && (blocked.Revision <= previous.Revision || blocked.EvidenceId == previous.EvidenceId))
                throw Invalid("Blocked evidence does not identify the current head.");
            Blocked = blocked;
        }

        /// <summary>Call at the reliable commit/no-write seam, BEFORE any fallible projection.</summary>
        public void RecordReceipt(Wire.RoomTerminalReceipt receipt)
        {
            var head = operations[terminalThrough + 1];
            if (receipt.Sequence != head.Sequence || receipt.OperationId != head.OperationId
                || !Enum.IsDefined(receipt.Disposition)) throw Invalid("Terminal receipt does not identify the current head.");
            receipts.Add(receipt.Sequence, receipt);
            results.Add(receipt.Sequence, new(receipt, [], [], [], true));
            terminalThrough = receipt.Sequence;
            Images.Complete(receipt.OperationId);
            Blocked = null;
            HeaderReview = null;
            SubroomReview = null;
            TransitionReview = null;
            InverseSource = null;
            ConnectionReview = null;
            CheckReview = null;
            MetadataReview = null;
            GeometryReview = null;
            DirectAnnotationReview = null;
            ConnectionAnnotationReview = null;
        }

        // Called by the executing owner before any response/projection publication.
        // The reliable receipt exists already; cleanup cannot relabel the write.
        public void RecordCleanupWarning(long sequence, string warning)
        {
            if (!results.TryGetValue(sequence, out var pending) || !pending.ProjectionPending
                || sequence != terminalThrough || sequence <= acknowledgedThrough)
                throw Invalid("Cleanup warning no longer owns unpublished terminal evidence.");
            var receipt = receipts[sequence] with { CleanupWarning = warning };
            receipts[sequence] = receipt;
            results[sequence] = pending with { Receipt = receipt };
        }

        public void InstallProjection(Wire.RoomTerminalResult result)
        {
            if (!receipts.TryGetValue(result.Receipt.Sequence, out var receipt) || receipt != result.Receipt
                || !results.TryGetValue(result.Receipt.Sequence, out var retained) || !retained.ProjectionPending
                || result.Receipt.Sequence <= acknowledgedThrough || result.ProjectionPending || result.Changes.IsDefault
                || result.RecoveryDocuments.IsDefault || result.DeletedRoomIds.IsDefault)
                throw Invalid("Projection does not match the retained terminal receipt.");
            // Prepare/validate the entire affected scope before publishing any body
            // body. Failure leaves the reliable receipt/T ProjectionPending
            // and therefore selects existing read-only replacement recovery.
            var covered = new HashSet<Guid>();
            void Cover(Guid roomId)
            {
                if (roomId == Guid.Empty || !receipt.AffectedRoomIds.Contains(roomId) || !covered.Add(roomId))
                    throw Invalid("Unexpected or duplicate affected room projection.");
            }
            foreach (var document in result.RecoveryDocuments)
            {
                Wire.RoomDocumentReconciliation.Validate(document);
                Cover(document.Observation.RoomId);
            }
            foreach (var roomId in result.DeletedRoomIds) Cover(roomId);
            foreach (var change in result.Changes)
            {
                Wire.RoomDocumentReconciliation.ValidateProjection(change);
                Cover(change.Observed.RoomId);
            }
            if (!covered.SetEquals(receipt.AffectedRoomIds)) throw Invalid("Incomplete affected room projection.");
            var prepared = result;
            results[result.Receipt.Sequence] = prepared;
            // A completed attempt whose ORIGINAL projection was unavailable may
            // receive this explicit read-only replacement. Never freshen a usable
            // retained attempt result when subsequent tracker facts change.
            foreach (var (id, attempt) in attempts.Where(x => x.Value.Summary.TerminalResult is { ProjectionPending: true } pending
                && pending.Receipt.Sequence == result.Receipt.Sequence).ToArray())
                attempts[id] = attempt with { Summary = attempt.Summary with { TerminalResult = prepared } };
        }

        public ImmutableArray<Wire.RoomTerminalResult> PendingProjections => results.Values
            .Where(x => x.ProjectionPending).ToImmutableArray();
        public Wire.RoomTerminalReceipt TerminalReceipt(long sequence) => receipts.TryGetValue(sequence, out var receipt)
            ? receipt : throw Invalid("There is no reliable terminal receipt for this operation.");

        /// <summary>
        /// Binds every attempt before execution. Duplicate or superseded review
        /// returns false and cannot execute. The caller publishes current progress.
        /// A new choice must have a new ID against current server-owned evidence.
        /// </summary>
        private bool RegisterResolution(Wire.RoomResolveRequest request)
        {
            Owner(request.Owner);
            if (request.ResolutionAttemptId == Guid.Empty || request.OperationId == Guid.Empty
                || request.EvidenceId == Guid.Empty || request.Sequence <= 0 || request.EvidenceRevision <= 0
                || !Wire.RoomEditingIdentity.ValidResolution(request.Resolution)
                || !Wire.RoomEditingIdentity.ValidObservation(request.LastCompleteRead, Identity.RoomId))
                throw Invalid("Invalid resolution envelope.");
            if (attempts.TryGetValue(request.ResolutionAttemptId, out var known))
            {
                if (!Wire.RoomEditingIdentity.SameResolution(known.Request, request)) throw Invalid("The resolution identity was reused with different content.");
                ActivityVersion++;
                return false;
            }
            if (!operations.TryGetValue(request.Sequence, out var operation) || operation.OperationId != request.OperationId)
                throw Invalid("Resolution names an unknown operation.");
            var admitted = Blocked is { } blocked && blocked.Sequence == request.Sequence
                && blocked.OperationId == request.OperationId && blocked.EvidenceId == request.EvidenceId
                && blocked.Revision == request.EvidenceRevision && !ProjectionPending;
            if (admitted && !ResolutionMatches(request.Resolution, Blocked!))
                throw Invalid("The resolution choice does not match the current corrective workflow.");
            attempts.Add(request.ResolutionAttemptId, new(request, new(request.ResolutionAttemptId,
                request.Sequence, request.OperationId, request.EvidenceId, request.EvidenceRevision,
                !admitted, !admitted, null, null, null)
                { ReviewedQuestion = admitted ? Blocked : null }));
            ActivityVersion++;
            return admitted;
        }

        private static bool ResolutionMatches(Wire.RoomHeadResolution resolution, Wire.RoomBlockedHead blocked) => resolution switch
        {
            Wire.ChooseRoomConflictValues => blocked.Disposition == Wire.RoomBlockedDisposition.ConflictAwaitingChoice
                && blocked.Evidence is Wire.RoomHeaderConflictEvidence or Wire.RoomSubroomConflictEvidence
                    or Wire.RoomTransitionConflictEvidence or Wire.RoomConnectionConflictEvidence or Wire.RoomCheckConflictEvidence
                    or Wire.RoomMetadataConflictEvidence or Wire.RoomGeometryConflictEvidence
                    or Wire.RoomDirectAnnotationConflictEvidence or Wire.RoomConnectionAnnotationConflictEvidence,
            Wire.ChooseRoomProposal => blocked.Disposition == Wire.RoomBlockedDisposition.ProposalAwaitingChoice
                && blocked.Evidence is Wire.RoomReferenceProposalEvidence or Wire.RoomInverseProposalEvidence,
            Wire.PrepareRoomProposalAgain => blocked.Disposition == Wire.RoomBlockedDisposition.ProposalStale
                && blocked.Evidence is Wire.RoomReferenceProposalEvidence or Wire.RoomInverseProposalEvidence,
            Wire.CorrectRoomSceneImageApply => blocked.Disposition == Wire.RoomBlockedDisposition.CorrectiveBlocked
                && blocked.Evidence is Wire.RoomSceneImageCorrectionEvidence,
            _ => false
        };

        /// <summary>
        /// Runs only a newly admitted attempt. Duplicate/stale attempts publish
        /// retained evidence/current progress without entering the mutation delegate.
        /// The retained summary never substitutes an old modal for the current head.
        /// </summary>
        public async Task ResolveHeadAsync(Wire.RoomResolveRequest request, Func<Task> executeHead)
        {
            if (!RegisterResolution(request)) return;
            try { await executeHead(); }
            finally
            {
                var attempt = attempts[request.ResolutionAttemptId];
                receipts.TryGetValue(request.Sequence, out var terminal);
                var resultingBlock = Blocked is { } head && head.Sequence == request.Sequence ? head : null;
                results.TryGetValue(request.Sequence, out var terminalResult);
                attempts[request.ResolutionAttemptId] = attempt with
                {
                    Summary = attempt.Summary with
                    {
                        Completed = true, TerminalDisposition = terminal?.Disposition,
                        ResultingEvidenceId = resultingBlock?.EvidenceId, ResultingRevision = resultingBlock?.Revision,
                        ResultingQuestion = resultingBlock, TerminalReceipt = terminal, TerminalResult = terminalResult
                    }
                };
            }
        }

        public void Acknowledge(Wire.RoomAckRequest request)
        {
            Owner(request.Owner);
            ValidateClaim(request.Incorporated);
            AcceptClaim(request.Incorporated);
            ActivityVersion++;
        }

        private void ValidateClaim(Wire.RoomIncorporationClaim? claim)
        {
            if (claim is null || claim.IncorporatedThrough < 0 || claim.IncorporatedThrough > terminalThrough
                || claim.Outcomes.IsDefault) throw Invalid("Invalid incorporation claim.");
            var identities = new HashSet<long>();
            foreach (var outcome in claim.Outcomes)
            {
                if (outcome is null || outcome.Sequence <= 0 || outcome.Sequence > claim.IncorporatedThrough
                    || !identities.Add(outcome.Sequence) || !receipts.TryGetValue(outcome.Sequence, out var receipt)
                    || receipt.OperationId != outcome.OperationId) throw Invalid("Incorporation identities do not match retained outcomes.");
            }
            for (var sequence = acknowledgedThrough + 1; sequence <= claim.IncorporatedThrough; sequence++)
                if (!identities.Contains(sequence) || !results.TryGetValue(sequence, out var result) || result.ProjectionPending)
                    throw Invalid("Incorporation must cover a usable contiguous terminal prefix.");
        }

        private void AcceptClaim(Wire.RoomIncorporationClaim claim)
        {
            if (claim.IncorporatedThrough <= acknowledgedThrough) return;
            acknowledgedThrough = claim.IncorporatedThrough;
            foreach (var sequence in results.Keys.TakeWhile(x => x <= acknowledgedThrough).ToArray()) results.Remove(sequence);
            foreach (var (id, attempt) in attempts.Where(x => x.Value.Summary.Sequence <= acknowledgedThrough
                && x.Value.Summary.TerminalResult is not null).ToArray())
                attempts[id] = attempt with { Summary = attempt.Summary with { TerminalResult = null } };
            // Compact receipt/operation/request/attempt evidence deliberately stays.
        }

        public Wire.RoomTrackingResponse Read(long after, double? completedRequestMilliseconds = null,
            Guid? resolutionAttemptId = null)
        {
            if (Invalidated) throw new RoomTrackingUnavailableException();
            if (after < acknowledgedThrough || after > terminalThrough) throw Invalid("The requested result range is no longer available or has not executed.");
            Wire.RoomResolutionAttemptSummary? attempt = null;
            if (resolutionAttemptId is { } attemptId)
            {
                if (!attempts.TryGetValue(attemptId, out var retained)) throw Invalid("The requested resolution attempt is unknown.");
                attempt = retained.Summary;
            }
            ActivityVersion++;
            return new(Identity, Progress, results.Where(x => x.Key > after).Select(x => x.Value).ToImmutableArray(),
                Blocked, operations.Where(x => x.Key > (Blocked?.Sequence ?? terminalThrough))
                    .Select(x => new Wire.RoomOperationIdentity(x.Key, x.Value.OperationId)).ToImmutableArray(),
                ProjectionPending, completedRequestMilliseconds) { ResolutionAttempt = attempt };
        }

        /// <summary>Possible commit without reliable receipt: never replay this visit.</summary>
        public void Invalidate() => Invalidated = true;

        private void Owner(Wire.RoomTrackingOwner owner)
        {
            if (!Wire.RoomEditingIdentity.ValidOwner(owner) || owner.TabId != Identity.TabId || owner.VisitId != Identity.VisitId)
                throw Invalid("The request does not own this room visit.");
        }
    }
}
