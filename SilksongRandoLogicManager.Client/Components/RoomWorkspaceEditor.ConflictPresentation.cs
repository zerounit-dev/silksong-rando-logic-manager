using System.Collections.Immutable;
using System.Globalization;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    // Presentation only: read the typed retained evidence, never a parsed or
    // projected Requirements value. Field order follows the authoring record.
    private static ConflictField Field<T>(string label, T server, T submitted, bool divergent) =>
        new(label, Content(server), Content(submitted), divergent, !EqualityComparer<T>.Default.Equals(server, submitted));

    private static ConflictField Field<T, TField>(string label, T server, T submitted,
        ImmutableArray<TField> divergent, TField field) where TField : struct, Enum =>
        Field(label, server, submitted, divergent.Contains(field));

    private static ConflictContent Content<T>(T value)
    {
        if (typeof(T) == typeof(bool?)) return new(ConflictContentKind.Verification, Flag: (bool?)(object?)value);
        return value switch
        {
            string text => new(ConflictContentKind.Text, text),
            bool flag => new(ConflictContentKind.Toggle, Flag: flag),
            double number => new(ConflictContentKind.Number, number.ToString("R", CultureInfo.InvariantCulture)),
            null => new(ConflictContentKind.Text),
            _ => throw new InvalidOperationException("Unsupported typed conflict presentation value.")
        };
    }

    private static IReadOnlyList<ConflictField> Review(RoomHeaderValues s, RoomHeaderValues u, ImmutableArray<RoomHeaderField> d) =>
    [
        Field("Name", s.FriendlyName, u.FriendlyName, d, RoomHeaderField.FriendlyName),
        Field("Reference ID", s.ReferenceId, u.ReferenceId, d, RoomHeaderField.ReferenceId),
        Field("Game ID", s.InGameId, u.InGameId, d, RoomHeaderField.InGameId),
        Field("Contributors", s.Contributors, u.Contributors, d, RoomHeaderField.Contributors),
        Field("Comments", s.Comments, u.Comments, d, RoomHeaderField.Comments)
    ];

    private static IReadOnlyList<ConflictField> Review(RoomSubroomValues s, RoomSubroomValues u, ImmutableArray<RoomSubroomField> d) =>
    [
        Field("Name", s.FriendlyName, u.FriendlyName, d, RoomSubroomField.FriendlyName),
        Field("Reference ID", s.ReferenceId, u.ReferenceId, d, RoomSubroomField.ReferenceId),
        Field("Notes", s.Notes, u.Notes, d, RoomSubroomField.Notes)
    ];

    private static IReadOnlyList<ConflictField> Review(RoomTransitionValues s, RoomTransitionValues u, ImmutableArray<RoomTransitionField> d) =>
    [
        Field("Alias", s.Alias, u.Alias, d, RoomTransitionField.Alias),
        Field("Name", s.FriendlyName, u.FriendlyName, d, RoomTransitionField.FriendlyName),
        Field("From subroom", s.SourceSubroomReferenceText, u.SourceSubroomReferenceText, d, RoomTransitionField.SourceSubroomReferenceText),
        Field("Destination room", s.DestinationRoomReferenceText, u.DestinationRoomReferenceText, d, RoomTransitionField.DestinationRoomReferenceText),
        Field("Destination alias", s.DestinationTransitionAliasText, u.DestinationTransitionAliasText, d, RoomTransitionField.DestinationTransitionAliasText),
        Field("Unsafe arrival", s.IsUnsafeArrival, u.IsUnsafeArrival, d, RoomTransitionField.IsUnsafeArrival),
        Field("Requirements", s.Requirements, u.Requirements, d, RoomTransitionField.Requirements),
        Field("Notes", s.Notes, u.Notes, d, RoomTransitionField.Notes),
        Field("TODO", s.IsTodo, u.IsTodo, d, RoomTransitionField.IsTodo),
        Field("Verification", s.IsVerified, u.IsVerified, d, RoomTransitionField.IsVerified)
    ];

    private static IReadOnlyList<ConflictField> Review(RoomConnectionValues s, RoomConnectionValues u, ImmutableArray<RoomConnectionField> d) =>
    [
        Field("Alias", s.Alias, u.Alias, d, RoomConnectionField.Alias),
        Field("Name", s.FriendlyName, u.FriendlyName, d, RoomConnectionField.FriendlyName),
        Field("From subroom", s.SourceSubroomReferenceText, u.SourceSubroomReferenceText, d, RoomConnectionField.SourceSubroomReferenceText),
        Field("To subroom", s.DestinationSubroomReferenceText, u.DestinationSubroomReferenceText, d, RoomConnectionField.DestinationSubroomReferenceText),
        Field("Requirements", s.Requirements, u.Requirements, d, RoomConnectionField.Requirements),
        Field("Notes", s.Notes, u.Notes, d, RoomConnectionField.Notes),
        Field("TODO", s.IsTodo, u.IsTodo, d, RoomConnectionField.IsTodo),
        Field("Verification", s.IsVerified, u.IsVerified, d, RoomConnectionField.IsVerified)
    ];

    private static IReadOnlyList<ConflictField> Review(RoomCheckValues s, RoomCheckValues u, ImmutableArray<RoomCheckField> d) =>
    [
        Field("Type", s.LocationType, u.LocationType, d, RoomCheckField.LocationType),
        Field("Name", s.FriendlyName, u.FriendlyName, d, RoomCheckField.FriendlyName),
        Field("Subroom", s.SubroomReferenceText, u.SubroomReferenceText, d, RoomCheckField.SubroomReferenceText),
        Field("Requirements", s.Requirements, u.Requirements, d, RoomCheckField.Requirements),
        Field("Notes", s.Notes, u.Notes, d, RoomCheckField.Notes),
        Field("TODO", s.IsTodo, u.IsTodo, d, RoomCheckField.IsTodo),
        Field("Verification", s.IsVerified, u.IsVerified, d, RoomCheckField.IsVerified)
    ];

    private static IReadOnlyList<ConflictField> Review(RoomInGameMetadata s, RoomInGameMetadata u, ImmutableArray<RoomMetadataField> d) =>
    [
        Field("Game ID", s.InGameId, u.InGameId, d, RoomMetadataField.InGameId),
        Field("In-game X", s.InGamePositionX, u.InGamePositionX, d, RoomMetadataField.InGamePositionX),
        Field("In-game Y", s.InGamePositionY, u.InGamePositionY, d, RoomMetadataField.InGamePositionY),
        Field("In-game Z", s.InGamePositionZ, u.InGamePositionZ, d, RoomMetadataField.InGamePositionZ),
        Field("Local X", s.LocalPositionX, u.LocalPositionX, d, RoomMetadataField.LocalPositionX),
        Field("Local Y", s.LocalPositionY, u.LocalPositionY, d, RoomMetadataField.LocalPositionY),
        Field("Local Z", s.LocalPositionZ, u.LocalPositionZ, d, RoomMetadataField.LocalPositionZ),
        Field("Annotation X", s.AnnotationSceneUnitX, u.AnnotationSceneUnitX, d, RoomMetadataField.AnnotationSceneUnitX),
        Field("Annotation Y", s.AnnotationSceneUnitY, u.AnnotationSceneUnitY, d, RoomMetadataField.AnnotationSceneUnitY)
    ];

    private static IReadOnlyList<ConflictField> Review(RoomSubroomGeometry s, RoomSubroomRectangle u, ImmutableArray<RoomGeometryField> d) =>
    [
        Field("X", s.X, u.X, d, RoomGeometryField.X),
        Field("Y", s.Y, u.Y, d, RoomGeometryField.Y),
        Field("Width", s.Width, u.Width, d, RoomGeometryField.Width),
        Field("Height", s.Height, u.Height, d, RoomGeometryField.Height)
    ];
}
