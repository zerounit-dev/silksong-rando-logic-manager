using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

public enum V2Severity { Neutral, Warning, Danger }
public enum V2ReferenceState { None, Resolved, Unresolved, Ambiguous, OutOfSync, TargetArchived }
public enum V2InverseState { None, Zero, One, Multiple }

public sealed record RoomHeaderView(Guid RoomId, string FriendlyName, string ReferenceId, string? InGameId,
    string? Contributors, string? Comments, double? SceneUnitWidth, double? SceneUnitHeight, bool HasSceneImageTransform,
    bool IsSceneImageStale, bool IsArchived, bool CanExportZone, DateTime UpdatedUtc, V2Severity FriendlyNameSeverity,
    V2Severity ReferenceIdSeverity, V2Severity InGameIdSeverity)
{
    public AppliedRoomStatus AppliedStatus { get; init; } = AppliedRoomStatus.Neutral;
}
/// <summary>Independent, non-persistence presentation contracts for header children.</summary>
public sealed record RoomTitleView(string FriendlyName, V2Severity Severity, bool IsArchived, AppliedRoomStatus AppliedStatus = AppliedRoomStatus.Neutral);
public sealed record RoomReferenceView(string ReferenceId, V2Severity Severity);
public sealed record RoomGameIdView(string? InGameId, V2Severity Severity);
public sealed record RoomContributorsView(string? Contributors);
/// <summary>Immutable durable baseline and current authored draft for ordinary V2 room-header saves.</summary>
public sealed record RoomHeaderDurableBaseline(Guid RoomId, DateTime UpdatedUtc, string FriendlyName, string? InGameId, string? Contributors, string? Comments, string ReferenceId = "");
public sealed record RoomHeaderDraft(string FriendlyName, string? InGameId, string? Contributors, string? Comments, string ReferenceId = "");
public sealed record RoomReferenceTransitionTargetEvidence(Guid EntityId, DateTime UpdatedUtc, string? ExpectedDestinationRoomReferenceText);
public sealed record RoomReferenceMapSceneTargetEvidence(Guid EntityId, string? ExpectedRoomReferenceText);
/// <summary>Pre-commit, typed evidence captured from resolved IDs before a room reference rename.</summary>
public sealed record RoomReferenceRenameProposal(RoomHeaderDurableBaseline SourceBaseline, RoomHeaderDraft SourceCurrent,
    IReadOnlyList<RoomReferenceTransitionTargetEvidence> TransitionTargets,
    IReadOnlyList<RoomReferenceMapSceneTargetEvidence> MapSceneTargets);
public enum V2RoomHeaderCommandStatus { Committed, Unchanged, Conflict, Missing, Proposal, Unexpected }
public sealed record V2RoomHeaderCommandOutcome(V2RoomHeaderCommandStatus Status, string? Message = null,
    RoomHeaderDurableBaseline? FreshBaseline = null, RoomHeaderDraft? RetainedDraft = null, TimeSpan ResolverElapsed = default,
    RoomReferenceRenameProposal? Proposal = null, bool MapResolutionChanged = false);
/// <summary>Page-owned, non-durable status for the ordinary room-header save path.</summary>
public enum V2RoomHeaderSaveState { Saved, Saving, Conflict, Failed, Missing }
public sealed record V2RoomHeaderSaveStatus(V2RoomHeaderSaveState State, string? Message = null)
{
    public bool IsEditable => State != V2RoomHeaderSaveState.Missing;
}
/// <summary>Read-only active-room navigation and context-control presentation.</summary>
public sealed record RoomNavigationContextControlsView(bool IsArchived);
/// <summary>Read-only room lifecycle/content-control presentation; archived-child visibility is page-local.</summary>
public sealed record RoomLifecycleContentControlsView(bool IsArchived, bool ShowArchivedChildren,
    bool RecheckRoomLogicBusy = false, bool CanExportZone = false);
/// <summary>Read-only room-export intent; the server derives scope from this room ID.</summary>
public sealed record RoomExportIntent(Guid RoomId);
public enum RoomLifecycleContentControlsIntent { RecheckRoomLogic }
public enum V2RoomLifecycleCommandStatus { Committed, Missing, ExpectedFailure, Unexpected }
/// <summary>Typed result for archive, restore, and archived-room permanent deletion.</summary>
public sealed record V2RoomLifecycleCommandOutcome(V2RoomLifecycleCommandStatus Status, string? Message = null,
    TimeSpan SceneImageDeleteElapsed = default);
/// <summary>Read-only safe-Markdown presentation for the room comments section.</summary>
public sealed record RoomCommentsView(string? Markdown);
/// <summary>Typed, page-owned footer intents; neither persistence nor another component owns them.</summary>
public enum RoomReminderFooterIntent { OpenSyntaxGuide }
/// <summary>Presentation-only reminder-footer copy; it has no durable state.</summary>
public sealed record RoomReminderFooterView(string Prose);
/// <summary>Read-only active-room context display state. It is not a map or scene loader contract.</summary>
public sealed record RoomContextPresentationView(bool IsMapVisible);
/// <summary>Typed, non-durable page-modal content contracts. The page host is closed unless content is supplied by a later workflow.</summary>
public abstract record V2PageModalContentView;
public sealed record V2ReferenceUpdateProposalView(string SourceKind, string SourceFriendlyName, string NewReferenceId) : V2PageModalContentView;
public sealed record V2InverseSetupProposalView(string SourceFriendlyName, string TargetFriendlyName,
    string? SourceRoomReferenceId = null, string? SourceAlias = null,
    bool FillsDestinationRoomReference = false, bool FillsDestinationAlias = false) : V2PageModalContentView;
public sealed record V2PermanentDeleteDialogView(string RecordKind, string FriendlyName) : V2PageModalContentView;
public sealed record V2InGameMetadataDialogView(string RecordKind, string? InGameId, double? InGameX, double? InGameY, double? InGameZ, double? LocalX, double? LocalY, double? LocalZ, double? AnnotationX, double? AnnotationY, V2Severity InGameIdSeverity = V2Severity.Neutral) : V2PageModalContentView;
/// <summary>Complete, page-local transition metadata draft.  It intentionally contains no EF row.</summary>
public sealed record TransitionInGameMetadataDraft(string? InGameId, double? InGamePositionX, double? InGamePositionY,
    double? InGamePositionZ, double? LocalPositionX, double? LocalPositionY, double? LocalPositionZ,
    double? AnnotationSceneUnitX, double? AnnotationSceneUnitY);
/// <summary>Complete page-local check metadata draft; it is never an EF row.</summary>
public sealed record CheckInGameMetadataDraft(string? InGameId, double? InGamePositionX, double? InGamePositionY,
    double? InGamePositionZ, double? LocalPositionX, double? LocalPositionY, double? LocalPositionZ,
    double? AnnotationSceneUnitX, double? AnnotationSceneUnitY);
/// <summary>Typed local text draft for the exact scene-dimensions dialog.</summary>
public sealed record V2SceneDimensionsDialogView(string Width, string Height, string? ValidationMessage = null) : V2PageModalContentView;
public sealed record RoomSceneDimensionsDurableBaseline(Guid RoomId, DateTime UpdatedUtc, double? Width, double? Height);
public sealed record RoomSceneDimensionsDraft(double? Width, double? Height);
public enum V2RoomSceneDimensionsCommandStatus { Committed, Unchanged, Conflict, Missing, ExpectedFailure, Unexpected }
public sealed record V2RoomSceneDimensionsCommandOutcome(V2RoomSceneDimensionsCommandStatus Status, string? Message = null,
    RoomSceneDimensionsDurableBaseline? FreshBaseline = null, RoomSceneDimensionsDraft? RetainedDraft = null);
/// <summary>Typed capture dialog state.  It is a page draft, never a Room entity.</summary>
public sealed record V2SceneImageCaptureDialogView(string AvailabilitySummary, string ScaleXPercent, string ScaleYPercent,
    string PanXPercent, string PanYPercent, bool IsAvailable, string ApplyLabel = "Apply capture",
    V2SceneImageCapturePreviewView? Preview = null, long BrowserResetVersion = 0) : V2PageModalContentView;
/// <summary>Typed, read-only scene projection used only by the page-owned capture draft.</summary>
public sealed record V2SceneImageCapturePreviewView(double Width, double Height,
    IReadOnlyList<SceneSubroomFrameView> Frames, IReadOnlyList<SceneMarkerView> Markers);
/// <summary>Browser-local preview values transferred once into the typed capture draft at Apply.</summary>
public sealed record V2SceneImageCapturePreviewValues(string ScaleXPercent, string ScaleYPercent,
    string PanXPercent, string PanYPercent);
/// <summary>Non-dismissible page-owned state while the approved slower image generation is completion-bound.</summary>
public sealed record V2SceneImageGenerationProgressView(string Message = "Generating scene image…") : V2PageModalContentView;
public sealed record V2SceneImageCaptureModalOperation(V2SceneImageCaptureDialogView Draft);
public enum V2SceneImageCaptureCommandStatus { Committed, ExpectedFailure, Missing, Unexpected }
public sealed record V2SceneImageCaptureCommandOutcome(V2SceneImageCaptureCommandStatus Status, string? Message = null,
    SceneImageCaptureDraft? Draft = null);
public sealed record V2CheckDisableDialogView(string FriendlyName) : V2PageModalContentView;
public sealed record V2ConnectionRemoveDialogView(string Alias) : V2PageModalContentView;
public sealed record V2DirectAnnotationRemoveDialogView(string RecordKind, string FriendlyName) : V2PageModalContentView;
public sealed record V2SubroomGeometryDialogView(string FriendlyName, double? X, double? Y, double? Width, double? Height) : V2PageModalContentView;
public sealed record V2SubroomGeometryModalOperation(SubroomDurableBaseline Baseline, V2SubroomGeometryDialogView Draft);
public sealed record V2SubroomRemoveModalOperation(SubroomDurableBaseline Baseline);
public sealed record V2SceneDimensionsModalOperation(RoomSceneDimensionsDurableBaseline Baseline, V2SceneDimensionsDialogView Draft);
public sealed record V2SubroomRemoveDialogView(string FriendlyName) : V2PageModalContentView;
/// <summary>Read-only catalogue reference supplied by the page to the syntax guide.</summary>
public sealed record V2RequirementSyntaxGuideDialogView(RequirementCatalogueReferenceView Reference) : V2PageModalContentView;
/// <summary>Transient page-local timing for one completely applied V2 room view.</summary>
public sealed record V2RefreshDiagnosticView(long Sequence, TimeSpan ServerElapsed);
/// <summary>Page-owned, non-durable permanent-delete confirmation intent.</summary>
public enum V2DeleteConfirmationKind { Subroom, Transition, Check }
public sealed record V2DeleteConfirmationIntent(V2DeleteConfirmationKind Kind, Guid EntityId);
public sealed record SubroomRowView(Guid EntityId, int SortOrder, bool IsArchived, DateTime UpdatedUtc,
    string FriendlyName, string ReferenceId, string Notes, double? SceneUnitX, double? SceneUnitY,
    double? SceneUnitWidth, double? SceneUnitHeight, V2Severity FriendlyNameSeverity,
    V2Severity ReferenceIdSeverity, V2Severity UsageSeverity, V2Severity GeometrySeverity,
    bool EnableAnnotation = true);
public sealed record SubroomTableView(IReadOnlyList<SubroomRowView> ActiveRows, IReadOnlyList<SubroomRowView> ArchivedRows);
public sealed record SubroomReferenceTransitionTargetEvidence(Guid EntityId, DateTime UpdatedUtc, string? ExpectedText);
public sealed record SubroomReferenceConnectionTargetEvidence(Guid EntityId, DateTime UpdatedUtc, string FieldName, string ExpectedText);
public sealed record SubroomReferenceCheckTargetEvidence(Guid EntityId, DateTime UpdatedUtc, string? ExpectedText);
public sealed record SubroomReferenceRenameProposal(SubroomDurableBaseline SourceBaseline, SubroomDraft SourceCurrent, IReadOnlyList<SubroomReferenceTransitionTargetEvidence> TransitionTargets, IReadOnlyList<SubroomReferenceConnectionTargetEvidence> ConnectionTargets, IReadOnlyList<SubroomReferenceCheckTargetEvidence> CheckTargets);
// These are UI contracts, not persistence entities.  Geometry is retained so a
// later scene slice can use one complete draft, but Phase 2a intentionally does
// not permit it to be written.
public sealed record SubroomDurableBaseline(Guid EntityId, DateTime UpdatedUtc, int SortOrder, bool IsArchived,
    string FriendlyName, string ReferenceId, string Notes, double? SceneUnitX, double? SceneUnitY,
    double? SceneUnitWidth, double? SceneUnitHeight, bool EnableAnnotation = true);
public sealed record SubroomDraft(Guid ClientDraftId, string FriendlyName, string ReferenceId, string Notes,
    double? SceneUnitX, double? SceneUnitY, double? SceneUnitWidth, double? SceneUnitHeight);
public enum V2SubroomCommandStatus { Committed, Unchanged, Conflict, Missing, Proposal, ExpectedFailure, Unexpected }
public sealed record V2SubroomCommandOutcome(V2SubroomCommandStatus Status, string? Message = null,
    SubroomDurableBaseline? FreshBaseline = null, SubroomDraft? RetainedDraft = null, Guid? CreatedEntityId = null,
    TimeSpan ResolverElapsed = default, SubroomReferenceRenameProposal? Proposal = null,
    V2AnnotationRefreshImpact AnnotationRefreshImpact = V2AnnotationRefreshImpact.None);
public sealed record TransitionRowView(Guid EntityId, int SortOrder, bool IsArchived, DateTime UpdatedUtc,
    bool? RequirementsParseSucceeded,
    string Alias, string FriendlyName, string? SourceSubroomReferenceText, string? DestinationRoomReferenceText,
    string? DestinationTransitionAliasText, string Requirements, string Notes, bool IsTodo, bool? IsVerified,
    string? InGameId, double? InGamePositionX, double? InGamePositionY, double? InGamePositionZ,
    double? LocalPositionX, double? LocalPositionY, double? LocalPositionZ, double? AnnotationSceneUnitX,
    double? AnnotationSceneUnitY, V2Severity AliasSeverity, V2Severity FriendlyNameSeverity,
    V2Severity SourceReferenceSeverity, V2Severity DestinationRoomSeverity, V2Severity DestinationAliasSeverity,
    V2Severity RequirementsSeverity, V2Severity InGameIdSeverity, V2ReferenceState SourceReferenceState,
    V2ReferenceState DestinationRoomState, V2ReferenceState DestinationAliasState, V2InverseState InverseState,
    bool EnableAnnotation = false, bool CanExplicitInverseSetup = false,
    Guid? ResolvedInverseRoomId = null);
public sealed record TransitionTableView(IReadOnlyList<TransitionRowView> ActiveRows, IReadOnlyList<TransitionRowView> ArchivedRows,
    IReadOnlyList<string> RoomReferenceSuggestions,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> DestinationAliasSuggestionsByTransitionId)
{
    /// <summary>Presentation-only active owning-room subroom reference suggestions.</summary>
    public IReadOnlyList<string> SubroomReferenceSuggestions { get; init; } = [];
}
/// <summary>Complete non-EF durable snapshot/current draft for one transition row.</summary>
public sealed record TransitionDurableBaseline(Guid EntityId, DateTime UpdatedUtc, int SortOrder, bool IsArchived,
    bool? RequirementsParseSucceeded,
    string Alias, string FriendlyName, string? InGameId, double? InGamePositionX, double? InGamePositionY,
    double? InGamePositionZ, double? LocalPositionX, double? LocalPositionY, double? LocalPositionZ,
    double? AnnotationSceneUnitX, double? AnnotationSceneUnitY, string? SourceSubroomReferenceText,
    string? DestinationRoomReferenceText, string? DestinationTransitionAliasText, string Requirements,
    string Notes, bool IsTodo, bool? IsVerified, bool EnableAnnotation = false);
public sealed record TransitionDraft(Guid ClientDraftId, string Alias, string FriendlyName, string? InGameId,
    double? InGamePositionX, double? InGamePositionY, double? InGamePositionZ, double? LocalPositionX,
    double? LocalPositionY, double? LocalPositionZ, double? AnnotationSceneUnitX, double? AnnotationSceneUnitY,
    string? SourceSubroomReferenceText, string? DestinationRoomReferenceText, string? DestinationTransitionAliasText,
    string Requirements, string Notes, bool IsTodo, bool? IsVerified);
public enum V2TransitionCommandStatus { Committed, Unchanged, Conflict, Missing, Proposal, ExpectedFailure, Unexpected }
/// <summary>Required V2 post-command scene work; annotation mutations affect canvas and viewport only.</summary>
public enum V2AnnotationRefreshImpact { None, CanvasAndViewport }
/// <summary>Closed durable annotation kinds that the command layer may select after a scene refresh.</summary>
public enum V2AnnotationSelectionKind { Transition, Check, Connection }
/// <summary>Service-provided selection instruction consumed only after the required scene refresh.</summary>
public sealed record V2AnnotationPostRefreshSelection(V2AnnotationSelectionKind Kind, Guid EntityId);
public sealed record TransitionInverseTargetEvidence(Guid TargetTransitionId, DateTime UpdatedUtc,
    string? ExpectedDestinationRoomReferenceText, string? ExpectedDestinationTransitionAliasText,
    bool FillDestinationRoomReferenceText, bool FillDestinationTransitionAliasText);
/// <summary>Captured before any source write; values are the atomic stale-target comparison evidence.</summary>
public sealed record TransitionInverseProposal(TransitionDurableBaseline SourceBaseline, TransitionDraft SourceCurrent,
    TransitionInverseTargetEvidence Target, string SourceRoomReferenceId, string SourceAlias,
    string SourceFriendlyName, string TargetFriendlyName, bool IsTargetOnlyStatusAction = false);
/// <summary>Typed, no-write inverse proposal for a complete uncommitted transition snapshot.</summary>
public sealed record TransitionInverseCreateProposal(TransitionDraft SourceCurrent,
    TransitionInverseTargetEvidence Target, string SourceRoomReferenceId, string SourceAlias,
    string SourceFriendlyName, string TargetFriendlyName);
public sealed record V2TransitionCommandOutcome(V2TransitionCommandStatus Status, string? Message = null,
    TransitionDurableBaseline? FreshBaseline = null, TransitionDraft? RetainedDraft = null,
    Guid? CreatedEntityId = null, TransitionInverseProposal? Proposal = null,
    TransitionInverseCreateProposal? CreateProposal = null, TimeSpan ResolverElapsed = default,
    V2AnnotationRefreshImpact AnnotationRefreshImpact = V2AnnotationRefreshImpact.None,
    V2AnnotationPostRefreshSelection? PostRefreshSelection = null);
public enum V2ModalFocusOutcome { Initiator, PendingTarget, Correction }
public sealed record V2ModalFocusRoute(string InitiatorId, string? PendingTargetId, V2ModalFocusOutcome Outcome)
{
    public string TargetId => Outcome == V2ModalFocusOutcome.PendingTarget && PendingTargetId is not null ? PendingTargetId : InitiatorId;
}
/// <summary>Page-owned, non-durable modal state. Field/action IDs are stable UI identities, never entity IDs.</summary>
public enum V2ModalRuntimeStage { ProposalOpen, Committing }
public sealed record V2ModalRuntimeState(V2PageModalContentView Content, string InitiatorId, string? PendingTargetId,
    V2ModalRuntimeStage Stage, V2TransitionModalOperation? TransitionOperation = null,
    V2ConnectionPermanentDeleteModalOperation? ConnectionPermanentDeleteOperation = null,
    RoomReferenceRenameProposal? RoomReferenceRenameProposal = null,
    SubroomReferenceRenameProposal? SubroomReferenceRenameProposal = null,
    V2RoomPermanentDeleteModalOperation? RoomPermanentDeleteOperation = null,
    V2TransitionMetadataModalOperation? TransitionMetadataOperation = null,
    V2CheckMetadataModalOperation? CheckMetadataOperation = null,
    V2DeleteConfirmationIntent? DeleteConfirmationIntent = null,
    V2ConnectionAnnotationDisableModalOperation? ConnectionAnnotationDisableOperation = null,
    V2DirectAnnotationRemoveModalOperation? DirectAnnotationRemoveOperation = null,
    V2SubroomGeometryModalOperation? SubroomGeometryOperation = null,
    V2SubroomRemoveModalOperation? SubroomRemoveOperation = null,
    V2SceneDimensionsModalOperation? SceneDimensionsOperation = null,
    V2SceneImageCaptureModalOperation? SceneImageCaptureOperation = null);
/// <summary>Page-only typed transition modal work. It carries service proposals, never EF rows.</summary>
public abstract record V2TransitionModalOperation;
public sealed record V2TransitionInverseEditModalOperation(TransitionInverseProposal Proposal) : V2TransitionModalOperation;
public sealed record V2TransitionInverseCreateModalOperation(TransitionInverseCreateProposal Proposal) : V2TransitionModalOperation;
public sealed record V2TransitionPermanentDeleteModalOperation(Guid EntityId) : V2TransitionModalOperation;
/// <summary>Typed metadata edit state; the baseline is retained for the ordinary field-diff command.</summary>
public sealed record V2TransitionMetadataModalOperation(TransitionDurableBaseline Baseline, TransitionInGameMetadataDraft Draft);
/// <summary>Typed check metadata state retains the complete durable comparison baseline.</summary>
public sealed record CheckMetadataDurableBaseline(Guid EntityId, DateTime UpdatedUtc, int SortOrder, bool IsArchived,
    bool? RequirementsParseSucceeded,
    string FriendlyName, string? SubroomReferenceText, string Requirements, string Notes, string? LocationType,
    bool EnableAnnotation, bool IsTodo, bool? IsVerified, string? InGameId, double? InGamePositionX,
    double? InGamePositionY, double? InGamePositionZ, double? LocalPositionX, double? LocalPositionY,
    double? LocalPositionZ, double? AnnotationSceneUnitX, double? AnnotationSceneUnitY);
public sealed record V2CheckMetadataModalOperation(CheckMetadataDurableBaseline Baseline, CheckInGameMetadataDraft Draft);
/// <summary>Page-only typed connection delete work; it carries only the archived row identity.</summary>
public sealed record V2ConnectionPermanentDeleteModalOperation(Guid EntityId);
/// <summary>Page-only typed confirmation state for an enabled connection alias annotation.</summary>
public sealed record V2ConnectionAnnotationDisableModalOperation(Guid EntityId);
/// <summary>Typed confirmation for a direct transition/check managed annotation.</summary>
public sealed record V2DirectAnnotationRemoveModalOperation(string RecordKind, Guid EntityId, TransitionDurableBaseline? TransitionBaseline = null, CheckMetadataDurableBaseline? CheckBaseline = null);
/// <summary>Page-only archived-room delete confirmation; no room entity crosses the UI boundary.</summary>
public sealed record V2RoomPermanentDeleteModalOperation(Guid RoomId);
/// <summary>Test-only page intents exercise the runtime without exposing an unfinished workflow control or command.</summary>
public enum V2ModalTestIntentKind { InverseProposal, PermanentDelete }
public sealed record V2ModalTestIntent(V2ModalTestIntentKind Kind, string InitiatorId, string? PendingTargetId = null);
public sealed record ConnectionRowView(Guid EntityId, int SortOrder, bool IsArchived, DateTime UpdatedUtc,
    bool? RequirementsParseSucceeded,
    string Alias, string FriendlyName, string SourceSubroomReferenceText, string DestinationSubroomReferenceText,
    string Requirements, string Notes, bool IsTodo, bool? IsVerified, bool EnableAnnotation, double? SceneUnitX,
    double? SceneUnitY, V2Severity AliasSeverity, V2Severity FriendlyNameSeverity, V2Severity SourceReferenceSeverity,
    V2Severity DestinationReferenceSeverity, V2Severity RequirementsSeverity, V2Severity PathwaySeverity,
    V2Severity NoSubroomSeverity, V2ReferenceState SourceReferenceState, V2ReferenceState DestinationReferenceState,
    string PathwayState, bool CanScaffoldInverse, bool CanPlaceAnnotation = false, bool CanRemoveAnnotation = false,
    bool HasCompleteAnnotationCoordinates = false, double? DurableSceneUnitX = null,
    double? DurableSceneUnitY = null);
public sealed record ConnectionTableView(IReadOnlyList<ConnectionRowView> activeRows, IReadOnlyList<ConnectionRowView> archivedRows,
    IReadOnlyList<string> SubroomReferenceSuggestions)
{
    // The connection mapper derives this from its loaded, set-based pathway
    // facts. Razor renders the mapped value and never reconstructs eligibility.
    public IReadOnlyList<ConnectionRowView> ActiveRows { get; } = ConnectionTableMapper.MapPlacementEligibility(activeRows);
    public IReadOnlyList<ConnectionRowView> ArchivedRows { get; } = archivedRows;
    /// <summary>Presentation-only eligibility for the connection final tail.</summary>
    public bool CanRenderUncommittedTail => SubroomReferenceSuggestions.Count > 0;

}
/// <summary>Complete non-EF durable snapshot/current draft for one connection row.</summary>
public sealed record ConnectionDurableBaseline(Guid EntityId, DateTime UpdatedUtc, int SortOrder, bool IsArchived,
    bool? RequirementsParseSucceeded,
    string Alias, string FriendlyName, string SourceSubroomReferenceText, string DestinationSubroomReferenceText,
    string Requirements, string Notes, bool EnableAnnotation, double? SceneUnitX, double? SceneUnitY,
    bool IsTodo, bool? IsVerified);
public sealed record ConnectionDraft(Guid ClientDraftId, string Alias, string FriendlyName,
    string SourceSubroomReferenceText, string DestinationSubroomReferenceText, string Requirements, string Notes,
    bool EnableAnnotation, double? SceneUnitX, double? SceneUnitY, bool IsTodo, bool? IsVerified)
{
    // The empty V2 final tail has no annotation state and starts at Unknown.
    public ConnectionDraft(Guid clientDraftId, string alias, string friendlyName, string source, string destination,
        string requirements, bool enableAnnotation, double? sceneUnitX, double? sceneUnitY, bool isTodo)
        : this(clientDraftId, alias, friendlyName, source, destination, requirements, "", enableAnnotation,
            sceneUnitX, sceneUnitY, isTodo, null) { }
    public ConnectionDraft(Guid clientDraftId, string alias, string friendlyName, string source, string destination,
        string requirements, string notes, bool enableAnnotation, double? sceneUnitX, double? sceneUnitY, bool isTodo)
        : this(clientDraftId, alias, friendlyName, source, destination, requirements, notes, enableAnnotation,
            sceneUnitX, sceneUnitY, isTodo, null) { }
    public ConnectionDraft(Guid clientDraftId, string alias, string friendlyName, string source, string destination,
        string requirements, bool enableAnnotation, double? sceneUnitX, double? sceneUnitY, bool isTodo, bool? isVerified)
        : this(clientDraftId, alias, friendlyName, source, destination, requirements, "", enableAnnotation,
            sceneUnitX, sceneUnitY, isTodo, isVerified) { }
}
public enum V2ConnectionCommandStatus { Committed, Unchanged, Conflict, Missing, ExpectedFailure, Unexpected }
public sealed record V2ConnectionCommandOutcome(V2ConnectionCommandStatus Status, string? Message = null,
    ConnectionDurableBaseline? FreshBaseline = null, ConnectionDraft? RetainedDraft = null,
    Guid? CreatedEntityId = null, TimeSpan ResolverElapsed = default,
    V2AnnotationRefreshImpact AnnotationRefreshImpact = V2AnnotationRefreshImpact.None,
    V2AnnotationPostRefreshSelection? PostRefreshSelection = null);
public sealed record CheckRowView(Guid EntityId, int SortOrder, bool IsArchived, DateTime UpdatedUtc,
    bool? RequirementsParseSucceeded,
    string FriendlyName, string? SubroomReferenceText, string Requirements, string Notes, string? LocationType,
    string LocationTypeDisplayName,
    bool IsTodo, bool? IsVerified, string? InGameId, double? InGamePositionX, double? InGamePositionY,
    double? InGamePositionZ, double? LocalPositionX, double? LocalPositionY, double? LocalPositionZ,
    double? AnnotationSceneUnitX, double? AnnotationSceneUnitY, bool EnableAnnotation, V2Severity FriendlyNameSeverity,
    V2Severity SubroomReferenceSeverity, V2Severity RequirementsSeverity, V2Severity InGameIdSeverity,
    V2Severity PositionSeverity, V2Severity LocationTypeSeverity, V2ReferenceState SubroomReferenceState);
public sealed record CheckTableView(IReadOnlyList<CheckRowView> ActiveRows, IReadOnlyList<CheckRowView> ArchivedRows,
    IReadOnlyList<string> SubroomReferenceSuggestions, IReadOnlyList<CheckLocationTypeDefinition> LocationTypes);
/// <summary>Read-only scene projection. Coordinates remain lower-left/y-up scene units.</summary>
public sealed record SceneImageDisplayView(bool HasTransform, bool IsStale, bool ImageExists, long ImageVersion = 0)
{ public bool IsAvailable => HasTransform && !IsStale && ImageExists; }
/// <summary>Read-only scene projection. Coordinates remain lower-left/y-up scene units.</summary>
public sealed record SceneLayoutView(bool HasValidBounds, double? SceneUnitWidth, double? SceneUnitHeight,
    IReadOnlyList<SceneSubroomFrameView> Frames, IReadOnlyList<SceneMarkerView> Markers,
    SceneImageDisplayView? Image = null, IReadOnlyList<SceneRelationshipThreadView>? Threads = null)
{
    public IReadOnlyList<SceneRelationshipThreadView> RelationshipThreads => Threads ?? [];
}
public sealed record SceneSubroomFrameView(Guid EntityId, string Label, string? Title, double X, double Y, double Width, double Height);
public sealed record SceneMarkerView(Guid EntityId, string Kind, string Label, string? Title, double X, double Y);
/// <summary>Resolved-ID-only live-canvas relationship with complete renderable endpoint geometry.</summary>
public sealed record SceneRelationshipThreadView(Guid MarkerEntityId, Guid FrameEntityId, double MarkerX, double MarkerY,
    double FrameX, double FrameY, double FrameWidth, double FrameHeight);
/// <summary>Closed pane-local scene identity. It is transient and never a persistence key or DOM marker.</summary>
public enum V2SceneSelectedItemKind { Subroom, Transition, Check, Connection }
/// <summary>One pane-local selection survives a non-renderable durable state; renderability is derived from SceneLayoutView.</summary>
public sealed record V2SceneSelectedItem(V2SceneSelectedItemKind Kind, Guid EntityId);
/// <summary>Browser callback evidence for one mounted scene owner; never persisted.</summary>
public sealed record V2SceneSelectionCallback(Guid RoomId, long OwnerGeneration, V2SceneSelectedItem? Selection);
/// <summary>Page-to-scene transient placement request; it is never persisted or a UI identity.</summary>
public sealed record SceneAnnotationPlacementRequest(string Kind, Guid EntityId, string InitiatorId, Guid? GroupMarkerEntityId = null);
/// <summary>Transient, page-derived availability for the fixed scene annotation action box.</summary>
public enum V2SceneAction { Rearm, Reset, Clear, ToggleVisibility, Dismiss }
public sealed record V2SceneActionBoxView(bool RearmEnabled, bool ResetEnabled, bool ClearEnabled, bool VisibilityEnabled,
    bool DismissEnabled, string RearmLabel, string ResetLabel, string ClearLabel, string VisibilityLabel, string DismissLabel,
    bool AnnotationIsVisible, string StatusText);
// Typed V2 check write contracts deliberately exclude imported metadata and
// annotation fields. Those remain read-only until their later workflows.
public sealed record CheckDurableBaseline(Guid EntityId, DateTime UpdatedUtc, int SortOrder, bool IsArchived,
    bool? RequirementsParseSucceeded,
    string FriendlyName, string? SubroomReferenceText, string Requirements, string Notes,
    string? LocationType, bool IsTodo, bool? IsVerified);
public sealed record CheckDraft(Guid ClientDraftId, string FriendlyName, string? SubroomReferenceText,
    string Requirements, string Notes, string? LocationType, bool IsTodo, bool? IsVerified);
public enum V2CheckCommandStatus { Committed, Unchanged, Conflict, Missing, ExpectedFailure, Unexpected }
public sealed record V2CheckCommandOutcome(V2CheckCommandStatus Status, string? Message = null,
    CheckDurableBaseline? FreshBaseline = null, CheckDraft? RetainedDraft = null, Guid? CreatedEntityId = null,
    TimeSpan ResolverElapsed = default,
    V2AnnotationRefreshImpact AnnotationRefreshImpact = V2AnnotationRefreshImpact.None,
    V2AnnotationPostRefreshSelection? PostRefreshSelection = null);
public sealed record RoomEditorV2View(RoomHeaderView Header, SubroomTableView Subrooms, TransitionTableView Transitions,
    ConnectionTableView Connections, CheckTableView Checks)
{
    /// <summary>Loaded independently by the Phase-4 scene loader; never contains persistence entities.</summary>
    public SceneLayoutView? Scene { get; init; }
    /// <summary>Read-only browser parser context; multiplicity is intentionally retained.</summary>
    public RequirementsParserContextView RequirementsParserContext { get; init; } = new([], [], [], []);
}
public sealed record RequirementsParserRoomView(Guid RoomId, string ReferenceId);
public sealed record RequirementsParserCheckView(Guid RoomId, string FriendlyName);
/// <summary>Browser recognition input only; management/output metadata never crosses this boundary.</summary>
public sealed record RequirementsParserPredicateView(string InputSyntax, IReadOnlyList<string> Aliases);
/// <summary>Browser recognition input only; item output metadata remains formatter-owned.</summary>
public sealed record RequirementsParserItemView(IReadOnlyList<string> Aliases);
public sealed record RequirementsParserContextView(IReadOnlyList<RequirementsParserRoomView> Rooms,
    IReadOnlyList<RequirementsParserCheckView> Checks,
    IReadOnlyList<RequirementsParserPredicateView> Predicates,
    IReadOnlyList<RequirementsParserItemView> Items);
