using System.Collections.Immutable;
using System.Text.Json;

namespace SilksongRandoLogicManager.Contracts;

/// <summary>Transport shape and normalized typed identity only; never authored-data validation.</summary>
public static class RoomEditingIdentity
{
    // Normalize DESERIALIZED closed typed records before comparison. Incoming JSON
    // member order/whitespace is not identity; collection order and raw text are.
    // DeepEquals compares JSON numbers by value rather than textual formatting.
    public static bool SameOperation(RoomEditOperation left, RoomEditOperation right) => Same(left, right);
    public static bool SameBatch(RoomBatchRequest left, RoomBatchRequest right) => Same(left, right);
    public static bool SameResolution(RoomResolveRequest left, RoomResolveRequest right) => Same(left, right);
    private static bool Same<T>(T left, T right) => JsonElement.DeepEquals(
        JsonSerializer.SerializeToElement(left), JsonSerializer.SerializeToElement(right));

    public static ImmutableArray<Guid> Dependencies(RoomEditOperation operation) => operation switch
    {
        SaveRoomHeader x => x.Origin.PrecedingOperationIds,
        SaveRoomSubroom x => x.Origin.PrecedingOperationIds,
        SaveRoomSubroomGeometry x => x.Origin.PrecedingOperationIds,
        ChangeRoomSubroomAnnotation x => x.Origin.PrecedingOperationIds,
        SaveRoomTransition x => x.Origin.PrecedingOperationIds,
        RequestRoomTransitionInverse x => x.Origin.PrecedingOperationIds,
        SaveRoomTransitionMetadata x => x.Origin.PrecedingOperationIds,
        ChangeRoomTransitionAnnotation x => x.Origin.PrecedingOperationIds,
        SaveRoomConnection x => x.Origin.PrecedingOperationIds,
        ScaffoldRoomConnectionInverse x => x.Origin.PrecedingOperationIds,
        ChangeRoomConnectionAnnotation x => x.Origin.PrecedingOperationIds,
        SaveRoomCheck x => x.Origin.PrecedingOperationIds,
        SaveRoomCheckMetadata x => x.Origin.PrecedingOperationIds,
        ChangeRoomCheckAnnotation x => x.Origin.PrecedingOperationIds,
        _ => []
    };

    public static bool ValidOwner(RoomTrackingOwner? owner) => owner is not null
        && owner.TabId != Guid.Empty && owner.VisitId != Guid.Empty;
    public static bool ValidObservation(RoomDocumentObservation? value, Guid roomId) => value is not null
        && value.RoomId == roomId && Utc(value.CacheFlagUtc);

    public static bool ValidOperation(RoomEditOperation? operation, Guid roomId) => operation switch
    {
        SaveRoomHeader x => Origin(x.Origin, roomId, Header) && Header(x.Submitted),
        SetRoomArchive => true,
        DeleteRoom x => Observation(x.ConfirmedArchivedRoom, roomId),
        CreateRoomSubroom x => Id(x.EntityId) && Subroom(x.Submitted),
        SaveRoomSubroom x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Subroom) && Subroom(x.Submitted),
        ReorderRoomSubroom x => Id(x.EntityId) && Placement(x.Original, RoomOrderSection.Subrooms),
        SetRoomSubroomArchive x => Id(x.EntityId),
        DeleteRoomSubroom x => Observation(x.ConfirmedArchivedRow),
        SaveRoomSubroomGeometry x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Geometry)
            && x.Submitted is not null && Finite(x.Submitted.X, x.Submitted.Y, x.Submitted.Width, x.Submitted.Height),
        ChangeRoomSubroomAnnotation x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Geometry) && Enum.IsDefined(x.Action),
        CreateRoomTransition x => Id(x.EntityId) && Transition(x.Submitted),
        SaveRoomTransition x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Transition) && Transition(x.Submitted),
        RequestRoomTransitionInverse x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Transition),
        SaveRoomTransitionMetadata x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Metadata) && Metadata(x.Submitted),
        ReorderRoomTransition x => Id(x.EntityId) && Placement(x.Original, RoomOrderSection.Transitions),
        SetRoomTransitionArchive x => Id(x.EntityId),
        DeleteRoomTransition x => Observation(x.ConfirmedArchivedRow),
        ChangeRoomTransitionAnnotation x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Annotation) && DirectAction(x.Action, x.Position),
        CreateRoomConnection x => Id(x.EntityId) && Connection(x.Submitted),
        SaveRoomConnection x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Connection) && Connection(x.Submitted),
        ScaffoldRoomConnectionInverse x => Id(x.SourceEntityId) && Id(x.NewEntityId)
            && Origin(x.Origin, x.SourceEntityId, Connection),
        ReorderRoomConnection x => Id(x.EntityId) && Placement(x.Original, RoomOrderSection.Connections),
        SetRoomConnectionArchive x => Id(x.EntityId),
        DeleteRoomConnection x => Observation(x.ConfirmedArchivedRow),
        ChangeRoomConnectionAnnotation x => Id(x.EntityId) && Group(x.Origin) && Enum.IsDefined(x.Action)
            && (x.Action is RoomConnectionAnnotationAction.Place or RoomConnectionAnnotationAction.Move
                ? Point(x.Position) : x.Position is null),
        CreateRoomCheck x => Id(x.EntityId) && Check(x.Submitted),
        SaveRoomCheck x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Check) && Check(x.Submitted),
        SaveRoomCheckMetadata x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Metadata) && Metadata(x.Submitted),
        ReorderRoomCheck x => Id(x.EntityId) && x.Original is not null
            && (Placement(x.Original, RoomOrderSection.Checks) || Placement(x.Original, RoomOrderSection.Enemies)),
        SetRoomCheckArchive x => Id(x.EntityId),
        DeleteRoomCheck x => Observation(x.ConfirmedArchivedRow),
        ChangeRoomCheckAnnotation x => Id(x.EntityId) && Origin(x.Origin, x.EntityId, Annotation) && DirectAction(x.Action, x.Position),
        ApplyRoomSceneImage x => Id(x.DraftOwnerId) && x.Revision >= 0 && ImageBaseline(x.Baseline)
            && ImageSettings(x.Settings) && (x.OwnedUploadAttemptId is null || Id(x.OwnedUploadAttemptId.Value)),
        ClearRoomSceneBounds x => Id(x.DraftOwnerId) && x.Revision >= 0 && ImageBaseline(x.Baseline),
        RecheckRoomRequirements => true,
        _ => false
    };

    public static bool ValidResolution(RoomHeadResolution? resolution) => resolution switch
    {
        ChooseRoomConflictValues x => Enum.IsDefined(x.Choice),
        ChooseRoomProposal x => Enum.IsDefined(x.Choice),
        PrepareRoomProposalAgain => true,
        CorrectRoomSceneImageApply x => Id(x.DraftOwnerId) && x.Revision >= 0
            && ImageBaseline(x.ReviewedBaseline) && ImageSettings(x.Settings)
            && (x.OwnedUploadAttemptId is null || Id(x.OwnedUploadAttemptId.Value)),
        _ => false
    };

    private static bool Origin<T>(RoomRowOrigin<T>? origin, Guid entityId, Func<T?, bool> valid) where T : class =>
        origin is not null && Enum.IsDefined(origin.Kind) && valid(origin.Original)
        && Ids(origin.PrecedingOperationIds)
        && (origin.LastConfirmed is null || Observation(origin.LastConfirmed, entityId))
        && (origin.Kind == RoomOriginKind.Observed
            ? origin.LastConfirmed is not null && origin.PrecedingOperationIds.IsEmpty
            : !origin.PrecedingOperationIds.IsEmpty);

    private static bool Header(RoomHeaderValues? x) => x is not null && x.FriendlyName is not null && x.ReferenceId is not null;
    private static bool Subroom(RoomSubroomValues? x) => x is not null && x.FriendlyName is not null && x.ReferenceId is not null;
    private static bool Transition(RoomTransitionValues? x) => x is not null && x.Alias is not null
        && x.FriendlyName is not null && x.Requirements is not null && x.Notes is not null;
    private static bool Connection(RoomConnectionValues? x) => x is not null && x.Alias is not null
        && x.FriendlyName is not null && x.SourceSubroomReferenceText is not null
        && x.DestinationSubroomReferenceText is not null && x.Requirements is not null && x.Notes is not null;
    private static bool Check(RoomCheckValues? x) => x is not null && x.FriendlyName is not null && x.Requirements is not null && x.Notes is not null;
    private static bool Geometry(RoomSubroomGeometry? x) => x is not null && Finite(x.X, x.Y, x.Width, x.Height);
    private static bool Metadata(RoomInGameMetadata? x) => x is not null && Finite(x.InGamePositionX, x.InGamePositionY,
        x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY);
    private static bool Annotation(RoomDirectAnnotation? x) => x is not null && Finite(x.X, x.Y, x.InGamePositionX, x.InGamePositionY);
    private static bool DirectAction(RoomDirectAnnotationAction action, RoomAnnotationPoint? position) => Enum.IsDefined(action)
        && (action is RoomDirectAnnotationAction.Place or RoomDirectAnnotationAction.Move ? Point(position) : position is null);
    private static bool Point(RoomAnnotationPoint? x) => x is not null && double.IsFinite(x.X) && double.IsFinite(x.Y);
    private static bool Placement(RoomOriginalPlacement? x, RoomOrderSection section) => x is not null
        && x.Section == section && x.Index >= 0 && Ids(x.Membership);
    private static bool Group(RoomConnectionAnnotationOrigin? x) => x is not null && Id(x.CanonicalEntityId)
        && !x.Members.IsDefaultOrEmpty && Ids(x.PrecedingOperationIds)
        && x.Members.All(m => m is not null && Id(m.EntityId) && m.Alias is not null && Finite(m.X, m.Y)
            && (m.LastConfirmed is null || Observation(m.LastConfirmed, m.EntityId)))
        && x.Members.Select(m => m.EntityId).Distinct().Count() == x.Members.Length
        && x.Members.Any(m => m.EntityId == x.CanonicalEntityId);
    private static bool ImageBaseline(RoomSceneImageBaseline? x) => x is not null && Utc(x.UpdatedUtc)
        && Finite(x.MinX, x.MinY, x.MaxX, x.MaxY, x.PixelsPerUnitX, x.PixelsPerUnitY, x.PixelOffsetX, x.PixelOffsetY);
    private static bool ImageSettings(RoomSceneImageSettings? x) => x is not null && Enum.IsDefined(x.Source)
        && Finite(x.MinX, x.MinY, x.MaxX, x.MaxY, x.PixelsPerUnitX, x.PixelsPerUnitY, x.PixelOffsetX, x.PixelOffsetY);
    private static bool Observation(RoomRowObservation? x, Guid? id = null) => x is not null
        && Id(x.EntityId) && (id is null || x.EntityId == id) && Utc(x.UpdatedUtc);
    private static bool Id(Guid id) => id != Guid.Empty;
    private static bool Utc(DateTime time) => time.Kind == DateTimeKind.Utc && time.Ticks > 0;
    private static bool Ids(ImmutableArray<Guid> ids) => !ids.IsDefault && ids.All(Id) && ids.Distinct().Count() == ids.Length;
    private static bool Finite(params double?[] values) => values.All(x => x is null || double.IsFinite(x.Value));
}
