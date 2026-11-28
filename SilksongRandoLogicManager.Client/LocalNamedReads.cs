using System.Collections.Immutable;
using System.Text.Json.Serialization;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

// Named SQL sources only. Image existence, aliases, scene preparation and map
// assets are deliberately absent: those remain the consuming managed owners'.
internal sealed record LocalVocabularySource<T>([property: JsonRequired] IReadOnlyList<T> Predicates,
    [property: JsonRequired] IReadOnlyList<T> Crests, [property: JsonRequired] IReadOnlyList<T> Methods,
    [property: JsonRequired] IReadOnlyList<T> Items, [property: JsonRequired] IReadOnlyList<T> Countables,
    [property: JsonRequired] IReadOnlyList<T> Souls);
internal sealed record LocalOverlaySource(Guid Id, Guid MapId, string ImageAssetKey, int SortOrder,
    double ScaleXPercent, double ScaleYPercent, double LeftOffsetPercent, double BottomOffsetPercent);
internal sealed record LocalMapsSource(ImmutableArray<MapDataMap> Maps, ImmutableArray<MapDataZone> Zones,
    ImmutableArray<MapDataScene> Scenes, ImmutableArray<MapDataChunk> Chunks, ImmutableArray<LocalOverlaySource> Overlays);
internal sealed record LocalRoomHeaderSource(RoomDocumentObservation Observation, RoomRowObservation Row,
    RoomHeaderValues Values, Guid? RoomGroupId, int SortOrder, RoomStatus CompletionStatus,
    RoomSceneImageBaseline SceneImage);
internal sealed record LocalSubroomSource(Guid RoomId, RoomSubroomSnapshot Snapshot);
internal sealed record LocalExplanationSource(Guid RoomId, RoomExplanationSnapshot Snapshot);
internal sealed record LocalTransitionSource(Guid RoomId, RoomTransitionSnapshot Snapshot);
internal sealed record LocalConnectionSource(Guid RoomId, RoomConnectionSnapshot Snapshot);
internal sealed record LocalCheckSource(Guid RoomId, RoomCheckSnapshot Snapshot);
internal sealed record LocalRoomSource(ImmutableArray<LocalRoomHeaderSource> Rooms,
    ImmutableArray<LocalSubroomSource> Subrooms, ImmutableArray<LocalTransitionSource> Transitions,
    ImmutableArray<LocalConnectionSource> Connections, ImmutableArray<LocalCheckSource> Checks,
    ImmutableArray<CatalogueGroup> Groups, ImmutableArray<Guid> RoomOrder, ImmutableArray<Guid> GroupOrder,
    ImmutableArray<LocalExplanationSource> Explanations);
