namespace SilksongRandoLogicManager.Contracts;

// Persisted/imported facts, not a server-prepared display or room replica.
public sealed record MapDataSnapshot(IReadOnlyList<MapDataMap> Maps, IReadOnlyList<MapDataZone> Zones,
    IReadOnlyList<MapDataScene> Scenes, IReadOnlyList<MapDataChunk> Chunks, IReadOnlyList<MapDataOverlay> FirstOverlays);
public sealed record MapDataMap(Guid Id, int SortOrder, double? MinX, double? MinY, double? MaxX, double? MaxY);
public sealed record MapDataZone(Guid Id, Guid MapId, string InGameId, double? MinX, double? MinY, double? MaxX, double? MaxY);
public sealed record MapDataScene(Guid Id, Guid MapZoneId, string InGameId, string? RoomReferenceText, Guid? ResolvedRoomId);
public sealed record MapDataChunk(Guid Id, Guid MapSceneId, int CacheIndex, double? MinX, double? MinY, double? MaxX, double? MaxY, double? Z);
public sealed record MapDataAsset(string Url, double AspectRatio);
public sealed record MapDataOverlay(Guid Id, Guid MapId, string ImageAssetKey, MapDataAsset? Asset,
    double ScaleXPercent, double ScaleYPercent, double LeftOffsetPercent, double BottomOffsetPercent);
