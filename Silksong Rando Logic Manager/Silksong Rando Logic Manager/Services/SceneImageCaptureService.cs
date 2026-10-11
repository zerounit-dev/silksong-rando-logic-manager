using System.Globalization;
using System.Text.Json;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record SceneImageCaptureDraft(double MinX, double MinY, double? MaxX, double? MaxY,
    double? PixelsPerUnitX, double? PixelsPerUnitY, double? PixelOffsetX, double? PixelOffsetY, bool UseSceneImage, DateTime UpdatedUtc, SceneImageCaptureBaseline Baseline);
public sealed record SceneImageCaptureBaseline(double MinX, double MinY, double? MaxX, double? MaxY,
    double? PixelsPerUnitX, double? PixelsPerUnitY, double? PixelOffsetX, double? PixelOffsetY, bool UseSceneImage, bool Stale, DateTime UpdatedUtc);
internal sealed class SceneImageCaptureConflictException(SceneImageCaptureBaseline baseline) : InvalidOperationException("The room scene state changed elsewhere. The draft is retained against the fresh baseline; review it before retrying.")
{ public SceneImageCaptureBaseline Baseline { get; } = baseline; }
public sealed record SceneImageCaptureDraftResult(SceneImageCaptureDraft Draft, string? Information = null);
public sealed record SceneImageRebuildRequest(IReadOnlyList<Guid> PackageRoomIds);
public enum SceneImageRebuildItemStatus { Generated, Skipped, Failed }
public sealed record SceneImageRebuildItem(Guid RoomId, SceneImageRebuildItemStatus Status, string Detail);
public sealed record SceneImageRebuildResult(IReadOnlyList<SceneImageRebuildItem> Items);

public sealed class SceneImageCaptureService(IDbContextFactory<LogicDbContext> dbContextFactory,
    MapOverlayAssetCatalog overlayAssets, MapOverlayPlacementService overlayPlacement, SceneImageFileService files,
    ILogger<SceneImageCaptureService>? logger = null)
{
    public const double SourceWidth = 20500, SourceHeight = 15000, RecommendedScale = 6.35;
    public const string EstimateUnavailableInformation = "Map alignment is unavailable; recommendation uses the source center.";

    public async Task<SceneImageCaptureDraftResult> GetDraftAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var room = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId && !x.IsArchived, cancellationToken)
            ?? throw new InvalidOperationException("The room is no longer active.");
        var draft = FromRoom(room);
        if (!HasBounds(draft))
            return new(draft with { PixelsPerUnitX = ValidScale(draft.PixelsPerUnitX) ? draft.PixelsPerUnitX : RecommendedScale,
                PixelsPerUnitY = ValidScale(draft.PixelsPerUnitY) ? draft.PixelsPerUnitY : RecommendedScale },
                "Enter valid bounds, then reset recommendation or enter pixel offsets manually for the scene image.");
        if (UsableMapping(draft)) return new(draft);
        return await RecommendAsync(db, roomId, draft, cancellationToken);
    }

    public bool IsSourceReady()
    {
        using var preview = files.OpenPreview();
        return preview is not null && File.Exists(Path.Combine(files.SourceDirectory, "build.json"));
    }

    public async Task ClearBoundsAsync(Guid roomId, SceneImageCaptureBaseline baseline, CancellationToken token = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(token);
        var room = await db.Rooms.SingleOrDefaultAsync(x => x.Id == roomId && !x.IsArchived, token)
            ?? throw new InvalidOperationException("The room is no longer active.");
        if (Baseline(room) != baseline) throw new SceneImageCaptureConflictException(Baseline(room));
        var stale = files.Exists(roomId) || room.IsSceneImageStale;
        var updated = DateTime.UtcNow <= room.UpdatedUtc ? room.UpdatedUtc.AddTicks(1) : DateTime.UtcNow;
        var affected = await MatchingBaseline(db, roomId, baseline).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.SceneMinX, 0d).SetProperty(x => x.SceneMinY, 0d)
            .SetProperty(x => x.SceneMaxX, (double?)null).SetProperty(x => x.SceneMaxY, (double?)null)
            .SetProperty(x => x.IsSceneImageStale, stale).SetProperty(x => x.UpdatedUtc, updated), token);
        if (affected != 1) { await db.Entry(room).ReloadAsync(token); throw new SceneImageCaptureConflictException(Baseline(room)); }
    }

    public async Task<SceneImageCaptureDraftResult> GetEstimateAsync(Guid roomId, SceneImageCaptureDraft draft, CancellationToken cancellationToken = default)
    {
        if (!HasBounds(draft)) return new(draft with { PixelsPerUnitX = RecommendedScale, PixelsPerUnitY = RecommendedScale },
            "Enter valid bounds before resetting pixel offset recommendation.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await RecommendAsync(db, roomId, draft, cancellationToken);
    }

    public async Task ApplyAsync(Guid roomId, SceneImageCaptureDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateGeneration(draft);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var room = await db.Rooms.SingleOrDefaultAsync(x => x.Id == roomId && !x.IsArchived, cancellationToken)
            ?? throw new InvalidOperationException("The room is no longer active.");
        if (Baseline(room) != draft.Baseline) throw new SceneImageCaptureConflictException(Baseline(room));
        var temporaryPath = await GenerateAsync(roomId, draft, cancellationToken);
        var outputPath = files.GetRoomImagePath(roomId);
        var backupPath = files.CreateTemporaryRoomImagePath(roomId);
        var outputReplaced = false;
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Entry(room).ReloadAsync(cancellationToken);
            if (room.IsArchived) throw new InvalidOperationException("The room is no longer active.");
            if (Baseline(room) != draft.Baseline) throw new SceneImageCaptureConflictException(Baseline(room));
            cancellationToken.ThrowIfCancellationRequested();
            var updated = DateTime.UtcNow <= room.UpdatedUtc ? room.UpdatedUtc.AddTicks(1) : DateTime.UtcNow;
            var affected = await MatchingBaseline(db, roomId, draft.Baseline).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.SceneMinX, draft.MinX).SetProperty(x => x.SceneMinY, draft.MinY)
                .SetProperty(x => x.SceneMaxX, draft.MaxX).SetProperty(x => x.SceneMaxY, draft.MaxY)
                .SetProperty(x => x.SceneImagePixelsPerUnitX, draft.PixelsPerUnitX).SetProperty(x => x.SceneImagePixelsPerUnitY, draft.PixelsPerUnitY)
                .SetProperty(x => x.SceneImagePixelOffsetX, draft.PixelOffsetX).SetProperty(x => x.SceneImagePixelOffsetY, draft.PixelOffsetY)
                .SetProperty(x => x.UseSceneImage, draft.UseSceneImage).SetProperty(x => x.IsSceneImageStale, false)
                .SetProperty(x => x.UpdatedUtc, updated), cancellationToken);
            if (affected != 1) { await db.Entry(room).ReloadAsync(cancellationToken); throw new SceneImageCaptureConflictException(Baseline(room)); }
            // The admitted conditional write holds SQLite's writer lock until
            // commit, so another Apply cannot replace a file this rollback owns.
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(outputPath)) File.Move(outputPath, backupPath);
            File.Move(temporaryPath, outputPath);
            outputReplaced = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (outputReplaced && File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(backupPath)) File.Move(backupPath, outputPath, true);
            throw;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            finally { if (transaction is not null) await transaction.DisposeAsync(); }
        }
        if (File.Exists(backupPath)) File.Delete(backupPath);
    }

    public async Task<SceneImageRebuildResult> RebuildPackageRoomsAsync(SceneImageRebuildRequest request, CancellationToken cancellationToken = default)
    {
        var items = new List<SceneImageRebuildItem>();
        foreach (var roomId in request.PackageRoomIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var room = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId, cancellationToken);
                if (room is null || room.IsArchived) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "room is absent or archived")); continue; }
                if (files.Exists(roomId)) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "scene image already exists")); continue; }
                var draft = FromRoom(room);
                if (!HasBounds(draft) || !UsableMapping(draft) || room.IsSceneImageStale)
                { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "bounds or capture mapping are unavailable, invalid or stale")); continue; }
                var temporary = await GenerateAsync(roomId, draft, cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (files.Exists(roomId)) items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "scene image already exists"));
                    else
                    {
                        try { File.Move(temporary, files.GetRoomImagePath(roomId)); items.Add(new(roomId, SceneImageRebuildItemStatus.Generated, "generated")); }
                        catch (IOException) when (files.Exists(roomId)) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "scene image already exists")); }
                    }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch (Exception ex) when (ex is MagickException or IOException or InvalidOperationException or OutOfMemoryException or JsonException)
            {
                logger?.LogError(ex, "Scene image rebuild failed for room {RoomId}", roomId);
                var detail = ex is JsonException ? "Scene image source manifest is malformed; check the source assets before retrying." : ex is InvalidOperationException ? ex.Message : ex is IOException ? "Scene image file access failed; check file access before retrying." : "Image processing failed; check source tiles or reduce the output size before retrying.";
                items.Add(new(roomId, SceneImageRebuildItemStatus.Failed, detail));
            }
        }
        return new(items);
    }

    private async Task<string> GenerateAsync(Guid roomId, SceneImageCaptureDraft draft, CancellationToken token)
    {
        ValidateGeneration(draft);
        SceneBounds.TryGetSize(draft.MinX, draft.MinY, draft.MaxX, draft.MaxY, out var width, out var height);
        var cropWidth = width * draft.PixelsPerUnitX!.Value;
        var cropHeight = height * draft.PixelsPerUnitY!.Value;
        var targetWidth = OutputAxis(cropWidth); var targetHeight = OutputAxis(cropHeight);
        using var output = new MagickImage(draft.UseSceneImage ? MagickColors.Transparent : MagickColors.Black, targetWidth, targetHeight);
        if (draft.UseSceneImage)
        {
            if (!IsSourceReady()) throw new InvalidOperationException("Generate the scene image preview and tiles before applying the scene image. Black mode requires no source assets.");
            await using var stream = File.OpenRead(Path.Combine(files.SourceDirectory, "build.json"));
            var manifest = await JsonSerializer.DeserializeAsync<SourceManifest>(stream, cancellationToken: token)
                ?? throw new InvalidOperationException("The scene image tile manifest is invalid.");
            if (manifest.Width != SourceWidth || manifest.Height != SourceHeight || manifest.Columns <= 0 || manifest.Rows <= 0)
                throw new InvalidOperationException("The scene image tile manifest does not describe the configured source.");
            var left = draft.MinX * draft.PixelsPerUnitX.Value + draft.PixelOffsetX!.Value;
            var top = SourceHeight - (draft.MaxY!.Value * draft.PixelsPerUnitY.Value + draft.PixelOffsetY!.Value);
            var right = left + cropWidth; var bottom = top + cropHeight;
            if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom))
                throw new InvalidOperationException("The source rectangle cannot be represented. Adjust bounds, scale or pixel offsets.");
            var ax = targetWidth / cropWidth; var ay = targetHeight / cropHeight;
            if (!double.IsFinite(ax) || !double.IsFinite(ay)) throw new InvalidOperationException("The source sampling scale cannot be represented.");
            var mosaicLeft = (int)Math.Clamp(Math.Floor(left) - 2, 0, SourceWidth);
            var mosaicTop = (int)Math.Clamp(Math.Floor(top) - 2, 0, SourceHeight);
            var mosaicRight = (int)Math.Clamp(Math.Ceiling(right) + 2, 0, SourceWidth);
            var mosaicBottom = (int)Math.Clamp(Math.Ceiling(bottom) + 2, 0, SourceHeight);
            if (mosaicRight > mosaicLeft && mosaicBottom > mosaicTop)
            {
                using var mosaic = new MagickImage(MagickColors.Transparent, (uint)(mosaicRight-mosaicLeft), (uint)(mosaicBottom-mosaicTop));
                for (var row = mosaicTop / 1024; row <= (mosaicBottom-1) / 1024; row++)
                for (var column = mosaicLeft / 1024; column <= (mosaicRight-1) / 1024; column++)
                {
                    token.ThrowIfCancellationRequested();
                    using var tile = new MagickImage(Path.Combine(files.SourceDirectory, "tiles", $"{column}-{row}.webp"));
                    mosaic.Composite(tile, column*1024-mosaicLeft, row*1024-mosaicTop, CompositeOperator.Over);
                }
                mosaic.VirtualPixelMethod = VirtualPixelMethod.Transparent;
                mosaic.SetArtifact("distort:viewport", string.Create(CultureInfo.InvariantCulture, $"{targetWidth}x{targetHeight}+0+0"));
                // Resample once across tile seams, using exact continuous fractional edges.
                mosaic.Distort(DistortMethod.AffineProjection, ax, 0, 0, ay,
                    (mosaicLeft-left)*ax+(ax-1)/2, (mosaicTop-top)*ay+(ay-1)/2);
                mosaic.ResetPage(); output.Composite(mosaic, 0, 0, CompositeOperator.Over);
            }
        }
        token.ThrowIfCancellationRequested();
        output.Format = MagickFormat.WebP;
        var temporary = files.CreateTemporaryRoomImagePath(roomId);
        try { output.Write(temporary); token.ThrowIfCancellationRequested(); return temporary; }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }

    private static uint OutputAxis(double value)
    {
        var rounded = Math.Max(1d, Math.Round(value, MidpointRounding.ToEven));
        if (!double.IsFinite(value) || !double.IsFinite(rounded) || rounded > uint.MaxValue)
            throw new InvalidOperationException("The output image size cannot be represented. Reduce bounds or pixels per unit.");
        return checked((uint)rounded);
    }
    private static bool ValidScale(double? value) => value is > 0 && double.IsFinite(value.Value);
    private static bool HasBounds(SceneImageCaptureDraft draft) => SceneBounds.TryGetSize(draft.MinX, draft.MinY, draft.MaxX, draft.MaxY, out _, out _);
    private static bool UsableMapping(SceneImageCaptureDraft draft) => SceneBounds.HasUsableScales(draft.PixelsPerUnitX, draft.PixelsPerUnitY) &&
        (!draft.UseSceneImage || SceneBounds.HasUsablePixelOffsets(draft.PixelOffsetX, draft.PixelOffsetY));
    private static void ValidateGeneration(SceneImageCaptureDraft draft)
    {
        if (!HasBounds(draft) || !UsableMapping(draft) || !SceneBounds.FiniteOrNull(draft.PixelOffsetX) || !SceneBounds.FiniteOrNull(draft.PixelOffsetY))
            throw new InvalidOperationException("Enter finite ordered bounds and positive scales; the scene image also needs complete finite pixel offsets.");
    }
    private static SceneImageCaptureBaseline Baseline(Room r) => new(r.SceneMinX, r.SceneMinY, r.SceneMaxX, r.SceneMaxY,
        r.SceneImagePixelsPerUnitX, r.SceneImagePixelsPerUnitY, r.SceneImagePixelOffsetX, r.SceneImagePixelOffsetY, r.UseSceneImage, r.IsSceneImageStale, r.UpdatedUtc);
    private static IQueryable<Room> MatchingBaseline(LogicDbContext db, Guid id, SceneImageCaptureBaseline b) => db.Rooms.Where(x =>
        x.Id == id && !x.IsArchived && x.UpdatedUtc == b.UpdatedUtc && x.SceneMinX == b.MinX && x.SceneMinY == b.MinY &&
        x.SceneMaxX == b.MaxX && x.SceneMaxY == b.MaxY && x.SceneImagePixelsPerUnitX == b.PixelsPerUnitX && x.SceneImagePixelsPerUnitY == b.PixelsPerUnitY &&
        x.SceneImagePixelOffsetX == b.PixelOffsetX && x.SceneImagePixelOffsetY == b.PixelOffsetY && x.UseSceneImage == b.UseSceneImage && x.IsSceneImageStale == b.Stale);
    private static SceneImageCaptureDraft FromRoom(Room r) => new(r.SceneMinX, r.SceneMinY, r.SceneMaxX, r.SceneMaxY,
        r.SceneImagePixelsPerUnitX, r.SceneImagePixelsPerUnitY, r.SceneImagePixelOffsetX, r.SceneImagePixelOffsetY, r.UseSceneImage, r.UpdatedUtc, Baseline(r));

    private async Task<SceneImageCaptureDraftResult> RecommendAsync(LogicDbContext db, Guid roomId, SceneImageCaptureDraft draft, CancellationToken token)
    {
        var center = await MapCenterAsync(db, roomId, token);
        var ex = draft.MinX / 2 + draft.MaxX!.Value / 2; var ey = draft.MinY / 2 + draft.MaxY!.Value / 2;
        var px = (center?.X ?? SourceWidth / 2) - ex * RecommendedScale;
        var py = (center?.Y ?? SourceHeight / 2) - ey * RecommendedScale;
        if (!SceneBounds.HasUsablePixelOffsets(px, py)) return new(draft, "Recommendation is unavailable. Enter finite pixel offsets manually.");
        return new(draft with { PixelsPerUnitX = RecommendedScale, PixelsPerUnitY = RecommendedScale, PixelOffsetX = px, PixelOffsetY = py },
            center is null ? EstimateUnavailableInformation : null);
    }

    private async Task<(double X, double Y)?> MapCenterAsync(LogicDbContext db, Guid roomId, CancellationToken token)
    {
        var ids = await db.MapScenes.AsNoTracking().Where(x => x.ResolvedRoomId == roomId).Select(x => x.MapZone!.MapId).Distinct().Take(2).ToListAsync(token);
        if (ids.Count != 1) return null;
        var id = ids[0];
        var map = await db.Maps.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.MapUnitMinX, x.MapUnitMinY, x.MapUnitMaxX, x.MapUnitMaxY }).SingleOrDefaultAsync(token);
        if (map is null || map.MapUnitMinX is not { } minX || map.MapUnitMinY is not { } minY ||
            !SceneBounds.TryGetSize(minX, minY, map.MapUnitMaxX, map.MapUnitMaxY, out _, out _)) return null;
        var overlay = await db.MapOverlays.AsNoTracking().Where(x => x.MapId == id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).FirstOrDefaultAsync(token);
        if (overlay is null || !overlayAssets.TryGet(overlay.ImageAssetKey, out var asset) ||
            !overlayPlacement.TryProject(minX, minY, map.MapUnitMaxX!.Value, map.MapUnitMaxY!.Value, overlay.ScaleXPercent,
                overlay.ScaleYPercent, overlay.LeftOffsetPercent, overlay.BottomOffsetPercent, asset.AspectRatio, out var placement)) return null;
        var chunks = await db.MapChunks.AsNoTracking().Where(x => x.MapScene!.ResolvedRoomId == roomId && x.MapScene.MapZone!.MapId == id)
            .Select(x => new { x.MapUnitMinX, x.MapUnitMinY, x.MapUnitMaxX, x.MapUnitMaxY }).ToListAsync(token);
        var usable = chunks.Where(x => x.MapUnitMinX is { } l && x.MapUnitMinY is { } b && SceneBounds.TryGetSize(l, b, x.MapUnitMaxX, x.MapUnitMaxY, out _, out _)).ToArray();
        if (usable.Length == 0) return null;
        var cx = usable.Min(x => x.MapUnitMinX!.Value) / 2 + usable.Max(x => x.MapUnitMaxX!.Value) / 2;
        var cy = usable.Min(x => x.MapUnitMinY!.Value) / 2 + usable.Max(x => x.MapUnitMaxY!.Value) / 2;
        var svgY = minY + map.MapUnitMaxY!.Value - cy;
        var xCenter = (cx - placement.X) / placement.Width * SourceWidth;
        var yCenter = (1 - (svgY - placement.Y) / placement.Height) * SourceHeight;
        return double.IsFinite(xCenter) && double.IsFinite(yCenter) ? (xCenter, yCenter) : null;
    }
    private sealed record SourceManifest(string MasterSha256, int Width, int Height, int Columns, int Rows);
}
