using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class DistributedImportApplicationServiceTests
{
    private static readonly DateTime Utc = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task MigrationCurrentSqlite_StartSelectedGrouping_AdmitsRoom_ArchivesOmittedChild_AndReimportSkips()
    {
        await using var fixture = await Fixture.CreateAsync();
        var groupId = Guid.NewGuid(); var roomId = Guid.NewGuid(); var oldChild = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new Room { Id = roomId, ReferenceId = "local", FriendlyName = "local", CreatedUtc = Utc, UpdatedUtc = Utc });
            db.Add(new Subroom { Id = oldChild, RoomId = roomId, ReferenceId = "old", FriendlyName = "old", CreatedUtc = Utc, UpdatedUtc = Utc });
            await db.SaveChangesAsync();
        }
        var incoming = Room(roomId, groupId, "incoming", "incoming", Utc.AddMinutes(1));
        var package = new DistributedImportPackage(2, false, null, new DistributedRoomGroupingSnapshot([new(groupId, "group", null, 7, Utc.AddDays(-1), Utc)]), [incoming]);
        var service = new DistributedImportApplicationService(fixture, TimeProvider.System);

        var started = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(package, false, true)));
        Assert.True(started.RoomGroupings.Applied);
        var prepared = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(started.State, roomId));
        Assert.Equal(DistributedRoomComparisonKind.Changed, prepared.Comparison.Kind);
        Assert.IsType<DistributedImportRoomApplied>(await service.DecideRoomAsync(started.State, new UseIncomingRoom(roomId), prepared.Comparison));

        await using (var db = fixture.CreateDbContext())
        {
            var room = await db.Rooms.SingleAsync(x => x.Id == roomId); var child = await db.Subrooms.SingleAsync(x => x.Id == oldChild); var group = await db.RoomGroups.SingleAsync(x => x.Id == groupId);
            Assert.Equal("incoming", room.ReferenceId); Assert.Equal(groupId, room.RoomGroupId); Assert.True(child.IsArchived); Assert.NotNull(child.ArchivedUtc);
            Assert.Equal(Utc.AddDays(-1), group.CreatedUtc); Assert.Equal(Utc, group.UpdatedUtc);
        }
        Assert.IsType<DistributedImportRoomSkipped>(await service.PrepareRoomAsync(started.State, roomId));
    }

    [Fact]
    public async Task MigrationCurrentSqlite_InterveningOtherContextWriteBeforeFreshTransactionComparison_WritesNothing_AndSweepArchivesOnlyCurrentRows()
    {
        await using var fixture = await Fixture.CreateAsync(); var id = Guid.NewGuid();
        var incoming = Room(id, null, "incoming", "incoming", Utc); var package = new DistributedImportPackage(2, false, null, null, [incoming]);
        await using (var db = fixture.CreateDbContext()) { db.Add(new Room { Id=id, ReferenceId="local", FriendlyName="local", CreatedUtc=Utc, UpdatedUtc=Utc }); await db.SaveChangesAsync(); }
        var service = new DistributedImportApplicationService(fixture, TimeProvider.System);
        var started = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(package, false, false)));
        var reviewed = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(started.State,id));
        await using (var db = fixture.CreateDbContext()) { var row=await db.Rooms.SingleAsync(); row.Comments="intervening"; await db.SaveChangesAsync(); }
        var stale = Assert.IsType<DistributedImportComparisonRefreshed>(await service.DecideRoomAsync(started.State,new UseIncomingRoom(id),reviewed.Comparison));
        Assert.Equal(DistributedRoomComparisonKind.Changed, stale.Comparison.Kind);
        await using (var db = fixture.CreateDbContext()) Assert.Equal("intervening", (await db.Rooms.SingleAsync()).Comments);
        var decided = Assert.IsType<DistributedImportRoomApplied>(await service.DecideRoomAsync(started.State, new UseIncomingRoom(id), stale.Comparison));

        var omitted = Guid.NewGuid(); await using (var db = fixture.CreateDbContext()) { db.Add(new Room { Id=omitted, ReferenceId="omitted", FriendlyName="omitted", CreatedUtc=Utc, UpdatedUtc=Utc }); await db.SaveChangesAsync(); }
        var sweep = Assert.IsType<DistributedImportSweepPrepared>(await service.PrepareSweepAsync(decided.State));
        Assert.Single(sweep.Rooms);
        Assert.IsType<DistributedImportSweepCompleted>(await service.DecideSweepAsync(decided.State, sweep.Review, true));
        await using (var db = fixture.CreateDbContext()) Assert.True((await db.Rooms.SingleAsync(x=>x.Id==omitted)).IsArchived);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_RequiresStartedCompletedStages_AndAdmitsOnlyKindSpecificIntents()
    {
        await using var fixture = await Fixture.CreateAsync();
        var newId = Guid.NewGuid(); var changedId = Guid.NewGuid();
        var package = new DistributedImportPackage(2, false, null, null, [Room(newId, null, "new", "new", Utc), Room(changedId, null, "incoming", "incoming", Utc.AddMinutes(1))]);
        var service = new DistributedImportApplicationService(fixture, TimeProvider.System);
        var notStarted = new DistributedImportOrchestrationState(package, false, false, false);
        Assert.IsType<DistributedImportRejected>(await service.PrepareRoomAsync(notStarted, newId));
        Assert.IsType<DistributedImportRejected>(await service.PrepareSweepAsync(new(package, true, true, false)));

        await using (var db = fixture.CreateDbContext()) { db.Add(new Room { Id = changedId, ReferenceId = "master", FriendlyName = "master", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync(); }
        var started = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(package, false, false))).State;
        var @new = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(started, newId));
        Assert.IsType<DistributedImportRejected>(await service.DecideRoomAsync(started, new UseIncomingRoom(newId), @new.Comparison));
        Assert.IsType<DistributedImportRoomSkipped>(await service.DecideRoomAsync(started, new SkipIncomingRoom(newId), @new.Comparison));
        var changed = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(started, changedId));
        Assert.IsType<DistributedImportRejected>(await service.DecideRoomAsync(started, new ImportIncomingRoom(changedId), changed.Comparison));
        Assert.IsType<DistributedImportRoomKept>(await service.DecideRoomAsync(started, new KeepMasterRoom(changedId), changed.Comparison));
        await using var verify = fixture.CreateDbContext(); Assert.DoesNotContain(await verify.Rooms.ToListAsync(), x => x.Id == newId); Assert.Equal("master", (await verify.Rooms.SingleAsync(x => x.Id == changedId)).ReferenceId);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_StartOrdersPendingReviews_NewThenChanged_GroupSortRoomSortAndGuid()
    {
        await using var fixture = await Fixture.CreateAsync();
        var groupA = Guid.Parse("00000000-0000-0000-0000-000000000001"); var groupB = Guid.Parse("00000000-0000-0000-0000-000000000002"); var unlistedGroup = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var newAHigh = Guid.Parse("00000000-0000-0000-0000-000000000101"); var newALowLaterGuid = Guid.Parse("00000000-0000-0000-0000-000000000102"); var newALowEarlierGuid = Guid.Parse("00000000-0000-0000-0000-000000000100"); var newB = Guid.Parse("00000000-0000-0000-0000-000000000103"); var newUngrouped = Guid.Parse("00000000-0000-0000-0000-000000000104");
        var changedA = Guid.Parse("00000000-0000-0000-0000-000000000201"); var changedB = Guid.Parse("00000000-0000-0000-0000-000000000202");
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(
                new Room { Id = changedA, ReferenceId = "local-a", FriendlyName = "local-a", CreatedUtc = Utc, UpdatedUtc = Utc },
                new Room { Id = changedB, ReferenceId = "local-b", FriendlyName = "local-b", CreatedUtc = Utc, UpdatedUtc = Utc });
            await db.SaveChangesAsync();
        }
        DistributedRoomDocument Incoming(Guid id, Guid? group, int sortOrder, string name) => Room(id, group, "incoming-" + name, name, Utc.AddMinutes(1)) with { SortOrder = sortOrder };
        var package = new DistributedImportPackage(2, false, null,
            new DistributedRoomGroupingSnapshot([new(groupA, "A", null, 1, Utc, Utc), new(groupB, "B", null, 2, Utc, Utc)]),
            [Incoming(changedB, groupB, 0, "changed-b"), Incoming(newUngrouped, unlistedGroup, 0, "unlisted"), Incoming(newAHigh, groupA, 2, "new-a-high"), Incoming(changedA, groupA, 9, "changed-a"), Incoming(newB, groupB, 0, "new-b"), Incoming(newALowLaterGuid, groupA, 1, "new-a-low-later"), Incoming(newALowEarlierGuid, groupA, 1, "new-a-low-earlier")]);

        var started = Assert.IsType<DistributedImportStarted>(await new DistributedImportApplicationService(fixture, TimeProvider.System).StartAsync(new(package, false, true)));

        Assert.Equal([newALowEarlierGuid, newALowLaterGuid, newAHigh, newB, newUngrouped, changedA, changedB], started.State.ReviewRoomIds);
        Assert.Equal(7, started.State.Progress.Total);
        Assert.Equal(0, started.State.Progress.Completed);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_AreaMapGuidReconciliation_UpdatesInsertsAndRemovesOnlyLocalRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var map = Guid.NewGuid(); var zone = Guid.NewGuid(); var scene = Guid.NewGuid(); var chunk = Guid.NewGuid(); var overlay = Guid.NewGuid(); var oldMap = Guid.NewGuid(); var oldZone = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(new Map { Id=map, InGameId="old-map" }, new MapZone { Id=zone, MapId=map, InGameId="old-zone" }, new MapScene { Id=scene, MapZoneId=zone, InGameId="old-scene", RoomReferenceText="old" }, new MapChunk { Id=chunk, MapSceneId=scene, CacheIndex=1, InitialState="old" }, new MapOverlay { Id=overlay, MapId=map, FriendlyName="old", ImageAssetKey="missing-is-accepted", ScaleXPercent=1, ScaleYPercent=2, LeftOffsetPercent=3, BottomOffsetPercent=4 }, new Map { Id=oldMap, InGameId="remove" }, new MapZone { Id=oldZone, MapId=oldMap, InGameId="remove-zone" });
            db.Add(new RoomGroup { Id=Guid.NewGuid(), FriendlyName="group", ZoneReferenceText="remove-zone", ResolvedMapZoneId=oldZone, CreatedUtc=Utc, UpdatedUtc=Utc }); await db.SaveChangesAsync();
        }
        var source = new DistributedAreaMapSnapshot([new DistributedMap(map, "new-map", "map", 7, 1,2,3,4, [new DistributedMapOverlay(overlay,"overlay","not-preflighted",10,20,30,40,2)], [new DistributedMapZone(zone,"new-zone","zone",5,6,7,8,[new DistributedMapScene(scene,"new-scene","scene","unresolved",[new DistributedMapChunk(chunk,9,"new",10,11,12,13,14)])])])]);
        var package = new DistributedImportPackage(2, false, source, null, []);
        var started = Assert.IsType<DistributedImportStarted>(await new DistributedImportApplicationService(fixture, TimeProvider.System).StartAsync(new(package, true, false)));
        Assert.True(started.AreaMap.Applied);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("new-map", (await verify.Maps.SingleAsync()).InGameId); Assert.Equal(7, (await verify.Maps.SingleAsync()).SortOrder);
        Assert.Equal("new-zone", (await verify.MapZones.SingleAsync()).InGameId); Assert.Equal("unresolved", (await verify.MapScenes.SingleAsync()).RoomReferenceText);
        Assert.Equal(9, (await verify.MapChunks.SingleAsync()).CacheIndex); Assert.Equal("not-preflighted", (await verify.MapOverlays.SingleAsync()).ImageAssetKey);
        Assert.Null((await verify.RoomGroups.SingleAsync()).ResolvedMapZoneId);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_SelectedUnselectedAndAbsentSnapshots_HaveOnlyTheirDocumentedEffects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mapId = Guid.NewGuid(); var groupId = Guid.NewGuid(); var roomId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new Map { Id = mapId, InGameId = "local-map" });
            db.Add(new RoomGroup { Id = groupId, FriendlyName = "local-group", SortOrder = 1, CreatedUtc = Utc, UpdatedUtc = Utc });
            db.Add(new Room { Id = roomId, RoomGroupId = groupId, ReferenceId = "room", FriendlyName = "room", SortOrder = 19, CreatedUtc = Utc, UpdatedUtc = Utc });
            await db.SaveChangesAsync();
        }
        var map = new DistributedAreaMapSnapshot([new(mapId, "incoming-map", null, 4, null, null, null, null, [], [])]);
        var groups = new DistributedRoomGroupingSnapshot([new(groupId, "incoming-group", null, 7, Utc.AddDays(-2), Utc.AddDays(-1))]);
        var service = new DistributedImportApplicationService(fixture, TimeProvider.System);
        var skipped = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, map, groups, []), false, false)));
        Assert.True(skipped.AreaMap.Skipped); Assert.True(skipped.RoomGroupings.Skipped);
        await using (var db = fixture.CreateDbContext()) { Assert.Equal("local-map", (await db.Maps.SingleAsync()).InGameId); Assert.Equal("local-group", (await db.RoomGroups.SingleAsync()).FriendlyName); }
        Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, map, groups, []), true, true)));
        await using (var db = fixture.CreateDbContext()) { Assert.Equal("incoming-map", (await db.Maps.SingleAsync()).InGameId); var group = await db.RoomGroups.SingleAsync(); Assert.Equal("incoming-group", group.FriendlyName); Assert.Equal(Utc.AddDays(-2), group.CreatedUtc); Assert.Equal(Utc.AddDays(-1), group.UpdatedUtc); }
        var absent = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, null, null, []), true, true)));
        Assert.False(absent.AreaMap.Applied); Assert.False(absent.RoomGroupings.Applied);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_GroupReplacementInsertionDeletionUngroupsActiveAndArchivedAndPreservesResolverOwnership()
    {
        await using var fixture = await Fixture.CreateAsync(); var old = Guid.NewGuid(); var retained = Guid.NewGuid(); var inserted = Guid.NewGuid();
        var active = Guid.NewGuid(); var archived = Guid.NewGuid(); var zone = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            var map = new Map { Id = Guid.NewGuid(), InGameId = "map" }; db.Add(map); db.Add(new MapZone { Id = zone, MapId = map.Id, InGameId = "zone" });
            db.AddRange(new RoomGroup { Id = old, FriendlyName = "remove", SortOrder = 1, CreatedUtc = Utc, UpdatedUtc = Utc }, new RoomGroup { Id = retained, FriendlyName = "old", ZoneReferenceText = "old", SortOrder = 2, CreatedUtc = Utc, UpdatedUtc = Utc });
            db.AddRange(new Room { Id = active, RoomGroupId = old, ReferenceId = "a", FriendlyName = "a", SortOrder = 11, CreatedUtc = Utc, UpdatedUtc = Utc }, new Room { Id = archived, RoomGroupId = old, ReferenceId = "b", FriendlyName = "b", SortOrder = 12, IsArchived = true, ArchivedUtc = Utc, CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync();
        }
        var groups = new DistributedRoomGroupingSnapshot([new(retained, "exact", " zone ", 4, Utc.AddDays(-4), Utc.AddDays(-3)), new(inserted, "inserted", null, 5, Utc.AddDays(-2), Utc.AddDays(-1))]);
        Assert.IsType<DistributedImportStarted>(await new DistributedImportApplicationService(fixture, TimeProvider.System).StartAsync(new(new(2, false, null, groups, []), false, true)));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(2, await verify.RoomGroups.CountAsync()); var exact = await verify.RoomGroups.SingleAsync(x => x.Id == retained);
        Assert.Equal((Guid?)zone, exact.ResolvedMapZoneId); Assert.Equal(" zone ", exact.ZoneReferenceText); Assert.Equal(Utc.AddDays(-4), exact.CreatedUtc); Assert.Equal(Utc.AddDays(-3), exact.UpdatedUtc);
        Assert.All(await verify.Rooms.Where(x => x.Id == active || x.Id == archived).ToArrayAsync(), r => { Assert.Null(r.RoomGroupId); Assert.Equal(r.Id == active ? 11 : 12, r.SortOrder); });
    }

    [Fact]
    public async Task MigrationCurrentSqlite_RoomFallbackAndOmittedChildren_ArchiveOnlyActiveRowsAtOperationTime()
    {
        var operationTime = Utc.AddHours(3); await using var fixture = await Fixture.CreateAsync(); var roomId = Guid.NewGuid();
        var sub = Guid.NewGuid(); var transition = Guid.NewGuid(); var connection = Guid.NewGuid(); var check = Guid.NewGuid(); var archivedSub = Guid.NewGuid(); var missingGroup = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new Room { Id = roomId, ReferenceId = "old", FriendlyName = "old", SortOrder = 41, CreatedUtc = Utc, UpdatedUtc = Utc });
            db.AddRange(new Subroom { Id = sub, RoomId = roomId, ReferenceId = "s", FriendlyName = "s", CreatedUtc = Utc, UpdatedUtc = Utc }, new Subroom { Id = archivedSub, RoomId = roomId, ReferenceId = "as", FriendlyName = "as", IsArchived = true, ArchivedUtc = Utc.AddDays(-1), CreatedUtc = Utc, UpdatedUtc = Utc.AddDays(-1) }, new RoomTransition { Id = transition, RoomId = roomId, Alias = "t", FriendlyName = "t", Requirements = "", Notes = "", CreatedUtc = Utc, UpdatedUtc = Utc }, new SubroomConnection { Id = connection, RoomId = roomId, Alias = "c", FriendlyName = "c", SourceSubroomReferenceText = "", DestinationSubroomReferenceText = "", Requirements = "", Notes = "", CreatedUtc = Utc, UpdatedUtc = Utc }, new CheckLocation { Id = check, RoomId = roomId, FriendlyName = "c", Requirements = "", Notes = "", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync($"UPDATE \"Subrooms\" SET \"ArchivedUtc\" = {Utc.AddDays(-1)}, \"UpdatedUtc\" = {Utc.AddDays(-1)} WHERE \"Id\" = {archivedSub}");
        }
        var incoming = Room(roomId, missingGroup, "new", "new", Utc.AddMinutes(1)) with { SortOrder = 99 };
        var service = new DistributedImportApplicationService(fixture, new FixedTimeProvider(operationTime)); var state = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, null, null, [incoming]), false, false))).State;
        var review = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(state, roomId)); Assert.IsType<DistributedImportRoomApplied>(await service.DecideRoomAsync(state, new UseIncomingRoom(roomId), review.Comparison));
        await using var verify = fixture.CreateDbContext(); var room = await verify.Rooms.SingleAsync(); Assert.Null(room.RoomGroupId); Assert.Equal(99, room.SortOrder);
        foreach (var row in new ArchivableEntity[] { await verify.Subrooms.SingleAsync(x => x.Id == sub), await verify.RoomTransitions.SingleAsync(x => x.Id == transition), await verify.SubroomConnections.SingleAsync(x => x.Id == connection), await verify.CheckLocations.SingleAsync(x => x.Id == check) }) { Assert.True(row.IsArchived); Assert.Equal(operationTime, row.ArchivedUtc); Assert.Equal(operationTime, row.UpdatedUtc); }
        var already = await verify.Subrooms.SingleAsync(x => x.Id == archivedSub); Assert.Equal(Utc.AddDays(-1), already.ArchivedUtc); Assert.Equal(Utc.AddDays(-1), already.UpdatedUtc);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_MapGroupRoomAndSweepFailures_RollBackOnlyTheirOwnTransaction()
    {
        foreach (var target in new[] { "Maps", "RoomGroups", "Rooms" })
        {
            var trap = new ThrowingCommandInterceptor(target); await using var fixture = await Fixture.CreateAsync(trap);
            var id = Guid.NewGuid(); await using (var db = fixture.CreateDbContext()) { db.Add(new Room { Id = id, ReferenceId = "local", FriendlyName = "local", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync(); }
            var map = new DistributedAreaMapSnapshot([new(Guid.NewGuid(), "map", null, 0, null, null, null, null, [], [])]);
            var groups = new DistributedRoomGroupingSnapshot([new(Guid.NewGuid(), "group", null, 0, Utc, Utc)]);
            var incoming = Room(id, null, "incoming", "incoming", Utc.AddMinutes(1)); var service = new DistributedImportApplicationService(fixture, TimeProvider.System);
            trap.Arm();
            if (target == "Maps") Assert.IsType<DistributedImportFailed>(await service.StartAsync(new(new(2, false, map, null, []), true, false)));
            else if (target == "RoomGroups") Assert.IsType<DistributedImportFailed>(await service.StartAsync(new(new(2, false, null, groups, []), false, true)));
            else { var state = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, null, null, [incoming]), false, false))).State; var review = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(state, id)); Assert.IsType<DistributedImportFailed>(await service.DecideRoomAsync(state, new UseIncomingRoom(id), review.Comparison)); }
            await using var verify = fixture.CreateDbContext(); Assert.Equal("local", (await verify.Rooms.SingleAsync()).ReferenceId);
        }
        var sweepTrap = new ThrowingCommandInterceptor("Rooms"); await using var sweepFixture = await Fixture.CreateAsync(sweepTrap); var omitted = Guid.NewGuid();
        await using (var db = sweepFixture.CreateDbContext()) { db.Add(new Room { Id = omitted, ReferenceId = "o", FriendlyName = "o", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync(); }
        var sweepService = new DistributedImportApplicationService(sweepFixture, TimeProvider.System); var state2 = Assert.IsType<DistributedImportStarted>(await sweepService.StartAsync(new(new(2, false, null, null, []), false, false))).State;
        sweepTrap.Arm();
        Assert.IsType<DistributedImportFailed>(await sweepService.DecideSweepAsync(state2, new([omitted]), true)); await using var sweepVerify = sweepFixture.CreateDbContext(); Assert.False((await sweepVerify.Rooms.SingleAsync()).IsArchived);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_RetryAfterLaterStageFailure_RetainsCompletedAreaMapTypedOutcome()
    {
        var trap = new ThrowingCommandInterceptor("RoomGroups");
        await using var fixture = await Fixture.CreateAsync(trap);
        var map = new DistributedAreaMapSnapshot([new(Guid.NewGuid(), "committed-map", null, 0, null, null, null, null, [], [])]);
        var groups = new DistributedRoomGroupingSnapshot([new(Guid.NewGuid(), "group", null, 0, Utc, Utc)]);
        var package = new DistributedImportPackage(2, false, map, groups, []);
        var service = new DistributedImportApplicationService(fixture, TimeProvider.System);

        trap.Arm();
        var failed = Assert.IsType<DistributedImportFailed>(await service.StartAsync(new(package, true, true)));
        Assert.NotNull(failed.State);
        Assert.True(failed.State.AreaMapOutcome.Applied);
        Assert.False(failed.State.AreaMapOutcome.Skipped);
        var committedMapOutcome = failed.State.AreaMapOutcome;

        trap.Disarm();
        var retried = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(package, true, true), failed.State));
        Assert.Equal(committedMapOutcome, retried.AreaMap);
        Assert.Equal(committedMapOutcome, retried.State.AreaMapOutcome);
        Assert.True(retried.AreaMap.Applied);
        Assert.False(retried.AreaMap.Skipped);
        Assert.True(retried.RoomGroupings.Applied);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal("committed-map", (await verify.Maps.SingleAsync()).InGameId);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_RetryAfterRoomFailure_RetainsAppliedMapAndGroupingOutcomesWithAccurateDetails()
    {
        var trap = new ThrowingCommandInterceptor("Rooms");
        await using var fixture = await Fixture.CreateAsync(trap);
        var roomId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new Room { Id = roomId, ReferenceId = "local", FriendlyName = "local", CreatedUtc = Utc, UpdatedUtc = Utc });
            await db.SaveChangesAsync();
        }

        var package = new DistributedImportPackage(
            2,
            false,
            new DistributedAreaMapSnapshot([new(Guid.NewGuid(), "applied-map", null, 0, null, null, null, null, [], [])]),
            new DistributedRoomGroupingSnapshot([new(Guid.NewGuid(), "applied-group", null, 0, Utc, Utc)]),
            [Room(roomId, null, "incoming", "incoming", Utc.AddMinutes(1))]);
        var service = new DistributedImportApplicationService(fixture, TimeProvider.System);

        var started = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(package, true, true)));
        Assert.Equal(new DistributedImportStageOutcome("area-map", true, false, null), started.AreaMap);
        Assert.Equal(new DistributedImportStageOutcome("room-groupings", true, false, null), started.RoomGroupings);
        var review = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(started.State, roomId));

        trap.Arm();
        var failed = Assert.IsType<DistributedImportFailed>(await service.DecideRoomAsync(started.State, new UseIncomingRoom(roomId), review.Comparison));
        trap.Disarm();
        Assert.NotNull(failed.State);
        Assert.Equal(started.AreaMap, failed.State.AreaMapOutcome);
        Assert.Equal(started.RoomGroupings, failed.State.RoomGroupingOutcome);

        var retried = Assert.IsType<DistributedImportRoomApplied>(await service.DecideRoomAsync(failed.State, new UseIncomingRoom(roomId), review.Comparison));
        Assert.Equal(started.AreaMap, retried.State.AreaMapOutcome);
        Assert.Equal(started.RoomGroupings, retried.State.RoomGroupingOutcome);
        Assert.True(retried.State.AreaMapOutcome.Applied);
        Assert.True(retried.State.RoomGroupingOutcome.Applied);
        Assert.Null(retried.State.AreaMapOutcome.Detail);
        Assert.Null(retried.State.RoomGroupingOutcome.Detail);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_CompleteAndPartialSweepsAndReimportKeepEarlierDurableStages()
    {
        var now = Utc.AddHours(4); await using var fixture = await Fixture.CreateAsync(); var included = Guid.NewGuid(); var omitted = Guid.NewGuid(); var partialOmitted = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext()) { db.AddRange(new Room { Id = included, ReferenceId = "old", FriendlyName = "old", CreatedUtc = Utc, UpdatedUtc = Utc }, new Room { Id = omitted, ReferenceId = "omit", FriendlyName = "omit", Comments = "author changed", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync(); }
        var incoming = Room(included, null, "new", "new", Utc.AddMinutes(1)); var service = new DistributedImportApplicationService(fixture, new FixedTimeProvider(now));
        var state = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, null, null, [incoming]), false, false))).State; var review = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(state, included)); state = Assert.IsType<DistributedImportRoomApplied>(await service.DecideRoomAsync(state, new UseIncomingRoom(included), review.Comparison)).State;
        var sweep = Assert.IsType<DistributedImportSweepPrepared>(await service.PrepareSweepAsync(state));
        Assert.IsType<DistributedImportSweepCompleted>(await service.DecideSweepAsync(state, sweep.Review, false)); await using (var unchanged = fixture.CreateDbContext()) Assert.False((await unchanged.Rooms.SingleAsync(x => x.Id == omitted)).IsArchived);
        Assert.IsType<DistributedImportSweepCompleted>(await service.DecideSweepAsync(state, sweep.Review, true));
        await using (var verify = fixture.CreateDbContext()) { var archived = await verify.Rooms.SingleAsync(x => x.Id == omitted); Assert.True(archived.IsArchived); Assert.Equal("author changed", archived.Comments); Assert.Equal(now, archived.ArchivedUtc); Assert.Equal(now, archived.UpdatedUtc); }
        await using (var db = fixture.CreateDbContext()) { db.Add(new Room { Id = partialOmitted, ReferenceId = "partial", FriendlyName = "partial", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync(); }
        var partial = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, true, null, new DistributedRoomGroupingSnapshot([]), [incoming]), false, true))).State; Assert.Empty(Assert.IsType<DistributedImportSweepPrepared>(await service.PrepareSweepAsync(partial)).Rooms); await using var final = fixture.CreateDbContext(); Assert.False((await final.Rooms.SingleAsync(x => x.Id == partialOmitted)).IsArchived); Assert.Equal("new", (await final.Rooms.SingleAsync(x => x.Id == included)).ReferenceId);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_QueryShapeIsBoundedAndSameGameWarningSurvivesEveryRoomDecision()
    {
        var trace = new CountingCommandInterceptor(); await using var fixture = await Fixture.CreateAsync(trace); var current = Guid.NewGuid(); var incomingId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext()) { db.Add(new Room { Id = current, ReferenceId = "current", FriendlyName = "current", InGameId = "game", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync(); }
        var incoming = Room(incomingId, null, "incoming", "incoming", Utc); var service = new DistributedImportApplicationService(fixture, TimeProvider.System); var state = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, null, null, [incoming]), false, false))).State;
        trace.Reset(); var prepared = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(state, incomingId)); Assert.Contains(prepared.Comparison.Warnings, x => x.Code == "same-game-id"); Assert.IsType<DistributedImportRoomSkipped>(await service.DecideRoomAsync(state, new SkipIncomingRoom(incomingId), prepared.Comparison));
        Assert.InRange(trace.ReaderCount, 1, 12); Assert.InRange(trace.CommandCount, 1, 12);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_ImportOperationSqlTraceIsBoundedForValidationStagesRoomAndSweep()
    {
        var trace = new CountingCommandInterceptor(); await using var fixture = await Fixture.CreateAsync(trace);
        var service = new DistributedImportApplicationService(fixture, TimeProvider.System);

        trace.Reset();
        Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, null, null, []), false, false)));
        Assert.Equal(15, trace.ReaderCount); Assert.Equal(15, trace.CommandCount); // validation: exchange identities plus four child-owner projections

        var mapId = Guid.NewGuid(); var zoneId = Guid.NewGuid(); var map = new DistributedAreaMapSnapshot([new(mapId, "map", null, 0, null, null, null, null, [], [new(zoneId, "zone", null, null, null, null, null, [])])]);
        trace.Reset();
        Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, map, null, []), true, false)));
        Assert.InRange(trace.ReaderCount, 20, 30); Assert.InRange(trace.CommandCount, 20, 35); // validation + map apply + one resolver pass

        var groupId = Guid.NewGuid(); var groups = new DistributedRoomGroupingSnapshot([new(groupId, "group", "zone", 0, Utc, Utc)]);
        trace.Reset();
        Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, null, groups, []), false, true)));
        Assert.InRange(trace.ReaderCount, 20, 30); Assert.InRange(trace.CommandCount, 20, 35); // validation + group apply + one resolver pass

        var roomId = Guid.NewGuid(); await using (var db = fixture.CreateDbContext()) { db.Add(new Room { Id = roomId, ReferenceId = "local", FriendlyName = "local", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync(); }
        var roomPackage = new DistributedImportPackage(2, false, null, null, [Room(roomId, null, "incoming", "incoming", Utc.AddMinutes(1))]);
        var state = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(roomPackage, false, false))).State;
        var review = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(state, roomId));
        trace.Reset();
        state = Assert.IsType<DistributedImportRoomApplied>(await service.DecideRoomAsync(state, new UseIncomingRoom(roomId), review.Comparison)).State;
        Assert.InRange(trace.ReaderCount, 15, 30); Assert.InRange(trace.CommandCount, 16, 35); // fresh comparison, whole-room replacement, resolver

        var omitted = Guid.NewGuid(); await using (var db = fixture.CreateDbContext()) { db.Add(new Room { Id = omitted, ReferenceId = "omitted", FriendlyName = "omitted", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync(); }
        trace.Reset();
        var sweep = Assert.IsType<DistributedImportSweepPrepared>(await service.PrepareSweepAsync(state));
        Assert.IsType<DistributedImportSweepCompleted>(await service.DecideSweepAsync(state, sweep.Review, true));
        Assert.InRange(trace.ReaderCount, 2, 4); Assert.InRange(trace.CommandCount, 3, 6); // reviewed-list read, active-ID reload, one update
    }

    [Fact]
    public async Task MigrationCurrentSqlite_InterruptionAfterCommittedMap_ReimportResumesAndPreservesIncomingArchiveTimestamps()
    {
        var trap = new ThrowingCommandInterceptor("Rooms"); await using var fixture = await Fixture.CreateAsync(trap); var mapId = Guid.NewGuid(); var roomId = Guid.NewGuid(); var archiveTime = Utc.AddDays(-5);
        var map = new DistributedAreaMapSnapshot([new(mapId, "durable-map", null, 3, null, null, null, null, [], [])]);
        var incoming = Room(roomId, null, "archived", "archived", Utc.AddMinutes(1)) with { IsArchived = true, ArchivedUtc = archiveTime, CreatedUtc = Utc.AddDays(-6), UpdatedUtc = Utc.AddDays(-4) };
        var package = new DistributedImportPackage(2, false, map, null, [incoming]); var service = new DistributedImportApplicationService(fixture, TimeProvider.System);
        var state = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(package, true, false))).State; await using (var mapCheck = fixture.CreateDbContext()) Assert.Equal("durable-map", (await mapCheck.Maps.SingleAsync()).InGameId);
        var review = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(state, roomId)); trap.Arm(); Assert.IsType<DistributedImportFailed>(await service.DecideRoomAsync(state, new ImportIncomingRoom(roomId), review.Comparison)); trap.Disarm();
        await using (var durable = fixture.CreateDbContext()) { Assert.Equal("durable-map", (await durable.Maps.SingleAsync()).InGameId); Assert.Empty(await durable.Rooms.ToListAsync()); }
        // A reopened wizard has no retained state; revalidation and reimport operate from
        // the committed SQLite map state and apply the still-pending room.
        var retried = new DistributedImportApplicationService(fixture, TimeProvider.System); var resumed = Assert.IsType<DistributedImportStarted>(await retried.StartAsync(new(package, false, false))).State;
        var fresh = Assert.IsType<DistributedImportRoomPrepared>(await retried.PrepareRoomAsync(resumed, roomId)); Assert.IsType<DistributedImportRoomApplied>(await retried.DecideRoomAsync(resumed, new ImportIncomingRoom(roomId), fresh.Comparison));
        await using var verify = fixture.CreateDbContext(); var row = await verify.Rooms.SingleAsync(); Assert.True(row.IsArchived); Assert.Equal(archiveTime, row.ArchivedUtc); Assert.Equal(Utc.AddDays(-6), row.CreatedUtc); Assert.Equal(Utc.AddDays(-4), row.UpdatedUtc);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_SweepUsesOnlyReviewedIdsAndArchivesThemWithoutOverwritingLaterAuthoredEdits()
    {
        var operationTime = Utc.AddHours(8); await using var fixture = await Fixture.CreateAsync();
        var reviewedId = Guid.NewGuid(); var createdAfterReviewId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new Room { Id = reviewedId, ReferenceId = "reviewed", FriendlyName = "reviewed", Comments = "before", CreatedUtc = Utc, UpdatedUtc = Utc });
            await db.SaveChangesAsync();
        }
        var service = new DistributedImportApplicationService(fixture, new FixedTimeProvider(operationTime));
        var state = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, null, null, []), false, false))).State;
        var prepared = Assert.IsType<DistributedImportSweepPrepared>(await service.PrepareSweepAsync(state));
        Assert.Equal([reviewedId], prepared.Review.RoomIds);
        await using (var db = fixture.CreateDbContext())
        {
            var reviewed = await db.Rooms.SingleAsync(x => x.Id == reviewedId);
            reviewed.Comments = "authored after review";
            db.Add(new Room { Id = createdAfterReviewId, ReferenceId = "new", FriendlyName = "new", CreatedUtc = Utc, UpdatedUtc = Utc });
            await db.SaveChangesAsync();
        }
        Assert.IsType<DistributedImportSweepCompleted>(await service.DecideSweepAsync(state, prepared.Review, true));
        await using var verify = fixture.CreateDbContext();
        var archived = await verify.Rooms.SingleAsync(x => x.Id == reviewedId);
        Assert.True(archived.IsArchived); Assert.Equal("authored after review", archived.Comments); Assert.Equal(operationTime, archived.ArchivedUtc); Assert.Equal(operationTime, archived.UpdatedUtc);
        Assert.False((await verify.Rooms.SingleAsync(x => x.Id == createdAfterReviewId)).IsArchived);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_AreaMapReconciliationStagesEveryStructuralIdentitySwap()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mapA = Guid.NewGuid(); var mapB = Guid.NewGuid(); var zoneA = Guid.NewGuid(); var zoneB = Guid.NewGuid(); var sceneA = Guid.NewGuid(); var sceneB = Guid.NewGuid(); var chunkA = Guid.NewGuid(); var chunkB = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(new Map { Id = mapA, InGameId = "map-a" }, new Map { Id = mapB, InGameId = "map-b" },
                new MapZone { Id = zoneA, MapId = mapA, InGameId = "zone-a" }, new MapZone { Id = zoneB, MapId = mapA, InGameId = "zone-b" },
                new MapScene { Id = sceneA, MapZoneId = zoneA, InGameId = "scene-a" }, new MapScene { Id = sceneB, MapZoneId = zoneA, InGameId = "scene-b" },
                new MapChunk { Id = chunkA, MapSceneId = sceneA, CacheIndex = 1 }, new MapChunk { Id = chunkB, MapSceneId = sceneA, CacheIndex = 2 });
            await db.SaveChangesAsync();
        }
        var snapshot = new DistributedAreaMapSnapshot([
            new(mapA, "map-b", null, 0, null, null, null, null, [], [
                new(zoneA, "zone-b", null, null, null, null, null, [
                    new(sceneA, "scene-b", null, null, [new(chunkA, 2, null, null, null, null, null, null)]),
                    new(sceneB, "scene-a", null, null, [new(chunkB, 1, null, null, null, null, null, null)])]),
                new(zoneB, "zone-a", null, null, null, null, null, [])]),
            new(mapB, "map-a", null, 1, null, null, null, null, [], [])]);
        Assert.IsType<DistributedImportStarted>(await new DistributedImportApplicationService(fixture, TimeProvider.System).StartAsync(new(new(2, false, snapshot, null, []), true, false)));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("map-b", (await verify.Maps.SingleAsync(x => x.Id == mapA)).InGameId); Assert.Equal("map-a", (await verify.Maps.SingleAsync(x => x.Id == mapB)).InGameId);
        Assert.Equal("zone-b", (await verify.MapZones.SingleAsync(x => x.Id == zoneA)).InGameId); Assert.Equal("zone-a", (await verify.MapZones.SingleAsync(x => x.Id == zoneB)).InGameId);
        Assert.Equal("scene-b", (await verify.MapScenes.SingleAsync(x => x.Id == sceneA)).InGameId); Assert.Equal("scene-a", (await verify.MapScenes.SingleAsync(x => x.Id == sceneB)).InGameId);
        Assert.Equal(2, (await verify.MapChunks.SingleAsync(x => x.Id == chunkA)).CacheIndex); Assert.Equal(1, (await verify.MapChunks.SingleAsync(x => x.Id == chunkB)).CacheIndex);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_MapInsertionAndMapGroupResolutionRecomputeOnlyResolverIds()
    {
        await using var fixture = await Fixture.CreateAsync(); var roomId = Guid.NewGuid(); var groupId = Guid.NewGuid(); var mapId = Guid.NewGuid(); var zoneId = Guid.NewGuid(); var sceneId = Guid.NewGuid();
        var created = Utc.AddDays(-3); var updated = Utc.AddDays(-2);
        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new Room { Id = roomId, ReferenceId = "room-ref", FriendlyName = "room", CreatedUtc = Utc, UpdatedUtc = Utc });
            await db.SaveChangesAsync();
        }
        var map = new DistributedAreaMapSnapshot([new(mapId, "inserted-map", null, 0, null, null, null, null, [], [new(zoneId, "zone-ref", null, null, null, null, null, [new(sceneId, "scene", null, "room-ref", [])])])]);
        var groups = new DistributedRoomGroupingSnapshot([new(groupId, "group", " zone-ref ", 4, created, updated)]);
        Assert.IsType<DistributedImportStarted>(await new DistributedImportApplicationService(fixture, TimeProvider.System).StartAsync(new(new(2, false, map, groups, []), true, true)));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("inserted-map", (await verify.Maps.SingleAsync(x => x.Id == mapId)).InGameId);
        var scene = await verify.MapScenes.SingleAsync(x => x.Id == sceneId); var group = await verify.RoomGroups.SingleAsync(x => x.Id == groupId);
        Assert.Equal("room-ref", scene.RoomReferenceText); Assert.Equal(roomId, scene.ResolvedRoomId);
        Assert.Equal(" zone-ref ", group.ZoneReferenceText); Assert.Equal(zoneId, group.ResolvedMapZoneId); Assert.Equal(created, group.CreatedUtc); Assert.Equal(updated, group.UpdatedUtc);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_UseIncomingCompleteRoom_ReplacesSharedInsertsIncomingOnlyAndPreservesEveryExchangedValue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var roomId = Guid.NewGuid(); var groupId = Guid.NewGuid();
        var sharedSubroomId = Guid.NewGuid(); var insertedSubroomId = Guid.NewGuid();
        var sharedTransitionId = Guid.NewGuid(); var insertedTransitionId = Guid.NewGuid();
        var sharedConnectionId = Guid.NewGuid(); var insertedConnectionId = Guid.NewGuid();
        var sharedCheckId = Guid.NewGuid(); var insertedCheckId = Guid.NewGuid();
        var incomingCreated = Utc.AddDays(-10); var incomingUpdated = Utc.AddDays(-9); var incomingArchived = Utc.AddDays(-8);
        var incoming = new DistributedRoomDocument(
            roomId, groupId, "incoming-room", "Incoming room", "incoming-game", "contributors", "comments", 101.1, 202.2, 55.5, 66.6, -7.7, 8.8, true, 23, false, null, incomingCreated, incomingUpdated,
            [
                new(sharedSubroomId, "shared-subroom", "Shared subroom", "shared notes", 1.1, 2.2, 3.3, 4.4, false, 2, false, null, incomingCreated, incomingUpdated),
                new(insertedSubroomId, "archived-subroom", "Archived subroom", null, null, null, null, null, true, 3, true, incomingArchived, incomingCreated.AddMinutes(1), incomingUpdated.AddMinutes(1))
            ],
            [
                new(sharedTransitionId, "ST", "Shared transition", "transition-game", 1.1, 2.2, 3.3, 4.4, 5.5, 6.6, 7.7, 8.8, false, "shared-subroom", "incoming-room", "ST", "transition requirements", "transition notes", 4, true, false, false, null, incomingCreated, incomingUpdated),
                new(insertedTransitionId, "AT", "Archived transition", null, null, null, null, null, null, null, null, null, true, null, null, null, "", "", 5, false, null, true, incomingArchived, incomingCreated.AddMinutes(2), incomingUpdated.AddMinutes(2))
            ],
            [
                new(sharedConnectionId, "SC", "Shared connection", "shared-subroom", "shared-subroom", "connection requirements", "connection notes", false, 9.9, 10.1, 6, true, true, false, null, incomingCreated, incomingUpdated),
                new(insertedConnectionId, "AC", "Archived connection", "archived-subroom", "archived-subroom", "", "", true, null, null, 7, false, null, true, incomingArchived, incomingCreated.AddMinutes(3), incomingUpdated.AddMinutes(3))
            ],
            [
                new(sharedCheckId, "Shared check", "check-game", 11.1, 12.2, 13.3, 14.4, 15.5, 16.6, 17.7, 18.8, "shared-subroom", "check requirements", "check notes", false, false, 8, true, true, false, null, incomingCreated, incomingUpdated),
                new(insertedCheckId, "Archived check", null, null, null, null, null, null, null, null, null, "archived-subroom", "", "", true, true, 9, false, null, true, incomingArchived, incomingCreated.AddMinutes(4), incomingUpdated.AddMinutes(4))
            ]);

        await using (var db = fixture.CreateDbContext())
        {
            db.Add(new RoomGroup { Id = groupId, FriendlyName = "group", CreatedUtc = Utc, UpdatedUtc = Utc });
            db.Add(new Room { Id = roomId, ReferenceId = "local-room", FriendlyName = "Local room", CreatedUtc = Utc, UpdatedUtc = Utc });
            db.AddRange(
                new Subroom { Id = sharedSubroomId, RoomId = roomId, ReferenceId = "local-subroom", FriendlyName = "Local subroom", Notes = "local", CreatedUtc = Utc, UpdatedUtc = Utc },
                new RoomTransition { Id = sharedTransitionId, RoomId = roomId, Alias = "LT", FriendlyName = "Local transition", Requirements = "local", Notes = "local", CreatedUtc = Utc, UpdatedUtc = Utc },
                new SubroomConnection { Id = sharedConnectionId, RoomId = roomId, Alias = "LC", FriendlyName = "Local connection", SourceSubroomReferenceText = "local-subroom", DestinationSubroomReferenceText = "local-subroom", Requirements = "local", Notes = "local", CreatedUtc = Utc, UpdatedUtc = Utc },
                new CheckLocation { Id = sharedCheckId, RoomId = roomId, FriendlyName = "Local check", Requirements = "local", Notes = "local", CreatedUtc = Utc, UpdatedUtc = Utc });
            await db.SaveChangesAsync();
        }

        var service = new DistributedImportApplicationService(fixture, new FixedTimeProvider(Utc.AddHours(1)));
        var state = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, null, null, [incoming]), false, false))).State;
        var reviewed = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(state, roomId));
        Assert.IsType<DistributedImportRoomApplied>(await service.DecideRoomAsync(state, new UseIncomingRoom(roomId), reviewed.Comparison));

        var loader = new DistributedImportProjectionLoader(fixture);
        AssertDocumentEquals(incoming, (await loader.LoadRoomAsync(roomId)) is { } projection ? DistributedImportProjectionMapper.MapRoom(projection) : null);

        await using var verify = fixture.CreateDbContext();
        var transition = await verify.RoomTransitions.SingleAsync(x => x.Id == sharedTransitionId);
        var connection = await verify.SubroomConnections.SingleAsync(x => x.Id == sharedConnectionId);
        var check = await verify.CheckLocations.SingleAsync(x => x.Id == sharedCheckId);
        Assert.Equal(sharedSubroomId, transition.ResolvedSourceSubroomId); Assert.Equal(roomId, transition.ResolvedDestinationRoomId); Assert.Equal(sharedTransitionId, transition.ResolvedDestinationTransitionId);
        Assert.Equal(sharedSubroomId, connection.ResolvedSourceSubroomId); Assert.Equal(sharedSubroomId, connection.ResolvedDestinationSubroomId); Assert.Equal(sharedSubroomId, check.ResolvedSubroomId);
        Assert.Equal(incomingCreated, (await verify.Rooms.SingleAsync(x => x.Id == roomId)).CreatedUtc); Assert.Equal(incomingUpdated, (await verify.Rooms.SingleAsync(x => x.Id == roomId)).UpdatedUtc);
    }

    private static void AssertDocumentEquals(DistributedRoomDocument expected, DistributedRoomDocument? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected with { Subrooms = actual.Subrooms, Transitions = actual.Transitions, Connections = actual.Connections, Checks = actual.Checks }, actual);
        Assert.Equal(expected.Subrooms, actual.Subrooms); Assert.Equal(expected.Transitions, actual.Transitions);
        Assert.Equal(expected.Connections, actual.Connections); Assert.Equal(expected.Checks, actual.Checks);
    }


    private static DistributedRoomDocument Room(Guid id, Guid? group, string reference, string name, DateTime updated) => new(id, group, reference, name, "game", null, null, null, null, null, null, null, null, false, 3, false, null, Utc, updated, [], [], [], []);

    [Fact]
    public async Task MigrationCurrentSqlite_EveryNonIdenticalRoomRemainsPendingUntilExplicitlyDecided_AndBlocksSweep()
    {
        await using var fixture = await Fixture.CreateAsync();
        var newId = Guid.NewGuid(); var changedId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext()) { db.Add(new Room { Id = changedId, ReferenceId = "local", FriendlyName = "local", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync(); }
        var package = new DistributedImportPackage(2, false, null, null, [Room(newId, null, "new", "new", Utc), Room(changedId, null, "changed", "changed", Utc.AddMinutes(1))]);
        var service = new DistributedImportApplicationService(fixture, TimeProvider.System);
        var state = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(package, false, false))).State;
        Assert.Equal(2, state.PendingRoomIds.Count);
        var @new = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(state, newId));
        var changed = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(state, changedId));
        Assert.IsType<DistributedImportRejected>(await service.PrepareSweepAsync(state));
        state = Assert.IsType<DistributedImportRoomSkipped>(await service.DecideRoomAsync(state, new SkipIncomingRoom(newId), @new.Comparison)).State;
        Assert.Single(state.PendingRoomIds); Assert.IsType<DistributedImportRejected>(await service.PrepareSweepAsync(state));
        state = Assert.IsType<DistributedImportRoomKept>(await service.DecideRoomAsync(state, new KeepMasterRoom(changedId), changed.Comparison)).State;
        Assert.Empty(state.PendingRoomIds); Assert.IsType<DistributedImportSweepPrepared>(await service.PrepareSweepAsync(state));
        await using var verify = fixture.CreateDbContext(); Assert.Equal("local", (await verify.Rooms.SingleAsync()).ReferenceId);
    }

    [Fact]
    public async Task MigrationCurrentSqlite_UnexpectedRoomFaultPropagatesInsteadOfProducingRetryOutcome()
    {
        var fault = new UnexpectedFaultInterceptor(); await using var fixture = await Fixture.CreateAsync(fault);
        var id = Guid.NewGuid(); await using (var db = fixture.CreateDbContext()) { db.Add(new Room { Id = id, ReferenceId = "local", FriendlyName = "local", CreatedUtc = Utc, UpdatedUtc = Utc }); await db.SaveChangesAsync(); }
        var service = new DistributedImportApplicationService(fixture, TimeProvider.System); var state = Assert.IsType<DistributedImportStarted>(await service.StartAsync(new(new(2, false, null, null, [Room(id, null, "incoming", "incoming", Utc.AddMinutes(1))]), false, false))).State;
        var review = Assert.IsType<DistributedImportRoomPrepared>(await service.PrepareRoomAsync(state, id)); fault.Arm();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideRoomAsync(state, new UseIncomingRoom(id), review.Comparison));
    }

    private sealed class Fixture : IDbContextFactory<LogicDbContext>, IAsyncDisposable
    {
        private readonly string path; private readonly DbContextOptions<LogicDbContext> options;
        private Fixture(string path, IInterceptor? interceptor) { this.path=path; var builder=new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}"); if(interceptor is not null) builder.AddInterceptors(interceptor); options=builder.Options; }
        public static async Task<Fixture> CreateAsync(IInterceptor? interceptor = null) { var f=new Fixture(Path.Combine(Path.GetTempPath(), $"silksong-distributed-import-{Guid.NewGuid():N}.db"), interceptor); await using var db=f.CreateDbContext(); await db.Database.MigrateAsync(); return f; }
        public LogicDbContext CreateDbContext() => new(options);
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); if(File.Exists(path)) File.Delete(path); return ValueTask.CompletedTask; }
    }

    private sealed class FixedTimeProvider(DateTime utc) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(utc); }
    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        public int ReaderCount { get; private set; } public int CommandCount { get; private set; } public void Reset() { ReaderCount = 0; CommandCount = 0; }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) { ReaderCount++; CommandCount++; return result; }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) { CommandCount++; return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { ReaderCount++; CommandCount++; return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { CommandCount++; return ValueTask.FromResult(result); }
    }
    private sealed class ThrowingCommandInterceptor(string table) : DbCommandInterceptor
    {
        private bool enabled;
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) => Throw(command.CommandText, result);
        private InterceptionResult<DbDataReader> Throw(string sql, InterceptionResult<DbDataReader> result) { if (enabled && sql.Contains($"\"{table}\"", StringComparison.Ordinal) && (sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))) throw new DbUpdateException("forced retryable sqlite operation failure"); return result; }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) { if (enabled && command.CommandText.Contains($"\"{table}\"", StringComparison.Ordinal) && (command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) || command.CommandText.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || command.CommandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))) throw new DbUpdateException("forced retryable sqlite operation failure"); return result; }
        public void Arm() => enabled = true;
        public void Disarm() => enabled = false;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) => ValueTask.FromResult(Throw(command.CommandText, result));
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { if (enabled && command.CommandText.Contains($"\"{table}\"", StringComparison.Ordinal) && (command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) || command.CommandText.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || command.CommandText.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))) throw new DbUpdateException("forced retryable sqlite operation failure"); return ValueTask.FromResult(result); }
    }

    private sealed class UnexpectedFaultInterceptor : SaveChangesInterceptor
    {
        private bool enabled;
        public void Arm() => enabled = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (enabled) throw new InvalidOperationException("unexpected persistence fault");
            return ValueTask.FromResult(result);
        }
    }

}
