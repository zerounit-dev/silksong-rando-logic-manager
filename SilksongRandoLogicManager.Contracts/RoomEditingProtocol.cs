using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Contracts;

public sealed record RoomTrackingOwner(Guid TabId, Guid VisitId);
public sealed record RoomTrackingIdentity(Guid TabId, Guid VisitId, Guid RoomId, Guid TrackerId);
public sealed record RoomQueuedOperation(long Sequence, Guid OperationId, RoomEditOperation Operation);
public sealed record RoomOperationIdentity(long Sequence, Guid OperationId);
// IncorporatedThrough is client-owned, distinct from the server's R/T/A counters.
public sealed record RoomTrackingProgress(long RegisteredThrough, long TerminalThrough, long AcknowledgedThrough);
public sealed record RoomIncorporationClaim(long IncorporatedThrough, ImmutableArray<RoomOperationIdentity> Outcomes);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RoomCompleteOpenRequest), "complete-open")]
[JsonDerivedType(typeof(RoomBaselineAdmissionRequest), "baseline-admission")]
public abstract record RoomOpenRequest(RoomTrackingOwner Owner);
public sealed record RoomCompleteOpenRequest(RoomTrackingOwner Owner) : RoomOpenRequest(Owner);
public sealed record RoomBaselineAdmissionRequest(RoomTrackingOwner Owner, RoomAdmissionBaseline Baseline) : RoomOpenRequest(Owner);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RoomCompleteOpenResponse), "complete-open")]
[JsonDerivedType(typeof(RoomBaselineAdmissionResponse), "baseline-admission")]
public abstract record RoomOpenResponse(RoomTrackingIdentity Identity, RoomTrackingProgress Progress);
public sealed record RoomCompleteOpenResponse(RoomTrackingIdentity Identity, RoomTrackingProgress Progress,
    RoomCompleteDocument Document) : RoomOpenResponse(Identity, Progress);
public sealed record RoomBaselineAdmissionResponse(RoomTrackingIdentity Identity, RoomTrackingProgress Progress,
    RoomAdmissionBaseline Baseline) : RoomOpenResponse(Identity, Progress);
public sealed record RoomBatchRequest(RoomTrackingOwner Owner, Guid RequestId, RoomIncorporationClaim Incorporated,
    RoomDocumentObservation LastCompleteRead, ImmutableArray<RoomQueuedOperation> Items);
public sealed record RoomAckRequest(RoomTrackingOwner Owner, RoomIncorporationClaim Incorporated);
public sealed record RoomResolveRequest(RoomTrackingOwner Owner, Guid ResolutionAttemptId, long Sequence,
    Guid OperationId, Guid EvidenceId, long EvidenceRevision, RoomHeadResolution Resolution,
    RoomDocumentObservation LastCompleteRead);

public enum RoomTerminalDisposition { Committed, Satisfied, UserReverted }
public enum RoomBlockedDisposition
{
    ConflictAwaitingChoice, ProposalAwaitingChoice, ProposalStale, CorrectiveBlocked, MissingBlocked
}
/// <summary>
/// A receipt precedes fallible projection. Neither ProjectionPending nor a cleanup
/// warning changes a known commit into rejection or permission to execute again.
/// </summary>
public sealed record RoomTerminalReceipt(long Sequence, Guid OperationId, RoomTerminalDisposition Disposition,
    Guid? CreatedEntityId, ImmutableArray<Guid> AffectedRoomIds, string? CleanupWarning)
{
    public SharedMutationTimestamps? SharedTimestamps { get; init; }
    public string? HistoryWarning { get; init; }
    public ImmutableArray<RoomDocumentObservation> SharedOriginalRooms { get; init; } = [];
}
public sealed record RoomTerminalResult(RoomTerminalReceipt Receipt, ImmutableArray<RoomDocumentDelta> Changes,
    ImmutableArray<RoomCompleteDocument> RecoveryDocuments, ImmutableArray<Guid> DeletedRoomIds, bool ProjectionPending)
{
    public SharedMutationSegment? Shared { get; init; }
}
public sealed record RoomBlockedHead(long Sequence, Guid OperationId, RoomBlockedDisposition Disposition,
    Guid EvidenceId, long Revision, string Message, RoomHeadEvidence Evidence);
public sealed record RoomTrackingResponse(RoomTrackingIdentity Identity, RoomTrackingProgress Progress,
    ImmutableArray<RoomTerminalResult> Results, RoomBlockedHead? Blocked,
    ImmutableArray<RoomOperationIdentity> RegisteredUnexecuted, bool ProjectionPending,
    double? CompletedRequestMilliseconds)
{
    // This is historical attempt evidence, never modal instructions. Render only
    // the current Blocked head above; do not reopen an old attempt's review/focus.
    public RoomResolutionAttemptSummary? ResolutionAttempt { get; init; }
}
public sealed record RoomResolutionAttemptSummary(Guid AttemptId, long Sequence, Guid OperationId,
    Guid ReviewedEvidenceId, long ReviewedRevision, bool Completed, bool ReviewSuperseded,
    RoomTerminalDisposition? TerminalDisposition, Guid? ResultingEvidenceId, long? ResultingRevision)
{
    // Original in-memory attempt evidence, separate from response.Blocked/current
    // progress. Never use these historical questions as modal instructions.
    public RoomBlockedHead? ReviewedQuestion { get; init; }
    public RoomBlockedHead? ResultingQuestion { get; init; }
    public RoomTerminalReceipt? TerminalReceipt { get; init; }
    // All historical attempt bodies/receipt/summary are released at acknowledgement;
    // response.ResolutionAttempt is then null. While unacknowledged, only an
    // originally pending projection may gain read-only recovery.
    public RoomTerminalResult? TerminalResult { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RoomHeaderConflictEvidence), "header-conflict")]
[JsonDerivedType(typeof(RoomSubroomConflictEvidence), "subroom-conflict")]
[JsonDerivedType(typeof(RoomExplanationConflictEvidence), "explanation-conflict")]
[JsonDerivedType(typeof(RoomTransitionConflictEvidence), "transition-conflict")]
[JsonDerivedType(typeof(RoomConnectionConflictEvidence), "connection-conflict")]
[JsonDerivedType(typeof(RoomCheckConflictEvidence), "check-conflict")]
[JsonDerivedType(typeof(RoomMetadataConflictEvidence), "metadata-conflict")]
[JsonDerivedType(typeof(RoomGeometryConflictEvidence), "geometry-conflict")]
[JsonDerivedType(typeof(RoomDirectAnnotationConflictEvidence), "direct-annotation-conflict")]
[JsonDerivedType(typeof(RoomConnectionAnnotationConflictEvidence), "connection-annotation-conflict")]
[JsonDerivedType(typeof(RoomReferenceProposalEvidence), "reference-proposal")]
[JsonDerivedType(typeof(RoomInverseProposalEvidence), "inverse-proposal")]
[JsonDerivedType(typeof(RoomSceneImageCorrectionEvidence), "scene-image-correction")]
[JsonDerivedType(typeof(RoomReloadRequiredEvidence), "reload-required")]
public abstract record RoomHeadEvidence;
public sealed record RoomHeaderConflictEvidence(RoomHeaderValues Reviewed, RoomHeaderValues Submitted,
    ImmutableArray<RoomHeaderField> DivergentFields) : RoomHeadEvidence;
public sealed record RoomSubroomConflictEvidence(RoomSubroomValues Reviewed, RoomSubroomValues Submitted,
    ImmutableArray<RoomSubroomField> DivergentFields) : RoomHeadEvidence;
public sealed record RoomExplanationConflictEvidence(RoomExplanationValues Reviewed, RoomExplanationValues Submitted,
    ImmutableArray<RoomExplanationField> DivergentFields) : RoomHeadEvidence;
public sealed record RoomTransitionConflictEvidence(RoomTransitionValues Reviewed, RoomTransitionValues Submitted,
    ImmutableArray<RoomTransitionField> DivergentFields) : RoomHeadEvidence;
public sealed record RoomConnectionConflictEvidence(RoomConnectionValues Reviewed, RoomConnectionValues Submitted,
    ImmutableArray<RoomConnectionField> DivergentFields) : RoomHeadEvidence;
public sealed record RoomCheckConflictEvidence(RoomCheckValues Reviewed, RoomCheckValues Submitted,
    ImmutableArray<RoomCheckField> DivergentFields) : RoomHeadEvidence;
public sealed record RoomMetadataConflictEvidence(RoomInGameMetadata Reviewed, RoomInGameMetadata Submitted,
    ImmutableArray<RoomMetadataField> DivergentFields) : RoomHeadEvidence;
public sealed record RoomGeometryConflictEvidence(RoomSubroomGeometry Reviewed, RoomSubroomRectangle Submitted,
    ImmutableArray<RoomGeometryField> DivergentFields) : RoomHeadEvidence;
public sealed record RoomDirectAnnotationConflictEvidence(RoomDirectAnnotation Reviewed) : RoomHeadEvidence;
public sealed record RoomConnectionAnnotationConflictEvidence(RoomConnectionAnnotationOrigin Reviewed) : RoomHeadEvidence;
public sealed record RoomReloadRequiredEvidence(Guid? EntityId) : RoomHeadEvidence;
public enum RoomReferenceTargetField
{
    TransitionDestinationRoom, MapSceneRoom, TransitionSourceSubroom,
    ConnectionSourceSubroom, ConnectionDestinationSubroom, CheckSubroom
}
public sealed record RoomReferenceTarget(Guid RoomId, Guid EntityId, RoomReferenceTargetField Field,
    DateTime? UpdatedUtc, string? ExpectedText);
public enum RoomReferenceSourceKind { Room, Subroom }
public sealed record RoomReferenceProposalEvidence(RoomReferenceSourceKind SourceKind, Guid SourceId,
    string SourceFriendlyName, string NewReferenceId, ImmutableArray<RoomReferenceTarget> Targets) : RoomHeadEvidence;
public sealed record RoomInverseProposalEvidence(Guid SourceId, Guid TargetRoomId, Guid TargetTransitionId,
    DateTime TargetUpdatedUtc, string? ExpectedDestinationRoomReferenceText,
    string? ExpectedDestinationTransitionAliasText, bool FillDestinationRoomReferenceText,
    bool FillDestinationTransitionAliasText, string SourceRoomReferenceId, string SourceAlias,
    string SourceFriendlyName, string TargetFriendlyName, bool TargetOnly, bool CreatingSource) : RoomHeadEvidence;
public sealed record RoomSceneImageCorrectionEvidence(Guid DraftOwnerId, long Revision,
    RoomSceneImageBaseline ReviewedBaseline, bool RequiresUploadReselection) : RoomHeadEvidence;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ChooseRoomConflictValues), "choose-conflict")]
[JsonDerivedType(typeof(ChooseRoomProposal), "choose-proposal")]
[JsonDerivedType(typeof(PrepareRoomProposalAgain), "prepare-proposal-again")]
[JsonDerivedType(typeof(CorrectRoomSceneImageApply), "correct-scene-image")]
public abstract record RoomHeadResolution;
// Reviewed typed values are retained in server evidence, not accepted from an
// untrusted replacement row. The request chooses only against that exact evidence.
public sealed record ChooseRoomConflictValues(RoomConflictChoice Choice) : RoomHeadResolution;
public enum RoomProposalChoice { UpdateCapturedTargets, SourceOnly, Revert }
public sealed record ChooseRoomProposal(RoomProposalChoice Choice) : RoomHeadResolution;
public sealed record PrepareRoomProposalAgain : RoomHeadResolution;
public sealed record CorrectRoomSceneImageApply(Guid DraftOwnerId, long Revision,
    RoomSceneImageBaseline ReviewedBaseline, RoomSceneImageSettings Settings,
    Guid? OwnedUploadAttemptId, SceneImageUploadPreparation? Preparation) : RoomHeadResolution;
