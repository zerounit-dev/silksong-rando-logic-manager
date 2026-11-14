using System.Collections.Immutable;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

// Named SQL sources only. Image existence, aliases, scene preparation and map
// assets are deliberately absent: those remain the consuming managed owners'.
internal sealed record LocalVocabularySource(ImmutableArray<VocabularyDefinition> Predicates,
    ImmutableArray<VocabularyDefinition> Crests, ImmutableArray<VocabularyDefinition> Methods,
    ImmutableArray<VocabularyDefinition> Items, ImmutableArray<VocabularyDefinition> Countables,
    ImmutableArray<VocabularyDefinition> Souls);
internal sealed record LocalRoomLookupSource(RoomDocumentObservation Observation, RoomLookupIdentity Identity,
    RoomLookupText Values, Guid? RoomGroupId, int SortOrder, RoomStatus CompletionStatus,
    RoomImageIndicatorFacts SceneImage);
internal sealed record LocalTransitionLookupSource(Guid RoomId, TransitionLookupFacts Facts, string? Requirements);
internal sealed record LocalConnectionLookupSource(Guid RoomId, RoomLookupIdentity Identity, string? Requirements);
internal sealed record LocalCheckLookupSource(Guid RoomId, CheckLookupFacts Facts, string? Requirements);
internal sealed record LocalCatalogueSource(LocalCacheState State, ImmutableArray<LocalRoomInventory> Inventory,
    ImmutableArray<LocalRoomLookupSource> Rooms, ImmutableArray<LocalTransitionLookupSource> Transitions,
    ImmutableArray<LocalConnectionLookupSource> Connections, ImmutableArray<LocalCheckLookupSource> Checks,
    ImmutableArray<CatalogueGroup> Groups, LocalVocabularySource Vocabulary);
internal sealed record LocalOverlaySource(Guid Id, Guid MapId, string ImageAssetKey, int SortOrder,
    double ScaleXPercent, double ScaleYPercent, double LeftOffsetPercent, double BottomOffsetPercent);
internal sealed record LocalMapsSource(ImmutableArray<MapDataMap> Maps, ImmutableArray<MapDataZone> Zones,
    ImmutableArray<MapDataScene> Scenes, ImmutableArray<MapDataChunk> Chunks, ImmutableArray<LocalOverlaySource> Overlays);
internal sealed record LocalRoomHeaderSource(RoomDocumentObservation Observation, RoomRowObservation Row,
    RoomHeaderValues Values, Guid? RoomGroupId, int SortOrder, RoomStatus CompletionStatus,
    RoomSceneImageBaseline SceneImage);
internal sealed record LocalSubroomSource(Guid RoomId, RoomSubroomSnapshot Snapshot);
internal sealed record LocalTransitionSource(Guid RoomId, RoomTransitionSnapshot Snapshot);
internal sealed record LocalConnectionSource(Guid RoomId, RoomConnectionSnapshot Snapshot);
internal sealed record LocalCheckSource(Guid RoomId, RoomCheckSnapshot Snapshot);
internal sealed record LocalRoomSource(ImmutableArray<LocalRoomHeaderSource> Rooms,
    ImmutableArray<LocalSubroomSource> Subrooms, ImmutableArray<LocalTransitionSource> Transitions,
    ImmutableArray<LocalConnectionSource> Connections, ImmutableArray<LocalCheckSource> Checks,
    ImmutableArray<CatalogueGroup> Groups, ImmutableArray<Guid> RoomOrder, ImmutableArray<Guid> GroupOrder);
