using System.Globalization;
using System.Text.Json;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record SceneImageCaptureDraft(double MinX, double MinY, double? MaxX, double? MaxY,
    double? PixelsPerUnitX, double? PixelsPerUnitY, double? PixelOffsetX, double? PixelOffsetY, bool UseSceneImage, DateTime UpdatedUtc, SceneImageCaptureBaseline Baseline)
{
    public bool UseUploadedSceneImage { get; init; }
    public string? OriginalHash { get; init; }
    public Guid? UploadHandle { get; init; }
    public SceneImageUploadPreparation? UploadPreparation { get; init; }
    public double SourcePixelWidth { get; init; } = SceneImageCaptureService.SourceWidth;
    public double SourcePixelHeight { get; init; } = SceneImageCaptureService.SourceHeight;
    public string? PreviewUrl { get; init; } = "/data/scene-source/preview.webp";
}
public sealed record SceneImageCaptureBaseline(double MinX, double MinY, double? MaxX, double? MaxY,
    double? PixelsPerUnitX, double? PixelsPerUnitY, double? PixelOffsetX, double? PixelOffsetY, bool UseSceneImage, bool Stale, DateTime UpdatedUtc)
{
    public bool UseUploadedSceneImage { get; init; }
    public string? OriginalHash { get; init; }
    public string? CropHash { get; init; }
}
internal sealed class SceneImageCaptureConflictException(SceneImageCaptureBaseline baseline) : InvalidOperationException("The room scene state changed elsewhere. The draft is retained against the fresh baseline; review it before retrying.")
{ public SceneImageCaptureBaseline Baseline { get; } = baseline; }
public sealed record SceneImageCaptureDraftResult(SceneImageCaptureDraft Draft, string? Information = null);
public enum SceneImageRebuildMode { MissingOrStale, AllUploaded }
public sealed record SceneImageRebuildRequest(IReadOnlyList<Guid> PackageRoomIds, SceneImageRebuildMode Mode);
public sealed record SceneImageRebuildProgress(int Completed, int Total);
public sealed record SceneImagePublicationResult(bool Published, Exception? CleanupException = null);
public enum SceneImageRebuildItemStatus { Generated, Skipped, Failed }
public enum SceneImageRebuildIssue { None, BoundsUnset, MappingIncomplete, MapSourceUnavailable, OriginalUnavailable, RoomChanged }
public sealed record SceneImageRebuildItem(Guid RoomId, SceneImageRebuildItemStatus Status, string Detail)
{
    public SceneImageRebuildIssue Issue { get; init; }
}
public sealed record SceneImageRebuildResult(IReadOnlyList<SceneImageRebuildItem> Items)
{
    public IReadOnlyList<Exception> CleanupExceptions { get; init; } = [];
    public IReadOnlyList<Exception> FailureExceptions { get; init; } = [];
}
internal sealed class SceneImageRebuildInterruptedException(SceneImageRebuildResult result, Exception inner)
    : Exception("Scene image generation stopped unexpectedly. Earlier results remain committed.", inner)
{ public SceneImageRebuildResult Result { get; } = result; }

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
        if (!draft.UseSceneImage) return new(draft);
        if (draft.UseUploadedSceneImage && draft.UseSceneImage)
        {
            using var admission = await files.AcquireLifecycleAsync(cancellationToken);
            try { draft = WithUploadedSource(roomId, draft); }
            catch (InvalidOperationException ex) { return new(draft with { SourcePixelWidth = 0, SourcePixelHeight = 0, PreviewUrl = null }, ex.Message); }
        }
        if (draft.UseUploadedSceneImage) return new(draft, UsableMapping(draft) ? null : "Enter the retained image's calibration and pixel offsets; no calibration is inferred.");
        if (!HasBounds(draft))
            return new(draft with { PixelsPerUnitX = ValidScale(draft.PixelsPerUnitX) ? draft.PixelsPerUnitX : StandardScale(draft),
                PixelsPerUnitY = ValidScale(draft.PixelsPerUnitY) ? draft.PixelsPerUnitY : StandardScale(draft) },
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
        using var admission = await files.AcquireLifecycleAsync(token);
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
        if (!draft.UseSceneImage) return new(draft);
        if (draft.UseUploadedSceneImage)
        {
            return new(draft with { PixelOffsetX = 0, PixelOffsetY = 0 });
        }
        if (!HasBounds(draft)) return new(draft with { PixelsPerUnitX = StandardScale(draft), PixelsPerUnitY = StandardScale(draft) },
            "Enter valid bounds before resetting pixel offset recommendation.");
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await RecommendAsync(db, roomId, draft, cancellationToken);
    }

    public async Task<SceneImageCaptureDraftResult> SelectUploadAsync(Guid roomId, SceneImageCaptureDraft draft, RoomImageUpload upload, CancellationToken token = default)
    {
        using var admission = await files.AcquireLifecycleAsync(token);
        var staged = files.GetStaged(upload.Handle);
        draft = draft with { UseSceneImage = true, UseUploadedSceneImage = true, OriginalHash = staged.View.Hash,
            UploadHandle = staged.View.Handle, SourcePixelWidth = staged.View.Width, SourcePixelHeight = staged.View.Height,
            PreviewUrl = $"/room-image-drafts/{staged.View.Handle:D}", PixelsPerUnitX = 64, PixelsPerUnitY = 64,
            PixelOffsetX = 0, PixelOffsetY = 0, UploadPreparation = new() };
        return new(draft);
    }

    private SceneImageCaptureDraft WithUploadedSource(Guid roomId, SceneImageCaptureDraft draft)
    {
        var path = files.OriginalForGeneration(roomId, draft.OriginalHash, draft.UploadHandle);
        using var image = SceneImageFileService.ReadOriginal(path);
        return draft with { SourcePixelWidth = image.Width, SourcePixelHeight = image.Height,
            PreviewUrl = draft.UploadHandle is { } handle ? $"/room-image-drafts/{handle:D}" : $"/room-image-originals/{roomId:D}?v={draft.OriginalHash}" };
    }

    public async Task<SceneImageCaptureDraftResult> SelectSourceAsync(Guid roomId, SceneImageCaptureDraft draft, bool? uploaded, RoomImageUpload? upload)
    {
        if (upload is not null) return await SelectUploadAsync(roomId, draft, upload);
        if (uploaded is null) return new(draft with { UseSceneImage = false });
        draft = draft with { UseSceneImage = true, UseUploadedSceneImage = uploaded.Value };
        if (!uploaded.Value) return new(draft with { SourcePixelWidth = SourceWidth, SourcePixelHeight = SourceHeight, PreviewUrl = "/data/scene-source/preview.webp" });
        using var admission = await files.AcquireLifecycleAsync();
        try { return new(WithUploadedSource(roomId, draft)); }
        catch (InvalidOperationException ex) { return new(draft with { SourcePixelWidth = 0, SourcePixelHeight = 0, PreviewUrl = null }, ex.Message); }
    }

    public Task<SceneImagePublicationResult> ApplyAsync(Guid roomId, SceneImageCaptureDraft draft, CancellationToken cancellationToken = default) =>
        GenerateAndPublishAsync(roomId, draft, false, cancellationToken);

    private async Task<SceneImagePublicationResult> GenerateAndPublishAsync(Guid roomId, SceneImageCaptureDraft draft,
        bool retainFreshOutput, CancellationToken cancellationToken)
    {
        using var admission = await files.AcquireLifecycleAsync(cancellationToken);
        if (!draft.UseSceneImage)
        {
            // None is only an authored opt-out, not a save of any hidden draft fields.
            await using var noneDb = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var saved = await noneDb.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId && !x.IsArchived, cancellationToken)
                ?? throw new InvalidOperationException("The room is no longer active.");
            if (Baseline(saved) != draft.Baseline) throw new SceneImageCaptureConflictException(Baseline(saved));
            var updated = saved.UseSceneImage ? (DateTime.UtcNow <= saved.UpdatedUtc ? saved.UpdatedUtc.AddTicks(1) : DateTime.UtcNow) : saved.UpdatedUtc;
            var affected = await MatchingBaseline(noneDb, roomId, draft.Baseline).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.UseSceneImage, false).SetProperty(x => x.UpdatedUtc, updated), cancellationToken);
            if (affected != 1)
            {
                var fresh = await noneDb.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId, cancellationToken);
                if (fresh is null || fresh.IsArchived) throw new InvalidOperationException("The room is no longer active.");
                throw new SceneImageCaptureConflictException(Baseline(fresh));
            }
            return new(true);
        }
        // Replacements publish only when Upload is the final selected source.
        if (!draft.UseUploadedSceneImage) draft = draft with { UploadHandle = null, UploadPreparation = null, OriginalHash = draft.Baseline.OriginalHash };
        if (draft.UploadHandle is not null && draft.UploadPreparation?.Native == true)
            draft = draft with { PixelsPerUnitX = 64, PixelsPerUnitY = 64, PixelOffsetX = 0, PixelOffsetY = 0 };
        ValidateGeneration(draft);
        string? preparedPath = null;
        SceneImagePublicationResult? result = null;
        Exception? preparationCleanup = null;
        try
        {
            if (draft.UploadHandle is { } handle)
            {
                var preparation = draft.UploadPreparation ?? throw new InvalidOperationException("Select the image again to prepare the replacement.");
                var staged = files.GetStaged(handle);
                if (staged.View.Hash != draft.OriginalHash)
                    throw new InvalidOperationException("The draft original identity does not match its upload. Select the image again.");
                var size = SceneImageUploadMath.Calculate(staged.View.Width, staged.View.Height, draft.PixelsPerUnitX, draft.PixelsPerUnitY,
                    preparation, draft.MinX, draft.MinY, draft.MaxX, draft.MaxY);
                preparedPath = files.PrepareOriginal(handle, draft.OriginalHash, size, cancellationToken);
                draft = draft with { PixelsPerUnitX = draft.PixelsPerUnitX * size.RatioX, PixelsPerUnitY = draft.PixelsPerUnitY * size.RatioY,
                    PixelOffsetX = draft.PixelOffsetX * size.RatioX, PixelOffsetY = draft.PixelOffsetY * size.RatioY,
                    OriginalHash = SceneImageFileService.Hash(preparedPath) };
            }
            result = await PublishPreparedAsync(roomId, draft, preparedPath, retainFreshOutput, cancellationToken);
        }
        finally
        {
            try { if (preparedPath is not null && File.Exists(preparedPath)) File.Delete(preparedPath); }
            catch (Exception ex) when (result?.Published == true)
            {
                logger?.LogError(ex, "Postcommit prepared image cleanup failed for room {RoomId}", roomId);
                preparationCleanup = ex;
            }
        }
        return preparationCleanup is null ? result! : result! with { CleanupException = result.CleanupException is { } previous ? new AggregateException(previous, preparationCleanup) : preparationCleanup };
    }

    private async Task<SceneImagePublicationResult> PublishPreparedAsync(Guid roomId, SceneImageCaptureDraft draft, string? preparedPath,
        bool retainFreshOutput, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var room = await db.Rooms.SingleOrDefaultAsync(x => x.Id == roomId && !x.IsArchived, cancellationToken)
            ?? throw new InvalidOperationException("The room is no longer active.");
        if (Baseline(room) != draft.Baseline) throw new SceneImageCaptureConflictException(Baseline(room));
        if (preparedPath is null && draft.OriginalHash != room.SceneImageOriginalHash)
            throw new SceneImageCaptureConflictException(Baseline(room));
        if (retainFreshOutput && !room.IsSceneImageStale && files.CropMatches(roomId, room.SceneImageCropHash)) return new(false);
        var temporaryPath = await GenerateAsync(roomId, draft, cancellationToken, preparedPath);
        string cropHash;
        try { cropHash = SceneImageFileService.Hash(temporaryPath); }
        catch { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); throw; }
        var outputPath = files.GetRoomImagePath(roomId);
        var backupPath = files.CreateTemporaryRoomImagePath(roomId);
        var originalPath = files.GetOriginalPath(roomId);
        var originalBackup = draft.UploadHandle is not null ? files.CreateOriginalBackupPath(roomId) : null;
        var originalReplaced = false;
        var outputReplaced = false;
        var committed = false;
        Exception? cleanupException = null;
        void RecordCleanup(Exception exception)
        {
            logger?.LogError(exception, "Postcommit scene image cleanup failed for room {RoomId}; image remains committed", roomId);
            cleanupException = cleanupException is null ? exception : new AggregateException(cleanupException, exception);
        }
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        try
        {
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Entry(room).ReloadAsync(cancellationToken);
            if (room.IsArchived) throw new InvalidOperationException("The room is no longer active.");
            if (Baseline(room) != draft.Baseline) throw new SceneImageCaptureConflictException(Baseline(room));
            cancellationToken.ThrowIfCancellationRequested();
            var updated = CaptureSettingsChanged(draft)
                ? (DateTime.UtcNow <= room.UpdatedUtc ? room.UpdatedUtc.AddTicks(1) : DateTime.UtcNow)
                : room.UpdatedUtc;
            var affected = await MatchingBaseline(db, roomId, draft.Baseline).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.SceneMinX, draft.MinX).SetProperty(x => x.SceneMinY, draft.MinY)
                .SetProperty(x => x.SceneMaxX, draft.MaxX).SetProperty(x => x.SceneMaxY, draft.MaxY)
                .SetProperty(x => x.SceneImagePixelsPerUnitX, draft.PixelsPerUnitX).SetProperty(x => x.SceneImagePixelsPerUnitY, draft.PixelsPerUnitY)
                .SetProperty(x => x.SceneImagePixelOffsetX, draft.PixelOffsetX).SetProperty(x => x.SceneImagePixelOffsetY, draft.PixelOffsetY)
                .SetProperty(x => x.UseSceneImage, draft.UseSceneImage).SetProperty(x => x.IsSceneImageStale, false)
                .SetProperty(x => x.UseUploadedSceneImage, draft.UseUploadedSceneImage)
                .SetProperty(x => x.SceneImageOriginalHash, draft.UploadHandle != null ? draft.OriginalHash : room.SceneImageOriginalHash)
                .SetProperty(x => x.SceneImageCropHash, cropHash)
                .SetProperty(x => x.UpdatedUtc, updated), cancellationToken);
            if (affected != 1) { await db.Entry(room).ReloadAsync(cancellationToken); throw new SceneImageCaptureConflictException(Baseline(room)); }
            // The admitted conditional write holds SQLite's writer lock until
            // commit, so another Apply cannot replace a file this rollback owns.
            cancellationToken.ThrowIfCancellationRequested();
            if (draft.UploadHandle is { } handle)
            {
                var source = preparedPath ?? throw new InvalidOperationException("The prepared original is unavailable.");
                if (File.Exists(originalPath)) File.Move(originalPath, originalBackup!);
                originalReplaced = true;
                File.Copy(source, originalPath);
                if (!SceneImageFileService.Matches(originalPath, draft.OriginalHash)) throw new IOException("The copied original could not be verified.");
            }
            if (File.Exists(outputPath)) File.Move(outputPath, backupPath);
            File.Move(temporaryPath, outputPath);
            outputReplaced = true;
            await transaction.CommitAsync(CancellationToken.None);
            committed = true;
        }
        catch (Exception failure)
        {
            List<Exception> recoveryFailures = [];
            void Recover(Action action)
            {
                try { action(); }
                catch (Exception ex) { recoveryFailures.Add(ex); logger?.LogError(ex, "Room image rollback failed for {RoomId}; retained rollback files may require recovery", roomId); }
            }
            Recover(() => { if (outputReplaced && File.Exists(outputPath)) File.Delete(outputPath); });
            Recover(() => { if (File.Exists(backupPath)) File.Move(backupPath, outputPath, true); });
            Recover(() => { if (originalReplaced && File.Exists(originalPath)) File.Delete(originalPath); });
            Recover(() => { if (originalBackup is not null && File.Exists(originalBackup)) File.Move(originalBackup, originalPath, true); });
            if (recoveryFailures.Count != 0) throw new AggregateException("Room image publication failed and filesystem rollback was incomplete.", new[] { failure }.Concat(recoveryFailures));
            throw;
        }
        finally
        {
            try
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception exception) when (committed) { RecordCleanup(exception); }
            }
            finally
            {
                try { if (transaction is not null) await transaction.DisposeAsync(); }
                catch (Exception exception) when (committed) { RecordCleanup(exception); }
            }
        }
        try { if (File.Exists(backupPath)) File.Delete(backupPath); }
        catch (Exception exception) { RecordCleanup(exception); }
        try { if (originalBackup is not null && File.Exists(originalBackup)) File.Delete(originalBackup); }
        catch (Exception exception) { RecordCleanup(exception); }
        return new(true, cleanupException);
    }

    public async Task<SceneImageRebuildResult> RebuildPackageRoomsAsync(SceneImageRebuildRequest request, CancellationToken cancellationToken = default, IProgress<SceneImageRebuildProgress>? progress = null)
    {
        if (!Enum.IsDefined(request.Mode)) throw new ArgumentOutOfRangeException(nameof(request));
        var items = new List<SceneImageRebuildItem>();
        var cleanupExceptions = new List<Exception>();
        var failureExceptions = new List<Exception>();
        var roomIds = request.PackageRoomIds.Distinct().ToArray();
        foreach (var roomId in roomIds)
        {
            progress?.Report(new(items.Count, roomIds.Length));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SceneImageCaptureDraft draft;
                {
                    using var admission = await files.AcquireLifecycleAsync(cancellationToken);
                    await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
                    var room = await db.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId, cancellationToken);
                    if (room is null || room.IsArchived) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "room is absent or archived")); continue; }
                    if (!room.UseSceneImage) { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "no image selected")); continue; }
                    var recognized = files.CropMatches(roomId, room.SceneImageCropHash);
                    if (!recognized && files.Exists(roomId) && !room.IsSceneImageStale)
                    {
                        var marked = await MatchingBaseline(db, roomId, Baseline(room)).ExecuteUpdateAsync(set => set.SetProperty(x => x.IsSceneImageStale, true), cancellationToken);
                        if (marked != 1) throw new InvalidOperationException("The room scene state changed during image checking. Review its saved settings before retrying.");
                        room.IsSceneImageStale = true;
                    }
                    if (request.Mode == SceneImageRebuildMode.MissingOrStale && !room.IsSceneImageStale && recognized)
                    { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "fresh scene image already exists")); continue; }
                    draft = FromRoom(room);
                }
                if (!HasBounds(draft))
                { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "finite ordered bounds are unavailable") { Issue = SceneImageRebuildIssue.BoundsUnset }); continue; }
                if (!UsableMapping(draft) || !SceneBounds.FiniteOrNull(draft.PixelOffsetX) || !SceneBounds.FiniteOrNull(draft.PixelOffsetY))
                { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "positive finite scales or required finite pixel offsets are unavailable") { Issue = SceneImageRebuildIssue.MappingIncomplete }); continue; }
                if (draft.UseSceneImage && !draft.UseUploadedSceneImage && !IsSourceReady())
                { items.Add(new(roomId, SceneImageRebuildItemStatus.Skipped, "scene image source preview or tile manifest is unavailable") { Issue = SceneImageRebuildIssue.MapSourceUnavailable }); continue; }
                var publication = await GenerateAndPublishAsync(roomId, draft, request.Mode == SceneImageRebuildMode.MissingOrStale, cancellationToken);
                if (publication.CleanupException is { } cleanup) cleanupExceptions.Add(cleanup);
                items.Add(new(roomId, publication.Published ? SceneImageRebuildItemStatus.Generated : SceneImageRebuildItemStatus.Skipped,
                    publication.CleanupException is not null ? "generated; postcommit cleanup failed (see diagnostics)"
                    : publication.Published ? "generated" : "fresh scene image already exists"));
            }
            catch (Exception ex) when (ex is MagickException or IOException or InvalidOperationException or OutOfMemoryException or JsonException)
            {
                logger?.LogError(ex, "Scene image rebuild failed for room {RoomId}", roomId);
                failureExceptions.Add(ex);
                var detail = ex is JsonException ? "Scene image source manifest is malformed; check the source assets before retrying." : ex is InvalidOperationException ? ex.Message : ex is IOException ? "Scene image file access failed; check file access before retrying." : "Image processing failed; check source tiles or reduce the output size before retrying.";
                items.Add(new(roomId, SceneImageRebuildItemStatus.Failed, detail)
                { Issue = ex is RoomImageOriginalUnavailableException ? SceneImageRebuildIssue.OriginalUnavailable
                    : ex is SceneImageCaptureConflictException ? SceneImageRebuildIssue.RoomChanged : SceneImageRebuildIssue.None });
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Scene image rebuild interrupted for room {RoomId}", roomId);
                throw new SceneImageRebuildInterruptedException(new(items.ToArray()) { CleanupExceptions = cleanupExceptions.ToArray(), FailureExceptions = failureExceptions.ToArray() }, ex);
            }
            finally { progress?.Report(new(items.Count, roomIds.Length)); }
        }
        return new(items) { CleanupExceptions = cleanupExceptions, FailureExceptions = failureExceptions };
    }

    private async Task<string> GenerateAsync(Guid roomId, SceneImageCaptureDraft draft, CancellationToken token, string? preparedPath = null)
    {
        ValidateGeneration(draft);
        SceneBounds.TryGetSize(draft.MinX, draft.MinY, draft.MaxX, draft.MaxY, out var width, out var height);
        var cropWidth = width * draft.PixelsPerUnitX!.Value;
        var cropHeight = height * draft.PixelsPerUnitY!.Value;
        var targetWidth = OutputAxis(cropWidth); var targetHeight = OutputAxis(cropHeight);
        using var output = new MagickImage(MagickColors.Transparent, targetWidth, targetHeight);
        if (draft.UseSceneImage && draft.UseUploadedSceneImage)
        {
            using var source = SceneImageFileService.ReadOriginal(preparedPath ?? files.OriginalForGeneration(roomId, draft.OriginalHash, null));
            var left = draft.MinX * draft.PixelsPerUnitX.Value + draft.PixelOffsetX!.Value;
            var top = source.Height - (draft.MaxY!.Value * draft.PixelsPerUnitY.Value + draft.PixelOffsetY!.Value);
            var ax = targetWidth / cropWidth; var ay = targetHeight / cropHeight;
            if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(ax) || !double.IsFinite(ay))
                throw new InvalidOperationException("The source rectangle cannot be represented. Adjust bounds, scale or pixel offsets.");
            source.VirtualPixelMethod = VirtualPixelMethod.Transparent;
            source.SetArtifact("distort:viewport", string.Create(CultureInfo.InvariantCulture, $"{targetWidth}x{targetHeight}+0+0"));
            source.Distort(DistortMethod.AffineProjection, ax, 0, 0, ay, -left*ax+(ax-1)/2, -top*ay+(ay-1)/2);
            source.ResetPage(); output.Composite(source, 0, 0, CompositeOperator.Over);
        }
        else if (draft.UseSceneImage)
        {
            if (!IsSourceReady()) throw new InvalidOperationException("Generate the scene image preview and tiles before applying the ScriptersWar crop.");
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
    private static bool CaptureSettingsChanged(SceneImageCaptureDraft d) =>
        d.MinX != d.Baseline.MinX || d.MinY != d.Baseline.MinY || d.MaxX != d.Baseline.MaxX || d.MaxY != d.Baseline.MaxY ||
        d.PixelsPerUnitX != d.Baseline.PixelsPerUnitX || d.PixelsPerUnitY != d.Baseline.PixelsPerUnitY ||
        d.PixelOffsetX != d.Baseline.PixelOffsetX || d.PixelOffsetY != d.Baseline.PixelOffsetY || d.UseSceneImage != d.Baseline.UseSceneImage ||
        d.UseUploadedSceneImage != d.Baseline.UseUploadedSceneImage || d.OriginalHash != d.Baseline.OriginalHash;
    private static bool HasBounds(SceneImageCaptureDraft draft) => SceneBounds.TryGetSize(draft.MinX, draft.MinY, draft.MaxX, draft.MaxY, out _, out _);
    private static bool UsableMapping(SceneImageCaptureDraft draft) => SceneBounds.HasUsableScales(draft.PixelsPerUnitX, draft.PixelsPerUnitY) &&
        (!draft.UseSceneImage || SceneBounds.HasUsablePixelOffsets(draft.PixelOffsetX, draft.PixelOffsetY));
    private static void ValidateGeneration(SceneImageCaptureDraft draft)
    {
        if (!draft.UseSceneImage || !HasBounds(draft) || !UsableMapping(draft) || !SceneBounds.FiniteOrNull(draft.PixelOffsetX) || !SceneBounds.FiniteOrNull(draft.PixelOffsetY))
            throw new InvalidOperationException("Enter finite ordered bounds and positive scales; the scene image also needs complete finite pixel offsets.");
    }
    private static SceneImageCaptureBaseline Baseline(Room r) => new(r.SceneMinX, r.SceneMinY, r.SceneMaxX, r.SceneMaxY,
        r.SceneImagePixelsPerUnitX, r.SceneImagePixelsPerUnitY, r.SceneImagePixelOffsetX, r.SceneImagePixelOffsetY, r.UseSceneImage, r.IsSceneImageStale, r.UpdatedUtc)
        { UseUploadedSceneImage = r.UseUploadedSceneImage, OriginalHash = r.SceneImageOriginalHash, CropHash = r.SceneImageCropHash };
    private static IQueryable<Room> MatchingBaseline(LogicDbContext db, Guid id, SceneImageCaptureBaseline b) => db.Rooms.Where(x =>
        x.Id == id && !x.IsArchived && x.UpdatedUtc == b.UpdatedUtc && x.SceneMinX == b.MinX && x.SceneMinY == b.MinY &&
        x.SceneMaxX == b.MaxX && x.SceneMaxY == b.MaxY && x.SceneImagePixelsPerUnitX == b.PixelsPerUnitX && x.SceneImagePixelsPerUnitY == b.PixelsPerUnitY &&
        x.SceneImagePixelOffsetX == b.PixelOffsetX && x.SceneImagePixelOffsetY == b.PixelOffsetY && x.UseSceneImage == b.UseSceneImage && x.IsSceneImageStale == b.Stale &&
        x.UseUploadedSceneImage == b.UseUploadedSceneImage && x.SceneImageOriginalHash == b.OriginalHash && x.SceneImageCropHash == b.CropHash);
    private static SceneImageCaptureDraft FromRoom(Room r) => new(r.SceneMinX, r.SceneMinY, r.SceneMaxX, r.SceneMaxY,
        r.SceneImagePixelsPerUnitX, r.SceneImagePixelsPerUnitY, r.SceneImagePixelOffsetX, r.SceneImagePixelOffsetY, r.UseSceneImage, r.UpdatedUtc, Baseline(r))
        { UseUploadedSceneImage = r.UseUploadedSceneImage, OriginalHash = r.SceneImageOriginalHash };

    private async Task<SceneImageCaptureDraftResult> RecommendAsync(LogicDbContext db, Guid roomId, SceneImageCaptureDraft draft, CancellationToken token)
    {
        var center = draft.UseUploadedSceneImage ? null : await MapCenterAsync(db, roomId, token);
        var scale = StandardScale(draft);
        var ex = draft.MinX / 2 + draft.MaxX!.Value / 2; var ey = draft.MinY / 2 + draft.MaxY!.Value / 2;
        var px = (center?.X ?? draft.SourcePixelWidth / 2) - ex * scale;
        var py = (center?.Y ?? draft.SourcePixelHeight / 2) - ey * scale;
        if (!SceneBounds.HasUsablePixelOffsets(px, py)) return new(draft, "Recommendation is unavailable. Enter finite pixel offsets manually.");
        return new(draft with { PixelsPerUnitX = scale, PixelsPerUnitY = scale, PixelOffsetX = px, PixelOffsetY = py },
            center is null && !draft.UseUploadedSceneImage ? EstimateUnavailableInformation : null);
    }
    private static double StandardScale(SceneImageCaptureDraft draft) => RecommendedScale;

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
