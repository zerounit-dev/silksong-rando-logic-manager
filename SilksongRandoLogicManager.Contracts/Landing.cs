using SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

// Pure presentation contracts shared by the retained server renderer and V3.
public enum AppliedRoomStatus { Neutral, Success, Warning, Danger }
public sealed record MapSvgBounds(double X, double Y, double Width, double Height);
public sealed record MapRenderProjectionBounds(double MinX, double MinY, double MaxX, double MaxY);
public sealed record MapRenderProjectionOwner(Guid? ActiveResolvedRoomId, Guid? UnlinkedChunkId, string SvgPathData, MapRenderProjectionBounds Bounds)
{
    public bool IsLinked => ActiveResolvedRoomId is not null;
}
public sealed record GlobalAreaMapGeometryView(Guid MapId, MapSvgBounds MapBounds, string CacheKey, IReadOnlyList<MapRenderProjectionOwner> Owners, int InputChunkCount);
public sealed record AreaMapOwnerDecoration(Guid? RoomId, Guid? MapSceneId, string Label, AppliedRoomStatus Status);
public sealed record AreaMapOverlayView(Guid Id, Guid MapId, string ImageUrl, double AspectRatio, double ScaleXPercent, double ScaleYPercent, double LeftOffsetPercent, double BottomOffsetPercent, MapOverlayPlacement Placement);
public sealed record AreaMapDecorationView(IReadOnlyDictionary<Guid, AreaMapOwnerDecoration> Linked, IReadOnlyDictionary<Guid, AreaMapOwnerDecoration> Unlinked, AreaMapOverlayView? Overlay);
public sealed record AreaMapHostContextView(string SvgId, string ViewportKey, MapSvgBounds InitialBounds, Guid? CurrentRoomId, bool IsRoomContext);
public sealed record AreaMapSurfaceView(GlobalAreaMapGeometryView Geometry, AreaMapDecorationView Decoration, AreaMapHostContextView Context);
public sealed record AppliedRoomStatusInputs(
    IReadOnlyList<AppliedRoomStatusCalculation.StatusRoom> Rooms,
    IReadOnlyList<AppliedRoomStatusCalculation.StatusSubroom> Subrooms,
    IReadOnlyList<AppliedRoomStatusCalculation.StatusTransition> Transitions,
    IReadOnlyList<AppliedRoomStatusCalculation.StatusConnection> Connections,
    IReadOnlyList<AppliedRoomStatusCalculation.StatusCheck> Checks);
public enum LandingAreaMapState { NoImportedMap, UnusableGeometry, Ready }
public sealed record LandingAreaMapView(LandingAreaMapState State, AreaMapSurfaceView? Surface);
public sealed record LandingLinkRow(Guid MapSceneId, string ZoneInGameId, string CacheKey, string ChunkIndexes, string? RoomReferenceText, string? ResolutionStatus, string? LinkedRoomFriendlyName);
public sealed record LandingLinks(IReadOnlyList<LandingLinkRow> Rows, IReadOnlyList<string> RoomReferences);
public sealed record LandingLinkDraft(Guid MapSceneId, string? RoomReferenceText);
public sealed record ApplyLandingLinks(IReadOnlyList<LandingLinkDraft> Drafts);
public sealed record LandingCommit(bool Committed, int? MergedCount = null, SharedMutationSegment? Shared = null)
{
    public string? HistoryWarning { get; init; }
}
public sealed record LandingPlacement(Guid MapId, double ScaleXPercent, double ScaleYPercent, double LeftOffsetPercent, double BottomOffsetPercent);
