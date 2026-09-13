using System.Text.Json.Serialization;

namespace Silksong_Rando_Logic_Manager.Services;

// The current Version-3 JSON contract deliberately has no EF entity or context reference.
public sealed record Version3RoomExportEnvelope(int ExportVersion, bool IsPartialRoomDump, IReadOnlyList<DistributedRoomDocument> Rooms, DistributedRoomGroupingSnapshot RoomGroupings, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DistributedAreaMapSnapshot? AreaMap);
public sealed record DistributedRoomGroupingSnapshot(IReadOnlyList<DistributedRoomGroup> Groups);
public sealed record DistributedRoomGroup(Guid Id, string FriendlyName, string? ZoneReferenceText, int SortOrder, DateTime CreatedUtc, DateTime UpdatedUtc);
public sealed record DistributedRoomDocument(Guid Id, Guid? RoomGroupId, string ReferenceId, string FriendlyName, string? InGameId, string? Contributors, string? Comments, double? SceneUnitWidth, double? SceneUnitHeight, double? SceneImageScaleXPercent, double? SceneImageScaleYPercent, double? SceneImagePanXPercent, double? SceneImagePanYPercent, bool IsSceneImageStale, int SortOrder, bool IsArchived, DateTime? ArchivedUtc, DateTime CreatedUtc, DateTime UpdatedUtc, IReadOnlyList<DistributedSubroom> Subrooms, IReadOnlyList<DistributedTransition> Transitions, IReadOnlyList<DistributedConnection> Connections, IReadOnlyList<DistributedCheck> Checks);
public sealed record DistributedSubroom(Guid Id, string ReferenceId, string FriendlyName, string? Notes, double? SceneUnitX, double? SceneUnitY, double? SceneUnitWidth, double? SceneUnitHeight, bool EnableAnnotation, int SortOrder, bool IsArchived, DateTime? ArchivedUtc, DateTime CreatedUtc, DateTime UpdatedUtc);
public sealed record DistributedTransition(Guid Id, string Alias, string FriendlyName, string? InGameId, double? InGamePositionX, double? InGamePositionY, double? InGamePositionZ, double? LocalPositionX, double? LocalPositionY, double? LocalPositionZ, double? AnnotationSceneUnitX, double? AnnotationSceneUnitY, bool EnableAnnotation, string? SourceSubroomReferenceText, string? DestinationRoomReferenceText, string? DestinationTransitionAliasText, string Requirements, string Notes, int SortOrder, bool IsTodo, bool? IsVerified, bool IsArchived, DateTime? ArchivedUtc, DateTime CreatedUtc, DateTime UpdatedUtc);
public sealed record DistributedConnection(Guid Id, string Alias, string FriendlyName, string SourceSubroomReferenceText, string DestinationSubroomReferenceText, string Requirements, string Notes, bool EnableAnnotation, double? SceneUnitX, double? SceneUnitY, int SortOrder, bool IsTodo, bool? IsVerified, bool IsArchived, DateTime? ArchivedUtc, DateTime CreatedUtc, DateTime UpdatedUtc);
public sealed record DistributedCheck(Guid Id, string FriendlyName, string? InGameId, double? InGamePositionX, double? InGamePositionY, double? InGamePositionZ, double? LocalPositionX, double? LocalPositionY, double? LocalPositionZ, double? AnnotationSceneUnitX, double? AnnotationSceneUnitY, string? SubroomReferenceText, string Requirements, string Notes, string? LocationType, bool EnableAnnotation, int SortOrder, bool IsTodo, bool? IsVerified, bool IsArchived, DateTime? ArchivedUtc, DateTime CreatedUtc, DateTime UpdatedUtc);
public sealed record DistributedAreaMapSnapshot(IReadOnlyList<DistributedMap> Maps);
public sealed record DistributedMap(Guid Id, string InGameId, string? FriendlyName, int SortOrder, double? MapUnitMinX, double? MapUnitMinY, double? MapUnitMaxX, double? MapUnitMaxY, IReadOnlyList<DistributedMapOverlay> Overlays, IReadOnlyList<DistributedMapZone> Zones);
public sealed record DistributedMapOverlay(Guid Id, string FriendlyName, string ImageAssetKey, double ScaleXPercent, double ScaleYPercent, double LeftOffsetPercent, double BottomOffsetPercent, int SortOrder);
public sealed record DistributedMapZone(Guid Id, string InGameId, string? FriendlyName, double? MapUnitMinX, double? MapUnitMinY, double? MapUnitMaxX, double? MapUnitMaxY, IReadOnlyList<DistributedMapScene> Scenes);
public sealed record DistributedMapScene(Guid Id, string InGameId, string? FriendlyName, string? RoomReferenceText, IReadOnlyList<DistributedMapChunk> Chunks);
public sealed record DistributedMapChunk(Guid Id, int CacheIndex, string? InitialState, double? MapUnitMinX, double? MapUnitMinY, double? MapUnitMaxX, double? MapUnitMaxY, double? MapUnitZ);
public sealed record DistributedExportResult(string FileName, Version3RoomExportEnvelope Envelope, byte[] JsonUtf8);
public sealed record DistributedExportInvalidLocationType(Guid RoomId, string RoomName, Guid CheckId, string CheckName);
public abstract record CompleteExportOutcome;
public sealed record CompleteExported(DistributedExportResult Export) : CompleteExportOutcome;
public sealed record CompleteExportInvalidLocationType(DistributedExportInvalidLocationType Failure) : CompleteExportOutcome;
public abstract record CurrentRoomExportOutcome;
public sealed record CurrentRoomExported(DistributedExportResult Export) : CurrentRoomExportOutcome;
public sealed record CurrentRoomMissing(Guid RoomId) : CurrentRoomExportOutcome;
public sealed record CurrentRoomExportInvalidLocationType(DistributedExportInvalidLocationType Failure) : CurrentRoomExportOutcome;
public abstract record ZoneExportOutcome;
public sealed record ZoneExported(DistributedExportResult Export) : ZoneExportOutcome;
public sealed record ZoneExportUnavailable(Guid RoomId) : ZoneExportOutcome;
public sealed record ZoneExportInvalidLocationType(DistributedExportInvalidLocationType Failure) : ZoneExportOutcome;
