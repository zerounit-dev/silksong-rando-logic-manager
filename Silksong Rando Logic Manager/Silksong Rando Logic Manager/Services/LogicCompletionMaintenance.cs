using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using SilksongRandoLogicManager.Contracts;
using static Silksong_Rando_Logic_Manager.Services.AppliedRoomStatusCalculation;

namespace Silksong_Rando_Logic_Manager.Services;

/// <summary>Publication-admitted canonical input maintenance. Startup may read all
/// inputs once; ordinary transactions load only the captured old/new owner IDs.</summary>
internal static class LogicCompletionMaintenance
{
    internal static async Task<AppliedCompletionIndex> InitializeAsync(LogicDbContext db, CancellationToken token)
    {
        var index = await ReadIndexAsync(db, token);
        var initial = index.Prepare(new Dictionary<Guid, AppliedCompletionIndex.RoomFacts?>());
        await StoreAsync(db, initial.Calculate(index.RoomIds), token);
        return index;
    }

    // Offline bulk preparation already owns a verified read snapshot. Construct
    // its private process inputs without a startup backfill/write/migration or an
    // extra durable unit. Each original bulk unit finalizes its own changed owners;
    // normal server startup still backfills before serving a new run.
    internal static async Task<AppliedCompletionIndex> ReadIndexAsync(LogicDbContext db, CancellationToken token) =>
        new((await ReadAsync(db, null, token)).Values);

    internal static async Task<AppliedCompletionIndex.Pending> FinalizeAsync(LogicDbContext db,
        AppliedCompletionIndex index, IReadOnlyList<LogicChangeCapture.Change> source, CancellationToken token)
    {
        var owners = new HashSet<Guid>();
        foreach (var change in source)
        {
            if (change.Table == LogicCacheTable.Rooms) owners.Add(Guid.Parse(change.Key));
            else if (change.Table is LogicCacheTable.Subrooms or LogicCacheTable.RoomTransitions
                or LogicCacheTable.SubroomConnections or LogicCacheTable.CheckLocations)
            {
                var ordinal = change.Columns.ToList().IndexOf("RoomId");
                if (ordinal < 0) throw new InvalidOperationException("Missing completion owner column.");
                AddOwner(change.Before);
                AddOwner(change.After);
                void AddOwner(LogicCacheRow? row)
                {
                    if (row is null) return;
                    var cell = row.Values[ordinal];
                    if (cell.Type != LogicCacheCellType.Text || cell.Value is null)
                        throw new InvalidOperationException("Invalid completion owner storage type.");
                    owners.Add(Guid.Parse(cell.Value));
                }
            }
        }
        var current = await ReadAsync(db, owners, token);
        var replacements = owners.ToDictionary(id => id, id => current.GetValueOrDefault(id));
        var pending = index.Prepare(replacements);
        await StoreAsync(db, pending.Calculate(pending.AffectedOwners()), token);
        return pending;
    }

    private static async Task StoreAsync(LogicDbContext db,
        IReadOnlyDictionary<Guid, AppliedRoomStatus> statuses, CancellationToken token)
    {
        // Raw changed-only UPDATE bypasses authored/audit metadata while preserving
        // database-owned CacheFlagUtc triggers and the caller's original transaction.
        // No SaveChanges of stale tracked Rooms follows this finalization boundary.
        foreach (var (id, status) in statuses)
            await db.Rooms.Where(x => x.Id == id && x.CompletionStatus != status)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.CompletionStatus, status), token);
    }

    private static async Task<Dictionary<Guid, AppliedCompletionIndex.RoomFacts>> ReadAsync(
        LogicDbContext db, IReadOnlyCollection<Guid>? requested, CancellationToken token)
    {
        if (requested is { Count: 0 }) return [];
        var ids = requested?.ToArray();
        var roomQuery = db.Rooms.AsNoTracking();
        var subroomQuery = db.Subrooms.AsNoTracking();
        var transitionQuery = db.RoomTransitions.AsNoTracking();
        var connectionQuery = db.SubroomConnections.AsNoTracking();
        var checkQuery = db.CheckLocations.AsNoTracking();
        if (ids is not null)
        {
            roomQuery = roomQuery.Where(x => ids.Contains(x.Id));
            subroomQuery = subroomQuery.Where(x => ids.Contains(x.RoomId));
            transitionQuery = transitionQuery.Where(x => ids.Contains(x.RoomId));
            connectionQuery = connectionQuery.Where(x => ids.Contains(x.RoomId));
            checkQuery = checkQuery.Where(x => ids.Contains(x.RoomId));
        }
        var rooms = await roomQuery.Select(x => new StatusRoom(x.Id, x.FriendlyName,
            x.ReferenceId, x.InGameId, x.IsArchived)).ToArrayAsync(token);
        var subrooms = (await subroomQuery.Select(x => new StatusSubroom(x.Id, x.RoomId,
            x.FriendlyName, x.ReferenceId, x.SceneUnitX, x.SceneUnitY, x.SceneUnitWidth,
            x.SceneUnitHeight, x.IsArchived)).ToArrayAsync(token)).ToLookup(x => x.RoomId);
        var transitions = (await transitionQuery.Select(x => new StatusTransition(x.Id, x.RoomId,
            x.Alias, x.FriendlyName, x.InGameId, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText,
            x.DestinationTransitionAliasText, x.Requirements, x.ResolvedSourceSubroomId,
            x.ResolvedDestinationRoomId, x.ResolvedDestinationTransitionId, x.IsTodo, x.IsVerified,
            x.IsArchived, x.RequirementsParseSucceeded)).ToArrayAsync(token)).ToLookup(x => x.RoomId);
        var connections = (await connectionQuery.Select(x => new StatusConnection(x.Id, x.RoomId,
            x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText,
            x.Requirements, x.ResolvedSourceSubroomId, x.ResolvedDestinationSubroomId,
            x.IsTodo, x.IsVerified, x.IsArchived, x.RequirementsParseSucceeded)).ToArrayAsync(token)).ToLookup(x => x.RoomId);
        var checks = (await checkQuery.Select(x => new StatusCheck(x.Id, x.RoomId, x.FriendlyName,
            x.SubroomReferenceText, x.Requirements, x.LocationType, x.AnnotationSceneUnitX,
            x.AnnotationSceneUnitY, x.ResolvedSubroomId, x.IsTodo, x.IsVerified, x.IsArchived,
            x.RequirementsParseSucceeded)).ToArrayAsync(token)).ToLookup(x => x.RoomId);
        return rooms.ToDictionary(x => x.Id, x => new AppliedCompletionIndex.RoomFacts(x,
            subrooms[x.Id].ToImmutableArray(), transitions[x.Id].ToImmutableArray(),
            connections[x.Id].ToImmutableArray(), checks[x.Id].ToImmutableArray()));
    }
}
