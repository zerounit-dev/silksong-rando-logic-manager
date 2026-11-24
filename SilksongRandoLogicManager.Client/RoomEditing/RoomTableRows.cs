using System.Collections.Immutable;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.RoomEditing;

public enum RoomRowAction { Geometry, Metadata, Annotation, Archive, Restore, Delete, Inverse, FollowInverse, History }
public sealed record RoomRowActionIntent(RoomOrderSection Section, Guid ClientId, RoomRowAction Action);
public sealed record RoomNativeField(string Kind, string Client, string Field, string Value, long Generation, bool Composing);
public enum RoomTableKind { Subroom, Transition, Connection, Check }
public enum RoomNativeTrigger { TextBlur, ControlBlur, BoundaryArrow, ControlChange }
public enum RoomNativeDirection { Left, Right, Up, Down }
public enum RoomNativeColumn { Name, Reference, Notes, Alias, Source, DestinationRoom, DestinationAlias, UnsafeArrival, Requirements, Todo, Verification, Destination, Type, Subroom }
public abstract record RoomTableBoundaryIntent(RoomTableKind Kind, Guid ClientId, ImmutableArray<RoomNativeField> Fields, long Interaction);
public sealed record RoomRowBoundaryIntent(RoomTableKind Kind, Guid ClientId, ImmutableArray<RoomNativeField> Fields, long Interaction,
    RoomNativeTrigger Trigger, RoomNativeColumn Column, RoomNativeDirection? Direction, string? FocusTarget)
    : RoomTableBoundaryIntent(Kind, ClientId, Fields, Interaction);
public sealed record RoomDropBoundaryIntent(RoomTableKind Kind, Guid ClientId, ImmutableArray<RoomNativeField> Fields, long Interaction,
    RoomOrderSection Section, int TargetIndex) : RoomTableBoundaryIntent(Kind, ClientId, Fields, Interaction);
public sealed record RoomCheckSectionRows(IReadOnlyList<RoomCheckRow> Active, IReadOnlyList<RoomCheckRow> Archived);

// Presentation identity is allocated before creation and is never replaced by a
// result. Initial native values remain fixed for that identity: Blazor may update
// diagnostics/actions, but only the owned generation handoff writes native values.
public abstract class RoomTableRow(Guid clientId)
{
    public Guid ClientId { get; } = clientId;
    public Guid? EntityId { get; set; }
    public RoomRowObservation? LastConfirmed { get; set; }
    public bool IsArchived { get; set; }
    public bool Pending { get; set; }
    public bool Attention { get; set; }
    public ImmutableDictionary<string, string> FieldClasses { get; set; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> FieldTitles { get; set; } = ImmutableDictionary<string, string>.Empty;
    public string? AnnotationClass { get; set; }
    public string AnnotationTitle { get; set; } = "re-arm placement";
    public string AnnotationAction { get; set; } = "place";
    public bool CanAnnotate { get; set; }
    public ImmutableArray<Guid> Dependencies { get; set; } = [];
    public string FieldClass(string field) => FieldClasses.GetValueOrDefault(field, "");
    public string? FieldTitle(string field) => FieldTitles.GetValueOrDefault(field);
}

public sealed class RoomSubroomRow(Guid clientId, RoomSubroomValues initial) : RoomTableRow(clientId)
{
    public RoomSubroomValues Initial { get; } = initial;
    public RoomSubroomValues Values { get; set; } = initial;
    public RoomSubroomGeometry Geometry { get; set; } = new(null, null, null, null, true);
}
public sealed class RoomTransitionRow(Guid clientId, RoomTransitionValues initial) : RoomTableRow(clientId)
{
    public RoomTransitionValues Initial { get; } = initial;
    public RoomTransitionValues Values { get; set; } = initial;
    public RoomInGameMetadata Metadata { get; set; } = new(null, null, null, null, null, null, null, null, null);
    public bool EnableAnnotation { get; set; } = true;
    public string InverseIcon { get; set; } = "fa-triangle-exclamation";
    public string InverseClass { get; set; } = "warning";
    public string InverseTitle { get; set; } = "no inverse transition";
    public bool CanPrepareInverse { get; set; }
    public Guid? InverseRoomId { get; set; }
    public ImmutableArray<string> DestinationAliases { get; set; } = [];
}
public sealed class RoomConnectionRow(Guid clientId, RoomConnectionValues initial) : RoomTableRow(clientId)
{
    public RoomConnectionValues Initial { get; } = initial;
    public RoomConnectionValues Values { get; set; } = initial;
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool EnableAnnotation { get; set; } = true;
    public string PathwayIcon { get; set; } = "fa-triangle-exclamation";
    public string PathwayClass { get; set; } = "warning";
    public string? PathwayTitle { get; set; }
    public bool CanScaffoldInverse { get; set; }
    public Guid? AnnotationMarkerId { get; set; }
    public Guid? AttentionSourceSubroomId { get; set; }
    public Guid? AttentionDestinationSubroomId { get; set; }
}
public sealed class RoomCheckRow(Guid clientId, RoomCheckValues initial, bool enemies) : RoomTableRow(clientId)
{
    public RoomCheckValues Initial { get; } = initial;
    public RoomCheckValues Values { get; set; } = initial;
    // Authoritative partition only. Optimistic Type changes never move this owner.
    public bool Enemies { get; } = enemies;
    public RoomInGameMetadata Metadata { get; set; } = new(null, null, null, null, null, null, null, null, null);
    public bool EnableAnnotation { get; set; } = true;
}
