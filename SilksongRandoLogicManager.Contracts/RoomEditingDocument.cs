using System.Collections.Immutable;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

namespace SilksongRandoLogicManager.Contracts;

public sealed record RoomDocumentObservation(Guid RoomId, DateTime CacheFlagUtc);
public sealed record RoomMetadata(bool? ImageExists);
public sealed record RoomHeaderSnapshot(RoomRowObservation Observation, RoomHeaderValues Values,
    Guid? RoomGroupId, int SortOrder, RoomSceneImageBaseline SceneImage)
{
    [System.Text.Json.Serialization.JsonRequired]
    public RoomMetadata Metadata { get; init; } = null!;
    /// <summary>Canonical persisted derived completion from the same source read,
    /// not a browser calculation or authored concurrency baseline.</summary>
    public required RoomStatus CompletionStatus { get; init; }
}
public sealed record RoomSubroomSnapshot(RoomRowObservation Observation, int SortOrder,
    RoomSubroomValues Values, RoomSubroomGeometry Geometry);
public sealed record RoomExplanationSnapshot(RoomRowObservation Observation, int SortOrder, RoomExplanationValues Values);
public sealed record RoomTransitionSnapshot(RoomRowObservation Observation, int SortOrder,
    RoomTransitionValues Values, RoomInGameMetadata Metadata, bool EnableAnnotation,
    bool? RequirementsParseSucceeded, Guid? ResolvedSourceSubroomId, Guid? ResolvedDestinationRoomId,
    Guid? ResolvedDestinationTransitionId);
public sealed record RoomConnectionSnapshot(RoomRowObservation Observation, int SortOrder,
    RoomConnectionValues Values, double? SceneUnitX, double? SceneUnitY, bool EnableAnnotation,
    bool? RequirementsParseSucceeded, Guid? ResolvedSourceSubroomId, Guid? ResolvedDestinationSubroomId);
public sealed record RoomCheckSnapshot(RoomRowObservation Observation, int SortOrder,
    RoomCheckValues Values, RoomInGameMetadata Metadata, bool EnableAnnotation,
    bool? RequirementsParseSucceeded, Guid? ResolvedSubroomId);

/// <summary>Complete scalar room facts; scene preparation belongs to the feature read.</summary>
public sealed record RoomScalarSnapshot(RoomDocumentObservation Observation, RoomHeaderSnapshot Header,
    ImmutableArray<RoomSubroomSnapshot> Subrooms, ImmutableArray<RoomTransitionSnapshot> Transitions,
    ImmutableArray<RoomConnectionSnapshot> Connections, ImmutableArray<RoomCheckSnapshot> Checks,
    ImmutableArray<RoomExplanationSnapshot> Explanations);

/// <summary>
/// One coherent logic/scene read. Map context has its own sampled loader boundary;
/// it is deliberately not falsely certified by this room observation.
/// </summary>
public sealed record RoomCompleteDocument(RoomDocumentObservation Observation, RoomHeaderSnapshot Header,
    ImmutableArray<RoomSubroomSnapshot> Subrooms, ImmutableArray<RoomTransitionSnapshot> Transitions,
    ImmutableArray<RoomConnectionSnapshot> Connections, ImmutableArray<RoomCheckSnapshot> Checks,
    SceneLayoutView? Scene, ImmutableArray<RoomExplanationSnapshot> Explanations)
{
    // Targeted page-read dependencies/order, not a global inventory certificate.
    public RoomReadContext? ReadContext { get; init; }
}

public sealed record RoomReadContext(CatalogueGroup? Group, ImmutableArray<Guid> RoomOrder, ImmutableArray<Guid> GroupOrder);

[Flags]
public enum RoomSceneConcerns { None = 0, Canvas = 1, Viewport = 2, BoundsImageAction = 4, ImageDisplay = 8 }
public sealed record RoomOrderReplacement(ImmutableArray<Guid> Active, ImmutableArray<Guid> Archived);
/// <summary>Both sections replace atomically, never as independently published lists.</summary>
public sealed record RoomCheckOrderReplacement(RoomOrderReplacement Checks, RoomOrderReplacement Enemies);
public sealed record RoomRemovedRows(ImmutableArray<Guid> Subrooms, ImmutableArray<Guid> Transitions,
    ImmutableArray<Guid> Connections, ImmutableArray<Guid> Checks, ImmutableArray<Guid> Explanations);

/// <summary>
/// Complete affected row values from one fresh scalar observation, with disjoint
/// removals. Null order means unchanged; an empty order is authoritative empty.
/// CoversFrom is present only with proof that the retained complete document plus
/// this delta covers every change since that complete observation. Observed alone
/// must never advance complete-document freshness.
/// </summary>
public sealed record RoomDocumentDelta(RoomDocumentObservation Observed, RoomDocumentObservation? CoversFrom,
    RoomHeaderSnapshot? Header, ImmutableArray<RoomSubroomSnapshot> Subrooms,
    ImmutableArray<RoomTransitionSnapshot> Transitions, ImmutableArray<RoomConnectionSnapshot> Connections,
    ImmutableArray<RoomCheckSnapshot> Checks, RoomRemovedRows Removed,
    RoomOrderReplacement? SubroomOrder, RoomOrderReplacement? TransitionOrder,
    RoomOrderReplacement? ConnectionOrder, RoomCheckOrderReplacement? CheckOrder,
    RoomSceneConcerns SceneConcerns, SceneLayoutView? Scene,
    ImmutableArray<RoomExplanationSnapshot> Explanations, RoomOrderReplacement? ExplanationOrder);
