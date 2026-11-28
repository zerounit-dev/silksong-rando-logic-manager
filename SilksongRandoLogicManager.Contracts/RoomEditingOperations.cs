using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Contracts;

/// <summary>
/// Closed R3 command vocabulary. These records carry immutable user intent, not
/// entities or an arbitrary table/column patch. Scene interaction before a completed
/// gesture is presentation-only and never creates one of these commands.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SaveRoomHeader), "save-header")]
[JsonDerivedType(typeof(SetRoomArchive), "room-archive")]
[JsonDerivedType(typeof(DeleteRoom), "delete-room")]
[JsonDerivedType(typeof(CreateRoomSubroom), "create-subroom")]
[JsonDerivedType(typeof(SaveRoomSubroom), "save-subroom")]
[JsonDerivedType(typeof(ReorderRoomSubroom), "reorder-subroom")]
[JsonDerivedType(typeof(SetRoomSubroomArchive), "subroom-archive")]
[JsonDerivedType(typeof(DeleteRoomSubroom), "delete-subroom")]
[JsonDerivedType(typeof(SaveRoomSubroomGeometry), "subroom-geometry")]
[JsonDerivedType(typeof(ChangeRoomSubroomAnnotation), "subroom-annotation")]
[JsonDerivedType(typeof(CreateRoomExplanation), "create-explanation")]
[JsonDerivedType(typeof(SaveRoomExplanation), "save-explanation")]
[JsonDerivedType(typeof(ReorderRoomExplanation), "reorder-explanation")]
[JsonDerivedType(typeof(SetRoomExplanationArchive), "explanation-archive")]
[JsonDerivedType(typeof(DeleteRoomExplanation), "delete-explanation")]
[JsonDerivedType(typeof(CreateRoomTransition), "create-transition")]
[JsonDerivedType(typeof(SaveRoomTransition), "save-transition")]
[JsonDerivedType(typeof(RequestRoomTransitionInverse), "transition-inverse")]
[JsonDerivedType(typeof(SaveRoomTransitionMetadata), "transition-metadata")]
[JsonDerivedType(typeof(ReorderRoomTransition), "reorder-transition")]
[JsonDerivedType(typeof(SetRoomTransitionArchive), "transition-archive")]
[JsonDerivedType(typeof(DeleteRoomTransition), "delete-transition")]
[JsonDerivedType(typeof(ChangeRoomTransitionAnnotation), "transition-annotation")]
[JsonDerivedType(typeof(CreateRoomConnection), "create-connection")]
[JsonDerivedType(typeof(SaveRoomConnection), "save-connection")]
[JsonDerivedType(typeof(ScaffoldRoomConnectionInverse), "connection-inverse")]
[JsonDerivedType(typeof(ReorderRoomConnection), "reorder-connection")]
[JsonDerivedType(typeof(SetRoomConnectionArchive), "connection-archive")]
[JsonDerivedType(typeof(DeleteRoomConnection), "delete-connection")]
[JsonDerivedType(typeof(ChangeRoomConnectionAnnotation), "connection-annotation")]
[JsonDerivedType(typeof(CreateRoomCheck), "create-check")]
[JsonDerivedType(typeof(SaveRoomCheck), "save-check")]
[JsonDerivedType(typeof(SaveRoomCheckMetadata), "check-metadata")]
[JsonDerivedType(typeof(ReorderRoomCheck), "reorder-check")]
[JsonDerivedType(typeof(SetRoomCheckArchive), "check-archive")]
[JsonDerivedType(typeof(DeleteRoomCheck), "delete-check")]
[JsonDerivedType(typeof(ChangeRoomCheckAnnotation), "check-annotation")]
[JsonDerivedType(typeof(ApplyRoomSceneImage), "apply-scene-image")]
[JsonDerivedType(typeof(ClearRoomSceneBounds), "clear-scene-bounds")]
[JsonDerivedType(typeof(RecheckRoomRequirements), "recheck-requirements")]
public abstract record RoomEditOperation;

public sealed record SaveRoomHeader(RoomRowOrigin<RoomHeaderValues> Origin, RoomHeaderValues Submitted) : RoomEditOperation;
public sealed record SetRoomArchive(bool OriginalArchived, bool Archived) : RoomEditOperation;
public sealed record DeleteRoom(RoomRowObservation ConfirmedArchivedRoom) : RoomEditOperation;

public sealed record CreateRoomSubroom(Guid EntityId, RoomSubroomValues Submitted) : RoomEditOperation;
public sealed record SaveRoomSubroom(Guid EntityId, RoomRowOrigin<RoomSubroomValues> Origin,
    RoomSubroomValues Submitted) : RoomEditOperation;
public sealed record ReorderRoomSubroom(Guid EntityId, RoomOriginalPlacement Original, int TargetIndex) : RoomEditOperation;
public sealed record SetRoomSubroomArchive(Guid EntityId, bool OriginalArchived, bool Archived) : RoomEditOperation;
public sealed record DeleteRoomSubroom(RoomRowObservation ConfirmedArchivedRow) : RoomEditOperation;
public sealed record SaveRoomSubroomGeometry(Guid EntityId, RoomRowOrigin<RoomSubroomGeometry> Origin,
    RoomSubroomRectangle Submitted) : RoomEditOperation;
public enum RoomSubroomAnnotationAction { Clear, Show, Hide }
public sealed record CreateRoomExplanation(Guid EntityId, RoomExplanationValues Submitted) : RoomEditOperation;
public sealed record SaveRoomExplanation(Guid EntityId, RoomRowOrigin<RoomExplanationValues> Origin, RoomExplanationValues Submitted) : RoomEditOperation;
public sealed record ReorderRoomExplanation(Guid EntityId, RoomOriginalPlacement Original, int TargetIndex) : RoomEditOperation;
public sealed record SetRoomExplanationArchive(Guid EntityId, bool OriginalArchived, bool Archived) : RoomEditOperation;
public sealed record DeleteRoomExplanation(RoomRowObservation ConfirmedArchivedRow) : RoomEditOperation;
public sealed record ChangeRoomSubroomAnnotation(Guid EntityId, RoomRowOrigin<RoomSubroomGeometry> Origin,
    RoomSubroomAnnotationAction Action) : RoomEditOperation;

public sealed record CreateRoomTransition(Guid EntityId, RoomTransitionValues Submitted) : RoomEditOperation;
public sealed record SaveRoomTransition(Guid EntityId, RoomRowOrigin<RoomTransitionValues> Origin,
    RoomTransitionValues Submitted) : RoomEditOperation;
public sealed record RequestRoomTransitionInverse(Guid EntityId, RoomRowOrigin<RoomTransitionValues> Origin) : RoomEditOperation;
public sealed record SaveRoomTransitionMetadata(Guid EntityId, RoomRowOrigin<RoomInGameMetadata> Origin,
    RoomInGameMetadata Submitted) : RoomEditOperation;
public sealed record ReorderRoomTransition(Guid EntityId, RoomOriginalPlacement Original, int TargetIndex) : RoomEditOperation;
public sealed record SetRoomTransitionArchive(Guid EntityId, bool OriginalArchived, bool Archived) : RoomEditOperation;
public sealed record DeleteRoomTransition(RoomRowObservation ConfirmedArchivedRow) : RoomEditOperation;
public sealed record ChangeRoomTransitionAnnotation(Guid EntityId, RoomRowOrigin<RoomDirectAnnotation> Origin,
    RoomDirectAnnotationAction Action, RoomAnnotationPoint? Position = null) : RoomEditOperation;

public sealed record CreateRoomConnection(Guid EntityId, RoomConnectionValues Submitted) : RoomEditOperation;
public sealed record SaveRoomConnection(Guid EntityId, RoomRowOrigin<RoomConnectionValues> Origin,
    RoomConnectionValues Submitted) : RoomEditOperation;
public sealed record ScaffoldRoomConnectionInverse(Guid SourceEntityId, Guid NewEntityId,
    RoomRowOrigin<RoomConnectionValues> Origin) : RoomEditOperation;
public sealed record ReorderRoomConnection(Guid EntityId, RoomOriginalPlacement Original, int TargetIndex) : RoomEditOperation;
public sealed record SetRoomConnectionArchive(Guid EntityId, bool OriginalArchived, bool Archived) : RoomEditOperation;
public sealed record DeleteRoomConnection(RoomRowObservation ConfirmedArchivedRow) : RoomEditOperation;
public enum RoomConnectionAnnotationAction { Place, Move, Disable, Clear, Show, ShowAndSelect }
public sealed record ChangeRoomConnectionAnnotation(Guid EntityId, RoomConnectionAnnotationOrigin Origin,
    RoomConnectionAnnotationAction Action, RoomAnnotationPoint? Position = null) : RoomEditOperation;

public sealed record CreateRoomCheck(Guid EntityId, RoomCheckValues Submitted) : RoomEditOperation;
public sealed record SaveRoomCheck(Guid EntityId, RoomRowOrigin<RoomCheckValues> Origin,
    RoomCheckValues Submitted) : RoomEditOperation;
public sealed record SaveRoomCheckMetadata(Guid EntityId, RoomRowOrigin<RoomInGameMetadata> Origin,
    RoomInGameMetadata Submitted) : RoomEditOperation;
public sealed record ReorderRoomCheck(Guid EntityId, RoomOriginalPlacement Original, bool Enemies,
    int TargetIndex) : RoomEditOperation;
public sealed record SetRoomCheckArchive(Guid EntityId, bool OriginalArchived, bool Archived) : RoomEditOperation;
public sealed record DeleteRoomCheck(RoomRowObservation ConfirmedArchivedRow) : RoomEditOperation;
public sealed record ChangeRoomCheckAnnotation(Guid EntityId, RoomRowOrigin<RoomDirectAnnotation> Origin,
    RoomDirectAnnotationAction Action, RoomAnnotationPoint? Position = null) : RoomEditOperation;

public sealed record ApplyRoomSceneImage(Guid DraftOwnerId, long Revision, RoomSceneImageBaseline Baseline,
    RoomSceneImageSettings Settings, Guid? OwnedUploadAttemptId, SceneImageUploadPreparation? Preparation) : RoomEditOperation;
public sealed record ClearRoomSceneBounds(Guid DraftOwnerId, long Revision, RoomSceneImageBaseline Baseline) : RoomEditOperation;
public sealed record RecheckRoomRequirements : RoomEditOperation;

// Placement is captured evidence, not an expected-order rejection policy. Existing
// domain ordering interprets the target index and preserves the other check section.
public enum RoomOrderSection { Subrooms, Transitions, Connections, Checks, Enemies, Explanations }
public sealed record RoomOriginalPlacement(RoomOrderSection Section, int Index, ImmutableArray<Guid> Membership);
public sealed record RoomSubroomGeometry(double? X, double? Y, double? Width, double? Height, bool EnableAnnotation);
public sealed record RoomSubroomRectangle(double? X, double? Y, double? Width, double? Height);
public sealed record RoomAnnotationPoint(double X, double Y);
public enum RoomDirectAnnotationAction { Place, Move, Remove, Show, Clear, Reset, ShowAndSelect }
public sealed record RoomDirectAnnotation(double? X, double? Y, bool EnableAnnotation,
    double? InGamePositionX, double? InGamePositionY);
public sealed record RoomInGameMetadata(string? InGameId, double? InGamePositionX, double? InGamePositionY,
    double? InGamePositionZ, double? LocalPositionX, double? LocalPositionY, double? LocalPositionZ,
    double? AnnotationSceneUnitX, double? AnnotationSceneUnitY);
// Individual raw sibling facts must not be replaced with canonical effective state.
public sealed record RoomConnectionAnnotationMember(Guid EntityId, string Alias, int SortOrder,
    bool IsArchived, double? X, double? Y, bool EnableAnnotation, RoomRowObservation? LastConfirmed);
public sealed record RoomConnectionAnnotationOrigin(ImmutableArray<RoomConnectionAnnotationMember> Members,
    Guid CanonicalEntityId, ImmutableArray<Guid> PrecedingOperationIds);

public enum RoomSceneImageSource { Uploaded, ScriptersWar, None }
public sealed record RoomSceneImageSettings(double MinX, double MinY, double? MaxX, double? MaxY,
    double? PixelsPerUnitX, double? PixelsPerUnitY, double? PixelOffsetX, double? PixelOffsetY,
    RoomSceneImageSource Source);
// Server-observed exact CAS evidence, never client authority for file ownership.
public sealed record RoomSceneImageBaseline(double MinX, double MinY, double? MaxX, double? MaxY,
    double? PixelsPerUnitX, double? PixelsPerUnitY, double? PixelOffsetX, double? PixelOffsetY,
    bool UseSceneImage, bool UseUploadedSceneImage, bool Stale, string? OriginalHash,
    string? CropHash, DateTime UpdatedUtc);
