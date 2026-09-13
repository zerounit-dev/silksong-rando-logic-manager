using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Text.RegularExpressions;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record MapLinkRow(Guid MapSceneId, string ZoneInGameId, string CacheKey, string ChunkIndexes, string? RoomReferenceText, ReferenceResolutionStatus? ResolutionStatus, string? LinkedRoomFriendlyName, Guid? ResolvedRoomId);
public sealed record MapLinkEditorData(IReadOnlyList<MapLinkRow> Rows, IReadOnlyList<Room> ActiveRooms);
public sealed record MapLinkDraft(Guid MapSceneId, string? RoomReferenceText);

public sealed class MapLinkService(IDbContextFactory<LogicDbContext> dbContextFactory)
{
    public async Task<MapLinkEditorData> GetAsync(Guid? resolvedRoomId = null, Guid? mapSceneId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scenes = await db.MapScenes.Include(x => x.MapZone).Include(x => x.Chunks).Include(x => x.ResolvedRoom).ToListAsync(cancellationToken);
        scenes = resolvedRoomId is { } room ? scenes.Where(x => x.ResolvedRoomId == room).ToList() : mapSceneId is { } scene ? scenes.Where(x => x.Id == scene).ToList() : scenes;
        var report = await new LogicReferenceResolver(db).GetResolutionReportAsync(cancellationToken);
        var rows = scenes.OrderBy(x => x.MapZone!.InGameId).ThenBy(x => x.InGameId).Select(x => new MapLinkRow(x.Id, x.MapZone!.InGameId, x.InGameId, string.Join(", ", x.Chunks.OrderBy(c => c.CacheIndex).Select(c => c.CacheIndex)), x.RoomReferenceText,
            report.References.SingleOrDefault(r => r.EntityType == nameof(MapScene) && r.EntityId == x.Id && r.FieldName == nameof(MapScene.RoomReferenceText))?.Status,
            x.ResolvedRoom is { IsArchived: false } linked ? linked.FriendlyName : null, x.ResolvedRoomId)).ToList();
        var rooms = await db.Rooms.AsNoTracking().Where(x => !x.IsArchived).OrderBy(x => x.ReferenceId).ToListAsync(cancellationToken);
        return new(rows, rooms);
    }

    public async Task SaveAsync(IReadOnlyCollection<MapLinkDraft> drafts, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ids = drafts.Select(x => x.MapSceneId).ToList();
        var scenes = await db.MapScenes.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        var changed = new List<MapScene>();
        foreach (var draft in drafts)
        {
            var scene = scenes.Single(x => x.Id == draft.MapSceneId);
            if (scene.RoomReferenceText == draft.RoomReferenceText) continue;
            scene.RoomReferenceText = draft.RoomReferenceText;
            changed.Add(scene);
        }

        await db.SaveChangesAsync(cancellationToken);
        await MapManifestService.AutoLinkBlankMapScenesAsync(db, changed, new MapManifestService.Counter(), cancellationToken);
        await new LogicReferenceResolver(db).ResolveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> MergeBlankMapScenesIntoRoomsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var scenes = await db.MapScenes.Where(scene => string.IsNullOrWhiteSpace(scene.RoomReferenceText)).ToListAsync(cancellationToken);
        var rooms = await db.Rooms.Where(room => !room.IsArchived && !string.IsNullOrWhiteSpace(room.InGameId)).ToListAsync(cancellationToken);
        var merged = 0;
        foreach (var scene in scenes)
        {
            var matches = rooms
                .Where(room => CacheKeyMatchesRoom(scene.InGameId, room.InGameId!))
                .ToList();
            var longestLength = matches.Count == 0 ? 0 : matches.Max(room => room.InGameId!.Trim().Length);
            var longestMatches = matches.Where(room => room.InGameId!.Trim().Length == longestLength).ToList();
            if (longestMatches.Count != 1)
            {
                continue;
            }

            scene.RoomReferenceText = longestMatches[0].ReferenceId;
            merged++;
        }

        if (merged > 0) await db.SaveChangesAsync(cancellationToken);
        await new LogicReferenceResolver(db).ResolveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return merged;
    }

    private static bool CacheKeyMatchesRoom(string cacheKey, string roomInGameId)
    {
        var roomId = roomInGameId.Trim();
        var key = cacheKey.Trim();
        return string.Equals(key, roomId, StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(key, $"^(?i:{Regex.Escape(roomId)})_[a-z1-9]");
    }
}
