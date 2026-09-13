using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record MapSvgBounds(double X, double Y, double Width, double Height);
public sealed record GlobalAreaMapGeometryView(
    Guid MapId, MapSvgBounds MapBounds, string CacheKey,
    IReadOnlyList<MapRenderProjectionOwner> Owners, int InputChunkCount);
public sealed record AreaMapOwnerDecoration(Guid? RoomId, Guid? MapSceneId, string Label, AppliedRoomStatus Status);
public sealed record AreaMapOverlayView(Guid Id, Guid MapId, string ImageUrl, double AspectRatio,
    double ScaleXPercent, double ScaleYPercent, double LeftOffsetPercent, double BottomOffsetPercent,
    MapOverlayPlacement Placement);
public sealed record AreaMapDecorationView(
    IReadOnlyDictionary<Guid, AreaMapOwnerDecoration> Linked,
    IReadOnlyDictionary<Guid, AreaMapOwnerDecoration> Unlinked,
    AreaMapOverlayView? Overlay);
public sealed record AreaMapHostContextView(
    string SvgId, string ViewportKey, MapSvgBounds InitialBounds,
    Guid? CurrentRoomId, bool IsRoomContext);
public sealed record AreaMapSurfaceView(
    GlobalAreaMapGeometryView Geometry, AreaMapDecorationView Decoration, AreaMapHostContextView Context);
public enum LandingAreaMapState { NoImportedMap, UnusableGeometry, Ready }
public sealed record LandingAreaMapView(LandingAreaMapState State, AreaMapSurfaceView? Surface);
public sealed record AreaMapLoadTrace(int SqlQueryCount, int RenderedChunkCount, bool GeometryCacheHit,
    TimeSpan ProjectionElapsed, TimeSpan TotalElapsed);

public interface IAreaMapLoader
{
    Task<LandingAreaMapView> LoadLandingAsync(CancellationToken cancellationToken = default);
    Task<AreaMapSurfaceView?> LoadRoomAsync(Guid roomId, GlobalAreaMapGeometryView? retainedGeometry,
        CancellationToken cancellationToken = default);
}

/// <summary>Page-owned stale-result and cancellation boundary for room-map loads.</summary>
public sealed class RoomAreaMapLoadOwner(IAreaMapLoader loader) : IAsyncDisposable
{
    private CancellationTokenSource? cancellation;
    private long operation;
    private bool disposed;
    public AreaMapSurfaceView? View { get; private set; }
    public GlobalAreaMapGeometryView? RetainedGeometry { get; private set; }
    public long AppliedGeneration { get; private set; }

    public async Task<bool> LoadAsync(Guid roomId, GlobalAreaMapGeometryView? retainedGeometry,
        long refreshGeneration, Func<Guid, long, bool> stillOwns)
    {
        if (disposed) return false;
        cancellation?.Cancel(); cancellation?.Dispose();
        var localCancellation = cancellation = new();
        var load = ++operation;
        try
        {
            var fresh = await loader.LoadRoomAsync(roomId, retainedGeometry, localCancellation.Token);
            if (disposed || localCancellation.IsCancellationRequested || load != operation || !stillOwns(roomId, refreshGeneration)) return false;
            View = fresh;
            if (fresh is not null) RetainedGeometry = fresh.Geometry;
            AppliedGeneration = load;
            return true;
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested) { return false; }
    }

    public void Clear()
    {
        ++operation;
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        View = null;
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed) { disposed = true; Clear(); RetainedGeometry = null; }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Scalar EF boundary for both map hosts.</summary>
public sealed class AreaMapLoader(IDbContextFactory<LogicDbContext> contexts,
    MapRenderProjectionService projection, MapOverlayAssetCatalog overlayAssets,
    MapOverlayPlacementService overlayPlacement, AppliedRoomStatusService statuses) : IAreaMapLoader
{
    public AreaMapLoadTrace? LastTrace { get; private set; }

    public async Task<LandingAreaMapView> LoadLandingAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var map = await FirstMapAsync(db, cancellationToken);
        if (map is null) return new(LandingAreaMapState.NoImportedMap, null);
        if (!map.Bounds.IsUsable) return new(LandingAreaMapState.UnusableGeometry, null);
        var (geometry, projectionResult, geometryQueries) = await LoadGeometryAsync(db, map, cancellationToken);
        var decoration = await LoadDecorationAsync(db, geometry, cancellationToken);
        LastTrace = new(1 + geometryQueries + 8, geometry.InputChunkCount, projectionResult.CacheHit,
            projectionResult.Elapsed, Stopwatch.GetElapsedTime(started));
        return new(LandingAreaMapState.Ready,
            new(geometry, decoration, new("map-wireframe", map.Id.ToString(), map.Bounds.Value, null, false)));
    }

    public async Task<AreaMapSurfaceView?> LoadRoomAsync(Guid roomId, GlobalAreaMapGeometryView? retainedGeometry,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var linkedZones = await db.MapScenes.AsNoTracking()
            .Where(x => x.ResolvedRoomId == roomId && x.ResolvedRoom != null && !x.ResolvedRoom.IsArchived)
            .Select(x => new ZoneFact(x.MapZone!.Id, x.MapZone.MapId, x.MapZone.MapUnitMinX,
                x.MapZone.MapUnitMinY, x.MapZone.MapUnitMaxX, x.MapZone.MapUnitMaxY))
            .Distinct().Take(2).ToListAsync(cancellationToken);
        var selectedZone = linkedZones.Count == 1 && linkedZones[0].Bounds.IsUsable ? linkedZones[0] : null;
        var map = selectedZone is null
            ? await FirstMapAsync(db, cancellationToken)
            : await db.Maps.AsNoTracking().Where(x => x.Id == selectedZone.MapId)
                .Select(x => new MapFact(x.Id, x.MapUnitMinX, x.MapUnitMinY, x.MapUnitMaxX, x.MapUnitMaxY))
                .SingleOrDefaultAsync(cancellationToken);
        if (map is null || !map.Bounds.IsUsable) return null;

        GlobalAreaMapGeometryView geometry;
        MapRenderProjectionResult projectionResult;
        var geometryQueries = 0;
        if (retainedGeometry?.MapId == map.Id)
        {
            geometry = retainedGeometry;
            projectionResult = new(geometry.CacheKey, geometry.Owners, true, TimeSpan.Zero);
        }
        else
        {
            (geometry, projectionResult, geometryQueries) = await LoadGeometryAsync(db, map, cancellationToken);
        }

        var decoration = await LoadDecorationAsync(db, geometry, cancellationToken);
        var initial = selectedZone?.Bounds.IsUsable == true
            ? ProjectZone(selectedZone.Bounds.Value, map.Bounds.Value)
            : map.Bounds.Value;
        var viewportKey = selectedZone is null ? $"room-context-map-{map.Id:N}" : $"room-context-{selectedZone.Id:N}";
        LastTrace = new(2 + geometryQueries + 8,
            geometry.InputChunkCount, projectionResult.CacheHit, projectionResult.Elapsed,
            Stopwatch.GetElapsedTime(started));
        return new(geometry, decoration,
            new($"room-map-context-{roomId:N}", viewportKey, initial, roomId, true));
    }

    private async Task<(GlobalAreaMapGeometryView Geometry, MapRenderProjectionResult Projection, int Queries)> LoadGeometryAsync(
        LogicDbContext db, MapFact map, CancellationToken token)
    {
        var chunks = await db.MapChunks.AsNoTracking()
            .Where(x => x.MapScene!.MapZone!.MapId == map.Id && x.MapUnitMinX != null && x.MapUnitMinY != null &&
                x.MapUnitMaxX != null && x.MapUnitMaxY != null)
            .Select(x => new MapRenderProjectionChunk(x.Id, x.Id.ToString(),
                x.MapScene!.ResolvedRoom != null && !x.MapScene.ResolvedRoom.IsArchived ? x.MapScene.ResolvedRoomId : null,
                x.MapUnitMinX!.Value, x.MapUnitMinY!.Value, x.MapUnitMaxX!.Value, x.MapUnitMaxY!.Value, x.MapUnitZ))
            .ToListAsync(token);
        var projected = projection.Project(new(map.Id, chunks));
        return (new(map.Id, map.Bounds.Value, projected.CacheKey, projected.Owners, chunks.Count), projected, 1);
    }

    private async Task<AreaMapDecorationView> LoadDecorationAsync(LogicDbContext db,
        GlobalAreaMapGeometryView geometry, CancellationToken token)
    {
        var roomIds = geometry.Owners.Where(x => x.ActiveResolvedRoomId is not null).Select(x => x.ActiveResolvedRoomId!.Value).Distinct().ToArray();
        var chunkIds = geometry.Owners.Where(x => x.UnlinkedChunkId is not null).Select(x => x.UnlinkedChunkId!.Value).ToArray();
        var roomLabels = await db.Rooms.AsNoTracking().Where(x => roomIds.Contains(x.Id))
            .Select(x => new { x.Id, x.FriendlyName }).ToListAsync(token);
        var chunkLabels = await db.MapChunks.AsNoTracking().Where(x => chunkIds.Contains(x.Id))
            .Select(x => new { x.Id, x.MapSceneId, Zone = x.MapScene!.MapZone!.InGameId, Scene = x.MapScene.InGameId, x.CacheIndex })
            .ToListAsync(token);
        var overlay = await db.MapOverlays.AsNoTracking().Where(x => x.MapId == geometry.MapId)
            .OrderBy(x => x.SortOrder).Select(x => new OverlayFact(x.Id, x.MapId, x.ImageAssetKey,
                x.ScaleXPercent, x.ScaleYPercent, x.LeftOffsetPercent, x.BottomOffsetPercent)).FirstOrDefaultAsync(token);
        var applied = await statuses.LoadAsync(db, roomIds, token);
        var linked = roomLabels.ToDictionary(x => x.Id, x => new AreaMapOwnerDecoration(x.Id, null, x.FriendlyName, applied.GetValueOrDefault(x.Id)));
        var unlinked = chunkLabels.ToDictionary(x => x.Id, x => new AreaMapOwnerDecoration(null, x.MapSceneId,
            $"unlinked | {x.Zone} | {x.Scene}-{x.CacheIndex}", AppliedRoomStatus.Neutral));
        AreaMapOverlayView? overlayView = null;
        if (overlay is not null && overlayAssets.TryGet(overlay.AssetKey, out var asset) &&
            overlayPlacement.TryProject(geometry.MapBounds.X, geometry.MapBounds.Y,
                geometry.MapBounds.X + geometry.MapBounds.Width, geometry.MapBounds.Y + geometry.MapBounds.Height,
                overlay.ScaleX, overlay.ScaleY, overlay.Left, overlay.Bottom, asset.AspectRatio, out var placement))
            overlayView = new(overlay.Id, overlay.MapId, asset.Url, asset.AspectRatio,
                overlay.ScaleX, overlay.ScaleY, overlay.Left, overlay.Bottom, placement);
        return new(linked, unlinked, overlayView);
    }

    private static Task<MapFact?> FirstMapAsync(LogicDbContext db, CancellationToken token) =>
        db.Maps.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new MapFact(x.Id, x.MapUnitMinX, x.MapUnitMinY, x.MapUnitMaxX, x.MapUnitMaxY))
            .FirstOrDefaultAsync(token);

    private static MapSvgBounds ProjectZone(MapSvgBounds zone, MapSvgBounds map) =>
        new(zone.X, map.Y + map.Y + map.Height - (zone.Y + zone.Height), zone.Width, zone.Height);

    private sealed record MapFact(Guid Id, double? MinX, double? MinY, double? MaxX, double? MaxY)
    {
        public OptionalBounds Bounds => new(MinX, MinY, MaxX, MaxY);
    }
    private sealed record ZoneFact(Guid Id, Guid MapId, double? MinX, double? MinY, double? MaxX, double? MaxY)
    {
        public OptionalBounds Bounds => new(MinX, MinY, MaxX, MaxY);
    }
    private readonly record struct OptionalBounds(double? MinX, double? MinY, double? MaxX, double? MaxY)
    {
        public bool IsUsable => MinX is double minX && MinY is double minY && MaxX is double maxX && MaxY is double maxY && maxX > minX && maxY > minY;
        public MapSvgBounds Value => new(MinX!.Value, MinY!.Value, MaxX!.Value - MinX.Value, MaxY!.Value - MinY.Value);
    }
    private sealed record OverlayFact(Guid Id, Guid MapId, string AssetKey, double ScaleX, double ScaleY, double Left, double Bottom);
}
