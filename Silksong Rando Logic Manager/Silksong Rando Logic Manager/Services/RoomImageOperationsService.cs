using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public enum RoomImageAssetKind { Original, Crop }
public sealed record UnusedRoomImage(Guid RoomId, RoomImageAssetKind Kind, string Hash, long Bytes);
public sealed record RoomImageScan(IReadOnlyList<UnusedRoomImage> Candidates, bool Complete)
{
    public long Bytes => Candidates.Sum(x => x.Bytes);
}
public sealed record RoomImageCleanupResult(int Deleted, long Bytes, IReadOnlyList<Exception> Failures);
public sealed record RoomImageBlocker(string FriendlyName, string Instruction);
public sealed record RoomImageRegenerationResult(int Generated, int Failed, bool Interrupted,
    IReadOnlyList<RoomImageBlocker> Blockers, IReadOnlyList<Exception> Diagnostics);

/// <summary>Explicit slow integrity/lifecycle operations. Never called by hot scene/sidebar loaders.</summary>
public sealed class RoomImageOperationsService(IDbContextFactory<LogicDbContext> contexts, SceneImageFileService files,
    SceneImageCaptureService capture, ILogger<RoomImageOperationsService> logger)
{
    public async Task<RoomImageScan> ScanAsync()
    {
        using var admission = await files.AcquireLifecycleAsync();
        await using var db = await contexts.CreateDbContextAsync();
        var rooms = await db.Rooms.AsNoTracking().Select(x => new { x.Id, x.SceneImageOriginalHash, x.SceneImageCropHash }).ToDictionaryAsync(x => x.Id);
        List<UnusedRoomImage> candidates = [];
        var complete = true;
        foreach (var kind in Enum.GetValues<RoomImageAssetKind>())
        {
            var directory = kind == RoomImageAssetKind.Original ? files.OriginalsDirectory : files.ScenesDirectory;
            var extension = kind == RoomImageAssetKind.Original ? ".image" : ".webp";
            if (!Directory.Exists(directory)) continue;
            string[] paths;
            try { paths = Directory.GetFiles(directory); }
            catch (Exception ex) { logger.LogError(ex, "Room image directory scan failed"); complete = false; continue; }
            foreach (var path in paths)
            {
                if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var roomId)) continue;
                if (Path.GetFileName(path) != $"{roomId:D}{extension}") continue;
                try
                {
                    var hash = SceneImageFileService.Hash(path);
                    var room = rooms.GetValueOrDefault(roomId);
                    var expected = kind == RoomImageAssetKind.Original ? room?.SceneImageOriginalHash : room?.SceneImageCropHash;
                    if (expected == hash) continue;
                    candidates.Add(new(roomId, kind, hash, new FileInfo(path).Length));
                    if (kind == RoomImageAssetKind.Crop && room is not null)
                        await db.Rooms.Where(x => x.Id == roomId && x.SceneImageCropHash == expected)
                            .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsSceneImageStale, true));
                }
                catch (Exception ex) { logger.LogError(ex, "Room image integrity scan failed for {Path}", path); complete = false; }
            }
        }
        return new(candidates, complete);
    }

    public async Task<RoomImageCleanupResult> DeleteUnusedAsync(RoomImageScan confirmed)
    {
        if (!confirmed.Complete || confirmed.Candidates.Count == 0) return new(0, 0, []);
        using var admission = await files.AcquireLifecycleAsync();
        await using var db = await contexts.CreateDbContextAsync();
        var ids = confirmed.Candidates.Select(x => x.RoomId).Distinct().ToArray();
        var rooms = await db.Rooms.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.SceneImageOriginalHash, x.SceneImageCropHash }).ToDictionaryAsync(x => x.Id);
        int deleted = 0; long bytes = 0; List<Exception> failures = [];
        foreach (var candidate in confirmed.Candidates)
        {
            var path = candidate.Kind == RoomImageAssetKind.Original ? files.GetOriginalPath(candidate.RoomId) : files.GetRoomImagePath(candidate.RoomId);
            try
            {
                if (!File.Exists(path)) continue;
                var hash = SceneImageFileService.Hash(path);
                var room = rooms.GetValueOrDefault(candidate.RoomId);
                var expected = candidate.Kind == RoomImageAssetKind.Original ? room?.SceneImageOriginalHash : room?.SceneImageCropHash;
                if (hash != candidate.Hash || hash == expected) continue;
                var size = new FileInfo(path).Length;
                File.Delete(path); deleted++; bytes += size;
                if (candidate.Kind == RoomImageAssetKind.Crop && room is not null)
                    await db.Rooms.Where(x => x.Id == candidate.RoomId && x.SceneImageCropHash == expected)
                        .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsSceneImageStale, true));
            }
            catch (Exception ex) { logger.LogError(ex, "Unused room image deletion failed for {RoomId}/{Kind}", candidate.RoomId, candidate.Kind); failures.Add(ex); }
        }
        return new(deleted, bytes, failures);
    }

    public async Task<RoomImageRegenerationResult> RegenerateAsync(SceneImageRebuildMode mode, IProgress<SceneImageRebuildProgress> progress)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var rooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.FriendlyName }).ToArrayAsync();
        var names = rooms.ToDictionary(x => x.Id, x => x.FriendlyName);
        SceneImageRebuildResult result; Exception? interruption = null;
        try { result = await capture.RebuildPackageRoomsAsync(new(rooms.Select(x => x.Id).ToArray(), mode), progress: progress); }
        catch (SceneImageRebuildInterruptedException ex) { result = ex.Result; interruption = ex; }
        List<RoomImageBlocker> blockers = [];
        foreach (var item in result.Items)
        {
            var instruction = item.Issue switch
            {
                SceneImageRebuildIssue.BoundsUnset => "Bounds are unset or invalid. Set room bounds before rebuilding.",
                SceneImageRebuildIssue.MappingIncomplete => "Image alignment is incomplete. Set scales and required offsets before rebuilding.",
                SceneImageRebuildIssue.MapSourceUnavailable => "ScriptersWar crop source is unavailable. Prepare its preview and tiles before rebuilding.",
                SceneImageRebuildIssue.OriginalUnavailable => "Uploaded original is unavailable. Replace it before rebuilding.",
                SceneImageRebuildIssue.RoomChanged => "Room image settings changed. Review the saved settings before rebuilding.",
                _ => null
            };
            if (instruction is not null && names.TryGetValue(item.RoomId, out var name)) blockers.Add(new(name, instruction));
        }
        var diagnostics = result.FailureExceptions.Concat(result.CleanupExceptions).ToList();
        if (interruption is not null) diagnostics.Add(interruption);
        return new(result.Items.Count(x => x.Status == SceneImageRebuildItemStatus.Generated),
            result.Items.Count(x => x.Status == SceneImageRebuildItemStatus.Failed), interruption is not null, blockers, diagnostics);
    }
}
