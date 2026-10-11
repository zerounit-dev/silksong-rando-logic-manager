namespace Silksong_Rando_Logic_Manager.Services;

public sealed record RoomGeometryExportResult(byte[]? Content, IReadOnlyList<RoomGraphExportFailure> Failures, IReadOnlyList<RoomGraphExportWarning> Warnings)
{
    public const string FileName = "room_geometry_data.json";
    public bool Succeeded => Content is not null && Failures.Count == 0;
}

public interface IRoomGeometryExportService
{
    Task<RoomGeometryExportResult> GenerateAsync(CancellationToken cancellationToken = default);
}

public sealed record RoomGeometryDocument(int SchemaVersion, IReadOnlyList<RoomGeometryRoom> Rooms);
public sealed record RoomGeometryRoom(string Id, string ImageGuid, string Name, string RoomGroupName, string? InGameId, bool IsMetadataOnly,
    double? SceneMinX, double? SceneMinY, double? SceneMaxX, double? SceneMaxY,
    double? SceneWidth, double? SceneHeight, double? ImagePixelWidth, double? ImagePixelHeight,
    IReadOnlyList<RoomGeometryNode> Nodes, IReadOnlyList<RoomGeometryTransition> Transitions,
    IReadOnlyList<RoomGeometryConnection> Connections, IReadOnlyList<RoomGeometryLocation> Locations);
public sealed record RoomGeometryNode(string Id, string Name, double? AnnotationX, double? AnnotationY, double? AnnotationWidth,
    double? AnnotationHeight, bool AnnotationEnabled,
    double? ImageNormalizedX, double? ImageNormalizedY, double? ImageNormalizedWidth, double? ImageNormalizedHeight,
    double? ImageNormalizedMinX, double? ImageNormalizedMinY, double? ImageNormalizedMaxX, double? ImageNormalizedMaxY,
    double? ImagePixelX, double? ImagePixelY, double? ImagePixelWidth, double? ImagePixelHeight,
    double? ImagePixelMinX, double? ImagePixelMinY, double? ImagePixelMaxX, double? ImagePixelMaxY);
public sealed record RoomGeometryTransition(string Id, string Alias, string Name, string? InGameId, bool IsUnsafeArrival,
    string SourceNodeId, string? Target, double? AnnotationX, double? AnnotationY, bool AnnotationEnabled,
    double? InGameX, double? InGameY, double? InGameZ,
    double? ImageNormalizedX, double? ImageNormalizedY, double? ImagePixelX, double? ImagePixelY, bool? IsOutOfBounds);
public sealed record RoomGeometryConnection(string Id, string Alias, string Name, string SourceNodeId, string TargetNodeId,
    double? AnnotationX, double? AnnotationY, bool AnnotationEnabled,
    double? ImageNormalizedX, double? ImageNormalizedY, double? ImagePixelX, double? ImagePixelY, bool? IsOutOfBounds);
public sealed record RoomGeometryLocation(string Id, string Name, string? InGameId, string LocationType, string NodeId,
    double? AnnotationX, double? AnnotationY, bool AnnotationEnabled, double? InGameX, double? InGameY, double? InGameZ,
    double? ImageNormalizedX, double? ImageNormalizedY, double? ImagePixelX, double? ImagePixelY, bool? IsOutOfBounds);
