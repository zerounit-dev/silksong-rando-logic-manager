using System.Text.Json;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record SceneImageCaptureDraft(double ScaleXPercent, double ScaleYPercent, double PanXPercent, double PanYPercent);
public sealed record SceneImageCaptureDraftResult(SceneImageCaptureDraft Draft, string? Information = null);
public sealed record SceneImageRebuildRequest(IReadOnlyList<Guid> PackageRoomIds);
public enum SceneImageRebuildItemStatus { Generated, Skipped, Failed }
public sealed record SceneImageRebuildItem(Guid RoomId, SceneImageRebuildItemStatus Status, string Detail);
public sealed record SceneImageRebuildResult(IReadOnlyList<SceneImageRebuildItem> Items);

public sealed class SceneImageCaptureService(
    IDbContextFactory<LogicDbContext> dbContextFactory,
    MapOverlayAssetCatalog overlayAssets,
    MapOverlayPlacementService overlayPlacement,
    SceneImageFileService files)
{
    private const int MaximumOutputDimension = 4096;
    public const string EstimateUnavailableInformation = "Automatic map bounds are unavailable. Adjust scale and pan manually.";
    private static readonly SceneImageCaptureDraft FallbackDraft = new(100, 100, 0, 0);
    private readonly SceneImageTransformService transforms = new();

    public async Task<SceneImageCaptureDraftResult> GetDraftAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var room = await db.Rooms.AsNoTracking().Where(item => item.Id == roomId && !item.IsArchived)
            .Select(item => new CaptureRoomState(item.SceneUnitWidth, item.SceneUnitHeight, item.SceneImageScaleXPercent,
                item.SceneImageScaleYPercent, item.SceneImagePanXPercent, item.SceneImagePanYPercent))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The room is no longer active.");
        if (!HasDimensions(room)) throw new InvalidOperationException("Set valid scene dimensions before capturing a scene image.");
        if (!IsSourceReady()) throw new InvalidOperationException("Generate the scene image preview and tiles before capturing a scene image.");
        if (HasCompleteTransform(room))
            return new(new(room.ScaleX!.Value, room.ScaleY!.Value, room.PanX!.Value, room.PanY!.Value));
        return await GetOptionalEstimateAsync(db, roomId, cancellationToken);
    }

    public bool IsSourceReady()
    {
        using var preview = files.OpenPreview();
        return preview is not null && File.Exists(Path.Combine(files.SourceDirectory, "build.json"));
    }

    public async Task<SceneImageCaptureDraftResult> GetEstimateAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var room = await db.Rooms.AsNoTracking().Where(item => item.Id == roomId && !item.IsArchived)
            .Select(item => new CaptureRoomState(item.SceneUnitWidth, item.SceneUnitHeight, item.SceneImageScaleXPercent,
                item.SceneImageScaleYPercent, item.SceneImagePanXPercent, item.SceneImagePanYPercent))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The room is no longer active.");
        if (!HasDimensions(room)) throw new InvalidOperationException("Set valid scene dimensions before capturing a scene image.");
        if (!IsSourceReady()) throw new InvalidOperationException("Generate the scene image preview and tiles before capturing a scene image.");
        return await GetOptionalEstimateAsync(db, roomId, cancellationToken);
    }

    public async Task ApplyAsync(Guid roomId, SceneImageCaptureDraft draft, CancellationToken cancellationToken = default)
    {
        var candidate = new Room { SceneImageScaleXPercent = draft.ScaleXPercent, SceneImageScaleYPercent = draft.ScaleYPercent, SceneImagePanXPercent = draft.PanXPercent, SceneImagePanYPercent = draft.PanYPercent };
        if (!transforms.IsValidTransform(candidate) || !transforms.HasCompleteTransform(candidate)) throw new InvalidOperationException("Scene image scales must be finite and positive; pan values must be finite.");
        if (!IsSourceReady()) throw new InvalidOperationException("Generate the scene image preview and tiles before capturing a scene image.");

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var room = await db.Rooms.SingleOrDefaultAsync(item => item.Id == roomId && !item.IsArchived, cancellationToken)
            ?? throw new InvalidOperationException("The room is no longer active.");
        if (!HasDimensions(room)) throw new InvalidOperationException("Set valid scene dimensions before capturing a scene image.");
        var temporaryPath = await GenerateAsync(roomId, room, draft, cancellationToken);
        var outputPath = files.GetRoomImagePath(roomId);
        var backupPath = files.CreateTemporaryRoomImagePath(roomId);
        var outputReplaced = false;
        try
        {
            if (File.Exists(outputPath)) File.Move(outputPath, backupPath);
            File.Move(temporaryPath, outputPath);
            outputReplaced = true;
            room.SceneImageScaleXPercent = draft.ScaleXPercent;
            room.SceneImageScaleYPercent = draft.ScaleYPercent;
            room.SceneImagePanXPercent = draft.PanXPercent;
            room.SceneImagePanYPercent = draft.PanYPercent;
            room.IsSceneImageStale = false;
            room.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (outputReplaced && File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(backupPath)) File.Move(backupPath, outputPath, true);
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        if (File.Exists(backupPath)) File.Delete(backupPath);
    }

    // This explicitly local derived-file operation deliberately does not track or
    // save the room it reads. The package IDs are only a scope, never room data.
    public async Task<SceneImageRebuildResult> RebuildPackageRoomsAsync(SceneImageRebuildRequest request, CancellationToken cancellationToken = default)
    {
        var items = new List<SceneImageRebuildItem>();
        foreach (var roomId in request.PackageRoomIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                var room = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(item => item.Id == roomId, cancellationToken);
                if (room is null) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "room is not currently persisted")); continue; }
                if (room.IsArchived) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "room is archived")); continue; }
                if (files.Exists(roomId)) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "scene image already exists")); continue; }
                if (!HasDimensions(room)) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "scene dimensions are unavailable")); continue; }
                if (!transforms.HasCompleteTransform(room) || !transforms.IsValidTransform(room)) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "capture transform is incomplete or invalid")); continue; }
                if (room.IsSceneImageStale) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "capture transform is stale")); continue; }
                if (!await HasSourceAssetsAsync(room, cancellationToken)) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "scene image source assets are unavailable")); continue; }
                var draft = new SceneImageCaptureDraft(room.SceneImageScaleXPercent!.Value, room.SceneImageScaleYPercent!.Value, room.SceneImagePanXPercent!.Value, room.SceneImagePanYPercent!.Value);
                var temporaryPath = await GenerateAsync(roomId, room, draft, cancellationToken);
                try
                {
                    if (await WriteMissingOutputAsync(roomId, temporaryPath, cancellationToken)) items.Add(new(roomId, SceneImageRebuildItemStatus.Generated, "generated"));
                    else items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "scene image already exists"));
                }
                catch (IOException exception) { items.Add(new(roomId, SceneImageRebuildItemStatus.Failed, exception.Message)); }
                finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            }
            catch (MagickException exception) { items.Add(new(roomId, SceneImageRebuildItemStatus.Failed, exception.Message)); }
            catch (IOException exception) { items.Add(new(roomId, SceneImageRebuildItemStatus.Failed, exception.Message)); }
        }
        return new(items);
    }

    private async Task<string> GenerateAsync(Guid roomId, Room room, SceneImageCaptureDraft draft, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(files.SourceDirectory, "build.json"));
        var manifest = await JsonSerializer.DeserializeAsync<SourceManifest>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The scene image tile manifest is invalid.");
        var cropWidth = Math.Max(1, (int)Math.Round(manifest.Width * draft.ScaleXPercent / 100d));
        var cropHeight = Math.Max(1, (int)Math.Round(manifest.Height * draft.ScaleYPercent / 100d));
        var centerX = manifest.Width * (.5 - draft.PanXPercent / 100d);
        var centerY = manifest.Height * (.5 - draft.PanYPercent / 100d);
        var left = (int)Math.Round(centerX - cropWidth / 2d);
        var top = (int)Math.Round(centerY - cropHeight / 2d);
        // Bound the output while keeping the declared scene aspect ratio.
        var targetHeight = Math.Max(1, cropHeight);
        var targetWidth = Math.Max(1, (int)Math.Round(targetHeight * room.SceneUnitWidth!.Value / room.SceneUnitHeight!.Value));
        var outputScale = Math.Min(1d, MaximumOutputDimension / (double)Math.Max(targetWidth, targetHeight));
        targetWidth = Math.Max(1, (int)Math.Round(targetWidth * outputScale));
        targetHeight = Math.Max(1, (int)Math.Round(targetHeight * outputScale));
        using var output = new MagickImage(MagickColors.Transparent, (uint)targetWidth, (uint)targetHeight);
        var tileSize = 1024;
        var firstColumn = Math.Max(0, (int)Math.Floor(left / (double)tileSize));
        var lastColumn = Math.Min(manifest.Columns - 1, (int)Math.Floor((left + cropWidth - 1) / (double)tileSize));
        var firstRow = Math.Max(0, (int)Math.Floor(top / (double)tileSize));
        var lastRow = Math.Min(manifest.Rows - 1, (int)Math.Floor((top + cropHeight - 1) / (double)tileSize));
        for (var row = firstRow; row <= lastRow; row++)
        for (var column = firstColumn; column <= lastColumn; column++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tileLeft = column * tileSize;
            var tileTop = row * tileSize;
            var intersectionLeft = Math.Max(left, tileLeft);
            var intersectionTop = Math.Max(top, tileTop);
            var intersectionRight = Math.Min(left + cropWidth, Math.Min(tileLeft + tileSize, manifest.Width));
            var intersectionBottom = Math.Min(top + cropHeight, Math.Min(tileTop + tileSize, manifest.Height));
            if (intersectionRight <= intersectionLeft || intersectionBottom <= intersectionTop) continue;
            using var tile = new MagickImage(Path.Combine(files.SourceDirectory, "tiles", $"{column}-{row}.webp"));
            tile.Crop(new MagickGeometry(intersectionLeft - tileLeft, intersectionTop - tileTop, (uint)(intersectionRight - intersectionLeft), (uint)(intersectionBottom - intersectionTop)));
            tile.ResetPage();
            var destinationWidth = Math.Max(1, (int)Math.Round((intersectionRight - intersectionLeft) / (double)cropWidth * targetWidth));
            var destinationHeight = Math.Max(1, (int)Math.Round((intersectionBottom - intersectionTop) / (double)cropHeight * targetHeight));
            var destinationX = (int)Math.Round((intersectionLeft - left) / (double)cropWidth * targetWidth);
            var destinationY = (int)Math.Round((intersectionTop - top) / (double)cropHeight * targetHeight);
            tile.Resize(new MagickGeometry((uint)destinationWidth, (uint)destinationHeight) { IgnoreAspectRatio = true });
            output.Composite(tile, destinationX, destinationY, CompositeOperator.Over);
        }

        output.Format = MagickFormat.WebP;
        var temporaryPath = files.CreateTemporaryRoomImagePath(roomId);
        output.Write(temporaryPath);
        return temporaryPath;
    }

    private async Task<bool> WriteMissingOutputAsync(Guid roomId, string temporaryPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outputPath = files.GetRoomImagePath(roomId);
        if (File.Exists(outputPath)) return false;
        try { File.Move(temporaryPath, outputPath); }
        catch (IOException) when (File.Exists(outputPath)) { return false; }
        await Task.CompletedTask;
        return true;
    }

    private async Task ReplaceOutputAsync(Guid roomId, string temporaryPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outputPath = files.GetRoomImagePath(roomId);
        var backupPath = files.CreateTemporaryRoomImagePath(roomId);
        var outputReplaced = false;
        try
        {
            if (File.Exists(outputPath)) File.Move(outputPath, backupPath);
            File.Move(temporaryPath, outputPath);
            outputReplaced = true;
        }
        catch
        {
            if (outputReplaced && File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(backupPath)) File.Move(backupPath, outputPath, true);
            throw;
        }
        finally
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
        await Task.CompletedTask;
    }

    private async Task<bool> HasSourceAssetsAsync(Room room, CancellationToken cancellationToken)
    {
        if (!IsSourceReady()) return false;
        try
        {
            await using var stream = File.OpenRead(Path.Combine(files.SourceDirectory, "build.json"));
            var manifest = await JsonSerializer.DeserializeAsync<SourceManifest>(stream, cancellationToken: cancellationToken);
            if (manifest is null || manifest.Width <= 0 || manifest.Height <= 0 || manifest.Columns <= 0 || manifest.Rows <= 0) return false;
            var cropWidth = Math.Max(1, (int)Math.Round(manifest.Width * room.SceneImageScaleXPercent!.Value / 100d));
            var cropHeight = Math.Max(1, (int)Math.Round(manifest.Height * room.SceneImageScaleYPercent!.Value / 100d));
            var centerX = manifest.Width * (.5 - room.SceneImagePanXPercent!.Value / 100d);
            var centerY = manifest.Height * (.5 - room.SceneImagePanYPercent!.Value / 100d);
            var left = (int)Math.Round(centerX - cropWidth / 2d); var top = (int)Math.Round(centerY - cropHeight / 2d);
            var firstColumn = Math.Max(0, (int)Math.Floor(left / 1024d)); var lastColumn = Math.Min(manifest.Columns - 1, (int)Math.Floor((left + cropWidth - 1) / 1024d));
            var firstRow = Math.Max(0, (int)Math.Floor(top / 1024d)); var lastRow = Math.Min(manifest.Rows - 1, (int)Math.Floor((top + cropHeight - 1) / 1024d));
            for (var row = firstRow; row <= lastRow; row++) for (var column = firstColumn; column <= lastColumn; column++)
                if (!File.Exists(Path.Combine(files.SourceDirectory, "tiles", $"{column}-{row}.webp"))) return false;
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }

    private async Task<SceneImageCaptureDraftResult> GetOptionalEstimateAsync(LogicDbContext db, Guid roomId, CancellationToken cancellationToken)
    {
        var mapIds = await db.MapScenes.AsNoTracking()
            .Where(scene => scene.ResolvedRoomId == roomId)
            .Select(scene => scene.MapZone!.MapId)
            .Distinct()
            .OrderBy(id => id)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (mapIds.Count != 1) return UnavailableEstimate();

        var mapId = mapIds[0];
        var map = await db.Maps.AsNoTracking().Where(item => item.Id == mapId)
            .Select(item => new CaptureMapFrame(item.MapUnitMinX, item.MapUnitMinY, item.MapUnitMaxX, item.MapUnitMaxY))
            .SingleOrDefaultAsync(cancellationToken);
        if (map is null || !HasBounds(map)) return UnavailableEstimate();

        var overlay = await db.MapOverlays.AsNoTracking().Where(item => item.MapId == mapId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => new CaptureOverlay(item.ImageAssetKey, item.ScaleXPercent, item.ScaleYPercent,
                item.LeftOffsetPercent, item.BottomOffsetPercent))
            .FirstOrDefaultAsync(cancellationToken);
        if (overlay is null || !overlayAssets.TryGet(overlay.ImageAssetKey, out var asset) ||
            !overlayPlacement.TryProject(map.MinX!.Value, map.MinY!.Value, map.MaxX!.Value, map.MaxY!.Value,
                overlay.ScaleX, overlay.ScaleY, overlay.Left, overlay.Bottom, asset.AspectRatio, out var placement))
            return UnavailableEstimate();

        var chunks = await db.MapChunks.AsNoTracking()
            .Where(chunk => chunk.MapScene!.ResolvedRoomId == roomId && chunk.MapScene.MapZone!.MapId == mapId &&
                chunk.MapUnitMinX != null && chunk.MapUnitMinY != null && chunk.MapUnitMaxX != null && chunk.MapUnitMaxY != null &&
                chunk.MapUnitMaxX > chunk.MapUnitMinX && chunk.MapUnitMaxY > chunk.MapUnitMinY)
            .Select(chunk => new CaptureChunkBounds(chunk.MapUnitMinX!.Value, chunk.MapUnitMinY!.Value,
                chunk.MapUnitMaxX!.Value, chunk.MapUnitMaxY!.Value))
            .ToListAsync(cancellationToken);
        var usable = chunks.Where(HasBounds).ToArray();
        if (usable.Length == 0) return UnavailableEstimate();

        var minX = usable.Min(chunk => chunk.MinX);
        var minY = usable.Min(chunk => chunk.MinY);
        var maxX = usable.Max(chunk => chunk.MaxX);
        var maxY = usable.Max(chunk => chunk.MaxY);
        var svgMinY = map.MinY!.Value + map.MaxY!.Value - maxY;
        var svgMaxY = map.MinY.Value + map.MaxY.Value - minY;
        var widthPercent = (maxX - minX) / placement.Width * 100d;
        var heightPercent = (svgMaxY - svgMinY) / placement.Height * 100d;
        var centerX = ((minX + maxX) / 2d - placement.X) / placement.Width;
        var centerY = ((svgMinY + svgMaxY) / 2d - placement.Y) / placement.Height;
        var panXPercent = (.5 - centerX) * 100d;
        var panYPercent = (.5 - centerY) * 100d;
        if (!double.IsFinite(widthPercent) || !double.IsFinite(heightPercent) || widthPercent <= 0 || heightPercent <= 0 ||
            !double.IsFinite(panXPercent) || !double.IsFinite(panYPercent)) return UnavailableEstimate();
        return new(new(widthPercent, heightPercent, panXPercent, panYPercent));
    }

    private static bool HasDimensions(Room room) => room.SceneUnitWidth is { } width && room.SceneUnitHeight is { } height && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
    private static bool HasDimensions(CaptureRoomState room) => room.Width is { } width && room.Height is { } height && double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
    private static bool HasCompleteTransform(CaptureRoomState room) => room.ScaleX is > 0 && room.ScaleY is > 0 && room.PanX is not null && room.PanY is not null && double.IsFinite(room.ScaleX.Value) && double.IsFinite(room.ScaleY.Value) && double.IsFinite(room.PanX.Value) && double.IsFinite(room.PanY.Value);
    private static bool HasBounds(CaptureMapFrame map) => map.MinX is { } minX && map.MinY is { } minY && map.MaxX is { } maxX && map.MaxY is { } maxY && double.IsFinite(minX) && double.IsFinite(minY) && double.IsFinite(maxX) && double.IsFinite(maxY) && maxX > minX && maxY > minY;
    private static bool HasBounds(CaptureChunkBounds chunk) => double.IsFinite(chunk.MinX) && double.IsFinite(chunk.MinY) && double.IsFinite(chunk.MaxX) && double.IsFinite(chunk.MaxY) && chunk.MaxX > chunk.MinX && chunk.MaxY > chunk.MinY;
    private static SceneImageCaptureDraftResult UnavailableEstimate() => new(FallbackDraft, EstimateUnavailableInformation);
    private sealed record CaptureRoomState(double? Width, double? Height, double? ScaleX, double? ScaleY, double? PanX, double? PanY);
    private sealed record CaptureMapFrame(double? MinX, double? MinY, double? MaxX, double? MaxY);
    private sealed record CaptureOverlay(string ImageAssetKey, double ScaleX, double ScaleY, double Left, double Bottom);
    private sealed record CaptureChunkBounds(double MinX, double MinY, double MaxX, double MaxY);
    private sealed record SourceManifest(string MasterSha256, int Width, int Height, int Columns, int Rows);
}
