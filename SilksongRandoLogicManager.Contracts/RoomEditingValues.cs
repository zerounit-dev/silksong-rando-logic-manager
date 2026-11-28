using System.Collections.Immutable;

namespace SilksongRandoLogicManager.Contracts;

// R3 ordinary authoring allowlists. Metadata, geometry, lifecycle, resolver,
// validation and ordering facts deliberately cannot enter an ordinary row save.
public sealed record RoomHeaderValues(string FriendlyName, string ReferenceId, string? InGameId,
    string? Contributors, string? Comments);
public sealed record RoomSubroomValues(string FriendlyName, string ReferenceId, string? Notes);
public sealed record RoomExplanationValues(string FriendlyName, string ReferenceId, string Link, string Notes);
public sealed record RoomTransitionValues(string Alias, string FriendlyName, string? SourceSubroomReferenceText,
    string? DestinationRoomReferenceText, string? DestinationTransitionAliasText, string Requirements,
    string Notes, bool IsTodo, bool? IsVerified, bool IsUnsafeArrival);
public sealed record RoomConnectionValues(string Alias, string FriendlyName, string SourceSubroomReferenceText,
    string DestinationSubroomReferenceText, string Requirements, string Notes, bool IsTodo, bool? IsVerified);
public sealed record RoomCheckValues(string FriendlyName, string? SubroomReferenceText, string Requirements,
    string Notes, string? LocationType, bool IsTodo, bool? IsVerified);

public enum RoomHeaderField { FriendlyName, ReferenceId, InGameId, Contributors, Comments }
public enum RoomSubroomField { FriendlyName, ReferenceId, Notes }
public enum RoomExplanationField { FriendlyName, ReferenceId, Link, Notes }
public enum RoomTransitionField
{
    Alias, FriendlyName, SourceSubroomReferenceText, DestinationRoomReferenceText,
    DestinationTransitionAliasText, Requirements, Notes, IsTodo, IsVerified, IsUnsafeArrival
}
public enum RoomConnectionField
{
    Alias, FriendlyName, SourceSubroomReferenceText, DestinationSubroomReferenceText,
    Requirements, Notes, IsTodo, IsVerified
}
public enum RoomCheckField { FriendlyName, SubroomReferenceText, Requirements, Notes, LocationType, IsTodo, IsVerified }
public enum RoomMetadataField
{
    InGameId, InGamePositionX, InGamePositionY, InGamePositionZ,
    LocalPositionX, LocalPositionY, LocalPositionZ, AnnotationSceneUnitX, AnnotationSceneUnitY
}
public enum RoomGeometryField { X, Y, Width, Height, EnableAnnotation }

/// <summary>
/// An observation is evidence, not a whole-row timestamp veto. Projected originals
/// retain the last confirmed observation (if any) and preceding same-tracker IDs;
/// they never invent a durable timestamp for a row that has not been created.
/// </summary>
public sealed record RoomRowObservation(Guid EntityId, DateTime UpdatedUtc, bool IsArchived);
public enum RoomOriginKind { Observed, Projected }
public sealed record RoomRowOrigin<TValues>(RoomOriginKind Kind, TValues Original,
    RoomRowObservation? LastConfirmed, ImmutableArray<Guid> PrecedingOperationIds);

public enum RoomConflictChoice { KeepServer, KeepSubmitted }
/// <summary>The reviewed current values are distinct from the immutable original operation.</summary>
public sealed record RoomMergeReview<TValues>(TValues Reviewed, RoomConflictChoice Choice);
/// <summary>
/// Pure fieldwise guidance for the application transaction. A value is publishable
/// only when both gates are clear; existence/ownership/domain guards remain the
/// command's responsibility. This is never persisted validation status.
/// </summary>
public sealed record RoomValueMerge<TValues, TField>(TValues Values,
    ImmutableArray<TField> DivergentFields, bool ReviewSuperseded)
    where TField : struct, Enum
{
    public bool CanApply => !ReviewSuperseded && DivergentFields.IsEmpty;
}
