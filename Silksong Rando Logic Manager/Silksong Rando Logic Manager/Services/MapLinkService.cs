using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Text.RegularExpressions;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record MapLinkDraft(Guid MapSceneId, string? RoomReferenceText);
public sealed class MapLinkTargetMissingException : Exception { }
public sealed record MapLinkWriteOutcome(int? MergedCount, SilksongRandoLogicManager.Contracts.SharedMutationSegment? Shared);

public sealed class MapLinkService(IDbContextFactory<LogicDbContext> dbContextFactory, SharedPublicationCoordinator publications,
    SharedCatalogueService sharedCatalogue, ILogger<MapLinkService> logger, Authentication.LiveAccess access)
{
    public async Task<MapLinkWriteOutcome> SaveAsync(IReadOnlyCollection<MapLinkDraft> drafts, CancellationToken cancellationToken = default)
    {
        using var publication = await publications.AcquireAsync(cancellationToken);
        var historyActor = HistoryActor.From(await access.RequireAsync(Authentication.AccessPolicies.Author, cancellationToken));
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await LogicWriteScope.BeginAsync(db, publication, historyActor, cancellationToken);
        var ids = drafts.Select(x => x.MapSceneId).ToList();
        var scenes = await db.MapScenes.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        if (scenes.Count != ids.Distinct().Count()) throw new MapLinkTargetMissingException();
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
        await transaction.FinalizeAsync(cancellationToken);
        var prepared = publication.PrepareAdvance(transaction.FinalEffects.RoomFactsChanged, false);
        var outcome = new MapLinkWriteOutcome(null, null);
        var plan = await SharedRoomCommitPlan.PrepareAsync(db, transaction, prepared, cancellationToken);
        await transaction.CommitAsync(() => publication.PublishCommitted(prepared), cancellationToken);
        publication.Dispose();
        return await ProjectCommittedAsync(outcome, plan);
    }

    public async Task<MapLinkWriteOutcome> MergeBlankMapScenesIntoRoomsAsync(CancellationToken cancellationToken = default)
    {
        using var publication = await publications.AcquireAsync(cancellationToken);
        var historyActor = HistoryActor.From(await access.RequireAsync(Authentication.AccessPolicies.Author, cancellationToken));
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await LogicWriteScope.BeginAsync(db, publication, historyActor, cancellationToken);
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
        await transaction.FinalizeAsync(cancellationToken);
        var prepared = publication.PrepareAdvance(transaction.FinalEffects.RoomFactsChanged, false);
        var outcome = new MapLinkWriteOutcome(merged, null);
        var plan = await SharedRoomCommitPlan.PrepareAsync(db, transaction, prepared, cancellationToken);
        await transaction.CommitAsync(() => publication.PublishCommitted(prepared), cancellationToken);
        publication.Dispose();
        return await ProjectCommittedAsync(outcome, plan);
    }

    private async Task<MapLinkWriteOutcome> ProjectCommittedAsync(MapLinkWriteOutcome receipt, SharedRoomCommitPlan plan)
    {
        try { return receipt with { Shared = await sharedCatalogue.ReadCommittedMutationAsync(plan) }; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Map links committed but affected shared facts could not be projected.");
            return receipt;
        }
    }

    private static bool CacheKeyMatchesRoom(string cacheKey, string roomInGameId)
    {
        var roomId = roomInGameId.Trim();
        var key = cacheKey.Trim();
        return string.Equals(key, roomId, StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(key, $"^(?i:{Regex.Escape(roomId)})_[a-z1-9]");
    }
}
