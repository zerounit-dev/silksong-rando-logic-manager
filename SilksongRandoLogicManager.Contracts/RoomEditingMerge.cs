using System.Collections.Immutable;

namespace SilksongRandoLogicManager.Contracts;

/// <summary>
/// R3's four-case editable-value merge. Call with freshly read values inside the
/// existing logical operation transaction, not as a substitute for conditional
/// writes or domain/proposal guards. Equality is exact: authored text is not
/// trimmed, normalized or resolved here. Later queued origins are never rebased.
/// </summary>
public static class RoomEditingMerge
{
    public static RoomValueMerge<RoomHeaderValues, RoomHeaderField> Header(
        RoomHeaderValues original, RoomHeaderValues submitted, RoomHeaderValues current,
        RoomMergeReview<RoomHeaderValues>? review = null)
    {
        var merge = new Fields<RoomHeaderField>(review?.Choice);
        var r = review?.Reviewed;
        var values = new RoomHeaderValues(
            merge.Value(RoomHeaderField.FriendlyName, original.FriendlyName, submitted.FriendlyName, current.FriendlyName, r?.FriendlyName ?? current.FriendlyName),
            merge.Value(RoomHeaderField.ReferenceId, original.ReferenceId, submitted.ReferenceId, current.ReferenceId, r?.ReferenceId ?? current.ReferenceId),
            merge.Value(RoomHeaderField.InGameId, original.InGameId, submitted.InGameId, current.InGameId, r?.InGameId),
            merge.Value(RoomHeaderField.Contributors, original.Contributors, submitted.Contributors, current.Contributors, r?.Contributors),
            merge.Value(RoomHeaderField.Comments, original.Comments, submitted.Comments, current.Comments, r?.Comments));
        return merge.Result(values);
    }

    public static RoomValueMerge<RoomSubroomValues, RoomSubroomField> Subroom(
        RoomSubroomValues original, RoomSubroomValues submitted, RoomSubroomValues current,
        RoomMergeReview<RoomSubroomValues>? review = null)
    {
        var merge = new Fields<RoomSubroomField>(review?.Choice);
        var r = review?.Reviewed;
        var values = new RoomSubroomValues(
            merge.Value(RoomSubroomField.FriendlyName, original.FriendlyName, submitted.FriendlyName, current.FriendlyName, r?.FriendlyName ?? current.FriendlyName),
            merge.Value(RoomSubroomField.ReferenceId, original.ReferenceId, submitted.ReferenceId, current.ReferenceId, r?.ReferenceId ?? current.ReferenceId),
            merge.Value(RoomSubroomField.Notes, original.Notes, submitted.Notes, current.Notes, r?.Notes));
        return merge.Result(values);
    }

    public static RoomValueMerge<RoomExplanationValues, RoomExplanationField> Explanation(
        RoomExplanationValues original, RoomExplanationValues submitted, RoomExplanationValues current,
        RoomMergeReview<RoomExplanationValues>? review = null)
    {
        var merge = new Fields<RoomExplanationField>(review?.Choice);
        var r = review?.Reviewed;
        return merge.Result(new RoomExplanationValues(
            merge.Value(RoomExplanationField.FriendlyName, original.FriendlyName, submitted.FriendlyName, current.FriendlyName, r?.FriendlyName ?? current.FriendlyName),
            merge.Value(RoomExplanationField.ReferenceId, original.ReferenceId, submitted.ReferenceId, current.ReferenceId, r?.ReferenceId ?? current.ReferenceId),
            merge.Value(RoomExplanationField.Link, original.Link, submitted.Link, current.Link, r?.Link ?? current.Link),
            merge.Value(RoomExplanationField.Notes, original.Notes, submitted.Notes, current.Notes, r?.Notes ?? current.Notes)));
    }

    public static RoomValueMerge<RoomTransitionValues, RoomTransitionField> Transition(
        RoomTransitionValues original, RoomTransitionValues submitted, RoomTransitionValues current,
        RoomMergeReview<RoomTransitionValues>? review = null)
    {
        var merge = new Fields<RoomTransitionField>(review?.Choice);
        var r = review?.Reviewed;
        var values = new RoomTransitionValues(
            merge.Value(RoomTransitionField.Alias, original.Alias, submitted.Alias, current.Alias, r?.Alias ?? current.Alias),
            merge.Value(RoomTransitionField.FriendlyName, original.FriendlyName, submitted.FriendlyName, current.FriendlyName, r?.FriendlyName ?? current.FriendlyName),
            merge.Value(RoomTransitionField.SourceSubroomReferenceText, original.SourceSubroomReferenceText, submitted.SourceSubroomReferenceText, current.SourceSubroomReferenceText, r?.SourceSubroomReferenceText),
            merge.Value(RoomTransitionField.DestinationRoomReferenceText, original.DestinationRoomReferenceText, submitted.DestinationRoomReferenceText, current.DestinationRoomReferenceText, r?.DestinationRoomReferenceText),
            merge.Value(RoomTransitionField.DestinationTransitionAliasText, original.DestinationTransitionAliasText, submitted.DestinationTransitionAliasText, current.DestinationTransitionAliasText, r?.DestinationTransitionAliasText),
            merge.Value(RoomTransitionField.Requirements, original.Requirements, submitted.Requirements, current.Requirements, r?.Requirements ?? current.Requirements),
            merge.Value(RoomTransitionField.Notes, original.Notes, submitted.Notes, current.Notes, r?.Notes ?? current.Notes),
            merge.Value(RoomTransitionField.IsTodo, original.IsTodo, submitted.IsTodo, current.IsTodo, r?.IsTodo ?? default),
            merge.Value(RoomTransitionField.IsVerified, original.IsVerified, submitted.IsVerified, current.IsVerified, r?.IsVerified),
            merge.Value(RoomTransitionField.IsUnsafeArrival, original.IsUnsafeArrival, submitted.IsUnsafeArrival, current.IsUnsafeArrival, r?.IsUnsafeArrival ?? default));
        return merge.Result(values);
    }

    public static RoomValueMerge<RoomConnectionValues, RoomConnectionField> Connection(
        RoomConnectionValues original, RoomConnectionValues submitted, RoomConnectionValues current,
        RoomMergeReview<RoomConnectionValues>? review = null)
    {
        var merge = new Fields<RoomConnectionField>(review?.Choice);
        var r = review?.Reviewed;
        var values = new RoomConnectionValues(
            merge.Value(RoomConnectionField.Alias, original.Alias, submitted.Alias, current.Alias, r?.Alias ?? current.Alias),
            merge.Value(RoomConnectionField.FriendlyName, original.FriendlyName, submitted.FriendlyName, current.FriendlyName, r?.FriendlyName ?? current.FriendlyName),
            merge.Value(RoomConnectionField.SourceSubroomReferenceText, original.SourceSubroomReferenceText, submitted.SourceSubroomReferenceText, current.SourceSubroomReferenceText, r?.SourceSubroomReferenceText ?? current.SourceSubroomReferenceText),
            merge.Value(RoomConnectionField.DestinationSubroomReferenceText, original.DestinationSubroomReferenceText, submitted.DestinationSubroomReferenceText, current.DestinationSubroomReferenceText, r?.DestinationSubroomReferenceText ?? current.DestinationSubroomReferenceText),
            merge.Value(RoomConnectionField.Requirements, original.Requirements, submitted.Requirements, current.Requirements, r?.Requirements ?? current.Requirements),
            merge.Value(RoomConnectionField.Notes, original.Notes, submitted.Notes, current.Notes, r?.Notes ?? current.Notes),
            merge.Value(RoomConnectionField.IsTodo, original.IsTodo, submitted.IsTodo, current.IsTodo, r?.IsTodo ?? default),
            merge.Value(RoomConnectionField.IsVerified, original.IsVerified, submitted.IsVerified, current.IsVerified, r?.IsVerified));
        return merge.Result(values);
    }

    public static RoomValueMerge<RoomCheckValues, RoomCheckField> Check(
        RoomCheckValues original, RoomCheckValues submitted, RoomCheckValues current,
        RoomMergeReview<RoomCheckValues>? review = null)
    {
        var merge = new Fields<RoomCheckField>(review?.Choice);
        var r = review?.Reviewed;
        var values = new RoomCheckValues(
            merge.Value(RoomCheckField.FriendlyName, original.FriendlyName, submitted.FriendlyName, current.FriendlyName, r?.FriendlyName ?? current.FriendlyName),
            merge.Value(RoomCheckField.SubroomReferenceText, original.SubroomReferenceText, submitted.SubroomReferenceText, current.SubroomReferenceText, r?.SubroomReferenceText),
            merge.Value(RoomCheckField.Requirements, original.Requirements, submitted.Requirements, current.Requirements, r?.Requirements ?? current.Requirements),
            merge.Value(RoomCheckField.Notes, original.Notes, submitted.Notes, current.Notes, r?.Notes ?? current.Notes),
            merge.Value(RoomCheckField.LocationType, original.LocationType, submitted.LocationType, current.LocationType, r?.LocationType),
            merge.Value(RoomCheckField.IsTodo, original.IsTodo, submitted.IsTodo, current.IsTodo, r?.IsTodo ?? default),
            merge.Value(RoomCheckField.IsVerified, original.IsVerified, submitted.IsVerified, current.IsVerified, r?.IsVerified));
        return merge.Result(values);
    }

    public static RoomValueMerge<RoomInGameMetadata, RoomMetadataField> Metadata(
        RoomInGameMetadata original, RoomInGameMetadata submitted, RoomInGameMetadata current,
        RoomMergeReview<RoomInGameMetadata>? review = null)
    {
        var merge = new Fields<RoomMetadataField>(review?.Choice);
        var r = review?.Reviewed;
        var values = new RoomInGameMetadata(
            merge.Value(RoomMetadataField.InGameId, original.InGameId, submitted.InGameId, current.InGameId, r?.InGameId),
            merge.Value(RoomMetadataField.InGamePositionX, original.InGamePositionX, submitted.InGamePositionX, current.InGamePositionX, r?.InGamePositionX),
            merge.Value(RoomMetadataField.InGamePositionY, original.InGamePositionY, submitted.InGamePositionY, current.InGamePositionY, r?.InGamePositionY),
            merge.Value(RoomMetadataField.InGamePositionZ, original.InGamePositionZ, submitted.InGamePositionZ, current.InGamePositionZ, r?.InGamePositionZ),
            merge.Value(RoomMetadataField.LocalPositionX, original.LocalPositionX, submitted.LocalPositionX, current.LocalPositionX, r?.LocalPositionX),
            merge.Value(RoomMetadataField.LocalPositionY, original.LocalPositionY, submitted.LocalPositionY, current.LocalPositionY, r?.LocalPositionY),
            merge.Value(RoomMetadataField.LocalPositionZ, original.LocalPositionZ, submitted.LocalPositionZ, current.LocalPositionZ, r?.LocalPositionZ),
            merge.Value(RoomMetadataField.AnnotationSceneUnitX, original.AnnotationSceneUnitX, submitted.AnnotationSceneUnitX, current.AnnotationSceneUnitX, r?.AnnotationSceneUnitX),
            merge.Value(RoomMetadataField.AnnotationSceneUnitY, original.AnnotationSceneUnitY, submitted.AnnotationSceneUnitY, current.AnnotationSceneUnitY, r?.AnnotationSceneUnitY));
        return merge.Result(values);
    }

    public static RoomValueMerge<RoomSubroomRectangle, RoomGeometryField> Geometry(
        RoomSubroomGeometry original, RoomSubroomRectangle submitted, RoomSubroomGeometry current,
        RoomMergeReview<RoomSubroomGeometry>? review = null)
    {
        var merge = new Fields<RoomGeometryField>(review?.Choice);
        var r = review?.Reviewed;
        var values = new RoomSubroomRectangle(
            merge.Value(RoomGeometryField.X, original.X, submitted.X, current.X, r?.X),
            merge.Value(RoomGeometryField.Y, original.Y, submitted.Y, current.Y, r?.Y),
            merge.Value(RoomGeometryField.Width, original.Width, submitted.Width, current.Width, r?.Width),
            merge.Value(RoomGeometryField.Height, original.Height, submitted.Height, current.Height, r?.Height));
        // Visibility is deliberately neither overwritten nor conflict-reviewed by
        // a completed rectangle gesture; it belongs to its distinct typed action.
        return merge.Result(values);
    }

    // This helper owns no entity, renderer, field lookup, resolver or persistence.
    // Each public family above supplies its explicit closed editable allowlist.
    private sealed class Fields<TField>(RoomConflictChoice? choice) where TField : struct, Enum
    {
        private readonly ImmutableArray<TField>.Builder divergent = ImmutableArray.CreateBuilder<TField>();
        private bool superseded;

        public T Value<T>(TField field, T original, T submitted, T current, T reviewed)
        {
            var equality = EqualityComparer<T>.Default;
            if (equality.Equals(original, submitted)) return current;

            // A review can authorize only the relevant current values it showed.
            // An unrelated field changed by another writer is deliberately ignored.
            if (choice.HasValue && !equality.Equals(current, reviewed)) superseded = true;
            if (equality.Equals(current, submitted)) return current;
            if (equality.Equals(current, original)) return submitted;

            if (!choice.HasValue)
            {
                divergent.Add(field);
                return current;
            }
            return choice == RoomConflictChoice.KeepServer ? current : submitted;
        }

        public RoomValueMerge<TValues, TField> Result<TValues>(TValues values) =>
            new(values, divergent.ToImmutable(), superseded);
    }
}
