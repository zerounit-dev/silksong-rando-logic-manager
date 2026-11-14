using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed partial class LogicCatalogService
{
    public sealed record SidebarCommandOutcome(int Status, Wire.MutationResult? Result, Wire.ApiError? Error);
    private sealed record SidebarDecision(int Status, Guid? EditorId, Guid? RoomId, int? Count, string? Code, string? Message);

    // SQLite materializes authored TEXT dates with Unspecified Kind. Restore
    // only the wire annotation; never convert the instant or persisted value.
    private static Wire.GroupEditor? ToWire(GroupEditorData? value) => value is null ? null : new(
        new(value.Baseline.Id, value.Baseline.FriendlyName, value.Baseline.ZoneReferenceText,
            value.Baseline.IsVirtual, DateTime.SpecifyKind(value.Baseline.UpdatedUtc, DateTimeKind.Utc)),
        value.ZoneIds, value.ZoneReferenceStatus?.ToString());

    public async Task<Wire.GroupEditor?> ReadSidebarGroupAsync(Guid id, CancellationToken token) => ToWire(await GetGroupEditorDataAsync(id, token));

    // Context creation/open may cancel. After BEGIN IMMEDIATE succeeds every write,
    // resolver, projection and commit is completion-bound, never RequestAborted-bound.
    private Task<SidebarCommandOutcome> InSidebarCommandAsync(Func<LogicDbContext, Task<SidebarDecision>> operation, CancellationToken token) =>
        InSidebarTransactionAsync(operation, async (db, transaction, prepared, result) =>
        {
            var editor = result.EditorId is { } editorId ? ToWire(await ReadGroupEditorAsync(db, editorId, CancellationToken.None)) : null;
            var plan = await SharedRoomCommitPlan.PrepareAsync(db, transaction, prepared, CancellationToken.None,
                result.RoomId is { } roomId ? [roomId] : [], editor?.Group.Id);
            var shared = await sharedCatalogue.ReadMutationAsync(db, plan, CancellationToken.None);
            return result.Code is null
                ? new SidebarCommandOutcome(result.Status, new(shared, editor, result.RoomId, result.Count), null)
                : new SidebarCommandOutcome(result.Status, null, new(result.Code, result.Message!, Shared: shared, GroupEditor: editor));
        }, token);

    private async Task<TResult> InSidebarTransactionAsync<T, TResult>(Func<LogicDbContext, Task<T>> operation,
        Func<LogicDbContext, LogicWriteScope,
            SharedPublicationCoordinator.PreparedPublication, T, Task<TResult>> project, CancellationToken token)
    {
        using var images = await sharedCatalogue.AcquireImagesAsync(token);
        var (publication, historyActor) = await AcquireAuthorPublicationAsync(token);
        using var publicationLease = publication;
        await using var db = await dbContextFactory.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        await using var transaction = await LogicWriteScope.BeginAsync(db, publication, historyActor, CancellationToken.None);
        var result = await operation(db);
        await transaction.FinalizeAsync(CancellationToken.None);
        var prepared = publication.PrepareAdvance(transaction.FinalEffects.RoomFactsChanged, false);
        var outcome = await project(db, transaction, prepared, result);
        await transaction.CommitAsync(() => publication.PublishCommitted(prepared));
        return outcome;
    }

    private static SidebarDecision SidebarResult(int status = 200,
        Guid? editorId = null, Guid? roomId = null, int? count = null, string? code = null, string? message = null)
    {
        // Projection is deferred until source/completion finalization; operation
        // decisions carry identity only, not an early authoritative read model.
        return new(status, editorId, roomId, count, code, message);
    }

    public Task<SidebarCommandOutcome> CreateSidebarGroupAsync(CancellationToken token) => InSidebarCommandAsync(async db =>
    {
        var group = await CreateGroupCoreAsync(db, "New group");
        return SidebarResult(201, editorId: group.Id);
    }, token);

    private static async Task<RoomGroup> CreateGroupCoreAsync(LogicDbContext db, string name)
    {
        var group = new RoomGroup { FriendlyName = name.Trim(), IsVirtual = false,
            SortOrder = await NextSortOrderAsync(db.RoomGroups, CancellationToken.None) };
        db.RoomGroups.Add(group);
        await db.SaveChangesAsync();
        return group;
    }

    public Task<SidebarCommandOutcome> SaveSidebarGroupAsync(Guid id, Wire.SaveGroup command, CancellationToken token) => InSidebarCommandAsync(async db =>
    {
        var group = await db.RoomGroups.SingleOrDefaultAsync(x => x.Id == id);
        if (group is null) return SidebarResult(404, code: "missing", message: "This group no longer exists. Close this dialog and choose a current group.");
        if (!GroupMatches(group, command.Baseline)) return SidebarResult(409, editorId: id, code: "conflict",
            message: "The group changed. Review your retained draft; another Save applies it to the current baseline.");
        await SaveGroupCoreAsync(db, group, command.Values);
        return SidebarResult(editorId: id);
    }, token);

    private static bool GroupMatches(RoomGroup group, Wire.GroupBaseline baseline) => group.Id == baseline.Id
        && group.UpdatedUtc == baseline.UpdatedUtc && group.FriendlyName == baseline.FriendlyName
        && group.ZoneReferenceText == baseline.ZoneReferenceText && group.IsVirtual == baseline.IsVirtual;

    private async Task<CatalogSaveOutcome> SaveGroupCoreAsync(LogicDbContext db, RoomGroup group, Wire.GroupValues values)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (group.FriendlyName != values.FriendlyName) changed.Add(nameof(RoomGroup.FriendlyName));
        if (group.ZoneReferenceText != values.ZoneReferenceText) changed.Add(nameof(RoomGroup.ZoneReferenceText));
        if (group.IsVirtual != values.IsVirtual) changed.Add(nameof(RoomGroup.IsVirtual));
        if (changed.Count == 0) return CatalogSaveOutcome.Unchanged;
        var zone = group.ZoneReferenceText;
        group.FriendlyName = values.FriendlyName;
        group.ZoneReferenceText = values.ZoneReferenceText;
        group.IsVirtual = values.IsVirtual;
        await db.SaveChangesAsync();
        var resolve = changed.Contains(nameof(RoomGroup.ZoneReferenceText));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (resolve) await new LogicReferenceResolver(db, scopedResolverTrace).ResolveScopedAsync(group, changed,
            new Dictionary<string, string?> { [nameof(RoomGroup.ZoneReferenceText)] = zone }, CancellationToken.None);
        return new(true, resolve, resolve, true, false, resolve ? watch.Elapsed : TimeSpan.Zero);
    }

    public Task<SidebarCommandOutcome> DeleteSidebarGroupAsync(Guid id, CancellationToken token) => InSidebarCommandAsync(async db =>
    {
        var group = await db.RoomGroups.SingleOrDefaultAsync(x => x.Id == id);
        if (group is null) return SidebarResult(404, code: "missing", message: "This group no longer exists. Close the confirmation.");
        await DeleteGroupCoreAsync(db, group);
        return SidebarResult();
    }, token);

    private static async Task DeleteGroupCoreAsync(LogicDbContext db, RoomGroup group)
    {
        // Deliberately bulk/un-audited: unlink archived and active rooms without
        // changing authored room timestamps, as in the retained legacy contract.
        await db.Rooms.Where(x => x.RoomGroupId == group.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.RoomGroupId, (Guid?)null));
        db.RoomGroups.Remove(group);
        await db.SaveChangesAsync();
    }

    public Task<SidebarCommandOutcome> CreateSidebarRoomAsync(Guid? groupId, CancellationToken token) => InSidebarCommandAsync(async db =>
    {
        if (groupId is { } id && !await db.RoomGroups.AnyAsync(x => x.Id == id))
            return SidebarResult(404, code: "missing", message: "The destination group no longer exists. Choose a current destination.");
        var created = await CreateRoomCoreAsync(db, "New room", groupId);
        return SidebarResult(201, roomId: created.Entity.Id);
    }, token);

    private async Task<CatalogCreateOutcome<Room>> CreateRoomCoreAsync(LogicDbContext db, string name, Guid? groupId)
    {
        var reference = await CreateUniqueRoomReferenceIdAsync(db, name, CancellationToken.None);
        var room = new Room { FriendlyName = name.Trim(), ReferenceId = reference, RoomGroupId = groupId,
            SortOrder = await NextSortOrderAsync(db.Rooms.Where(x => !x.IsArchived && x.RoomGroupId == groupId), CancellationToken.None) };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await new LogicReferenceResolver(db, scopedResolverTrace).ResolveCreatedAsync(room, CancellationToken.None);
        return new(room, false, true, false, watch.Elapsed);
    }

    public Task<SidebarCommandOutcome> MoveSidebarGroupAsync(Guid id, Wire.MoveGroup command, CancellationToken token) => InSidebarCommandAsync(async db =>
    {
        var groups = await db.RoomGroups.OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync();
        if (!groups.Any(x => x.Id == id)) return SidebarResult(404, code: "missing", message: "The group no longer exists. Choose a current group.");
        if (!groups.Select(x => x.Id).SequenceEqual(command.ExpectedGroupIds))
            return SidebarResult(409, code: "conflict", message: "Group order changed. Review the current order before moving again.");
        if (command.Index < 0 || command.Index > groups.Count)
            return SidebarResult(400, code: "invalid", message: "The insertion position is invalid.");
        await MoveGroupCoreAsync(db, groups, id, command.Index);
        return SidebarResult();
    }, token);

    private static async Task MoveGroupCoreAsync(LogicDbContext db, List<RoomGroup> groups, Guid id, int index)
    {
        var original = groups.FindIndex(x => x.Id == id);
        var group = groups[original];
        var target = original < index ? index - 1 : index;
        if (target == original) return;
        groups.RemoveAt(original);
        groups.Insert(target, group);
        for (var i = 0; i < groups.Count; i++) groups[i].SortOrder = i;
        await db.SaveChangesAsync();
    }

    public Task<SidebarCommandOutcome> MoveSidebarRoomAsync(Guid id, Wire.MoveRoom command, CancellationToken token) => InSidebarCommandAsync(async db =>
    {
        var room = await db.Rooms.SingleOrDefaultAsync(x => x.Id == id && !x.IsArchived);
        if (room is null || command.TargetGroupId is { } target && !await db.RoomGroups.AnyAsync(x => x.Id == target)
            || command.ExpectedSourceGroupId is { } sourceGroup && !await db.RoomGroups.AnyAsync(x => x.Id == sourceGroup))
            return SidebarResult(404, code: "missing", message: "The room or a move destination no longer exists. Choose current rooms and groups.");
        var source = await ActiveSidebarRoomsAsync(db, room.RoomGroupId);
        var destination = room.RoomGroupId == command.TargetGroupId ? source : await ActiveSidebarRoomsAsync(db, command.TargetGroupId);
        if (room.RoomGroupId != command.ExpectedSourceGroupId
            || !source.Select(x => x.Id).SequenceEqual(command.ExpectedSourceRoomIds)
            || !destination.Select(x => x.Id).SequenceEqual(command.ExpectedTargetRoomIds))
            return SidebarResult(409, code: "conflict", message: "Room membership or order changed. Review the current placement before moving again.");
        if (command.Index < 0 || command.Index > destination.Count)
            return SidebarResult(400, code: "invalid", message: "The insertion position is invalid.");
        await MoveRoomCoreAsync(db, room, source, destination, command.TargetGroupId, command.Index);
        return SidebarResult();
    }, token);

    // V3 array baselines must match the world DTO's exact SQLite order. The
    // command reader retains that same stable order.
    private static Task<List<Room>> ActiveSidebarRoomsAsync(LogicDbContext db, Guid? groupId) => db.Rooms
        .Where(x => !x.IsArchived && x.RoomGroupId == groupId).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync();

    private static async Task MoveRoomCoreAsync(LogicDbContext db, Room room, List<Room> source, List<Room> destination, Guid? groupId, int index)
    {
        var original = source.IndexOf(room);
        var same = ReferenceEquals(source, destination);
        var target = same && original < index ? index - 1 : index;
        if (same && original == target) return;
        source.Remove(room);
        room.RoomGroupId = groupId;
        destination.Insert(target, room);
        for (var i = 0; i < source.Count; i++) source[i].SortOrder = i;
        if (!same) for (var i = 0; i < destination.Count; i++) destination[i].SortOrder = i;
        await db.SaveChangesAsync();
    }

    public Task<SidebarCommandOutcome> AutoMatchSidebarAsync(CancellationToken token) => InSidebarCommandAsync(async db =>
    {
        var count = await AutoMatchCoreAsync(db);
        return SidebarResult(count: count);
    }, token);

    private static async Task<int> AutoMatchCoreAsync(LogicDbContext db)
    {
        await new LogicReferenceResolver(db).ResolveAsync();
        var groups = await db.RoomGroups.Where(x => x.ResolvedMapZoneId != null)
            .Select(x => new RoomGroupZoneMatch(x.Id, x.ResolvedMapZoneId!.Value)).ToListAsync();
        var scenes = await db.MapScenes.Select(x => new MapSceneZoneMatch(x.MapZoneId, x.InGameId)).ToListAsync();
        var rooms = await db.Rooms.Where(x => !x.IsArchived && x.RoomGroupId == null && x.InGameId != null).ToListAsync();
        var count = rooms.Count(room => TryAssignRoomGroupFromMapZone(room, groups, scenes));
        if (count > 0) await db.SaveChangesAsync();
        return count;
    }
}
