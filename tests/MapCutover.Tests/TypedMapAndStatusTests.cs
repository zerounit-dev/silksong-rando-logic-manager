using System.Data.Common;
using System.Diagnostics;
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components;
using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;
using Xunit.Abstractions;

namespace MapCutover.Tests;

[Collection("MapCutoverPerformance")]
public sealed class TypedMapAndStatusTests(ITestOutputHelper output)
{
    private static readonly DateTime Utc = new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExactStatus_RealSqliteCoversPrecedenceArchiveAndCrossRoomNameException()
    {
        await using var fixture = await Fixture.CreateAsync();
        var neutral = Room("neutral"); var success = Room("success"); var warning = Room("warning");
        var todo = Room("todo"); var danger = Room("danger"); var archived = Room("archived", true);
        var crossA = Room("cross-a"); var crossB = Room("cross-b");
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(neutral, success, warning, todo, danger, archived, crossA, crossB);
            db.AddRange(Check(success.Id, "success", true), Check(warning.Id, "warning", null),
                Check(todo.Id, "todo", true, todo: true), Check(danger.Id, "danger", true, locationType: null),
                Check(archived.Id, "archived", false, locationType: null),
                Check(crossA.Id, "shared", true), Check(crossB.Id, "shared", true));
            await db.SaveChangesAsync();
        }
        var service = new AppliedRoomStatusService(fixture);
        var actual = await service.LoadAsync();
        Assert.Equal(AppliedRoomStatus.Neutral, actual[neutral.Id]);
        Assert.Equal(AppliedRoomStatus.Success, actual[success.Id]);
        Assert.Equal(AppliedRoomStatus.Warning, actual[warning.Id]);
        Assert.Equal(AppliedRoomStatus.Warning, actual[todo.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[danger.Id]);
        Assert.Equal(AppliedRoomStatus.Neutral, actual[archived.Id]);
        Assert.Equal(AppliedRoomStatus.Success, actual[crossA.Id]);
        Assert.Equal(AppliedRoomStatus.Success, actual[crossB.Id]);
        Assert.Equal(5, service.LastTrace!.SqlQueryCount);
    }

    [Fact]
    public async Task ExactStatus_RealSqliteCoversValidationInverseAndRoomLocalGameIdRules()
    {
        await using var fixture = await Fixture.CreateAsync();
        var duplicateA = Room("duplicate-a"); duplicateA.ReferenceId = "duplicate";
        var duplicateB = Room("duplicate-b"); duplicateB.ReferenceId = "duplicate";
        var geometry = Room("geometry"); var parse = Room("parse"); var inverse = Room("inverse"); var destination = Room("destination");
        var notVerified = Room("not-verified"); var localGame = Room("local-game"); var crossGameA = Room("cross-game-a"); var crossGameB = Room("cross-game-b");
        var noSubroomConnection = Room("no-subroom-connection"); var multipleInverse = Room("multiple-inverse"); var multipleTargetRoom = Room("multiple-target");
        var malformedPathway = Room("malformed-pathway"); var unrecognizedType = Room("unrecognized-type"); var archivedChild = Room("archived-child"); var unresolvedReference = Room("unresolved-reference");
        var target = new RoomTransition { Id = Guid.NewGuid(), RoomId = destination.Id, Alias = "D", FriendlyName = "target", Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc };
        var source = new RoomTransition { Id = Guid.NewGuid(), RoomId = inverse.Id, Alias = "S", FriendlyName = "source", DestinationRoomReferenceText = destination.ReferenceId, ResolvedDestinationRoomId = destination.Id, DestinationTransitionAliasText = target.Alias, ResolvedDestinationTransitionId = target.Id, Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc };
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(duplicateA, duplicateB, geometry, parse, inverse, destination, notVerified, localGame, crossGameA, crossGameB, noSubroomConnection, multipleInverse, multipleTargetRoom, malformedPathway, unrecognizedType, archivedChild, unresolvedReference);
            db.Add(new Subroom { Id = Guid.NewGuid(), RoomId = geometry.Id, FriendlyName = "broken", ReferenceId = "broken", SceneUnitX = 1, CreatedUtc = Utc, UpdatedUtc = Utc });
            var parseCheck = Check(parse.Id, "parse", true); parseCheck.RequirementsParseSucceeded = false; db.Add(parseCheck);
            db.AddRange(source, target, Check(notVerified.Id, "not verified", false));
            db.Add(new RoomTransition { Id = Guid.NewGuid(), RoomId = localGame.Id, Alias = "L", FriendlyName = "local transition", InGameId = "same-local", Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
            var localCheck = Check(localGame.Id, "local check", true); localCheck.InGameId = "same-local"; db.Add(localCheck);
            var crossCheckA = Check(crossGameA.Id, "cross a", true); crossCheckA.InGameId = "same-cross";
            var crossCheckB = Check(crossGameB.Id, "cross b", true); crossCheckB.InGameId = "same-cross"; db.AddRange(crossCheckA, crossCheckB);
            db.Add(new SubroomConnection { Id = Guid.NewGuid(), RoomId = noSubroomConnection.Id, Alias = "C", FriendlyName = "connection", Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
            var multipleSourceId = Guid.NewGuid(); var multipleTargetId = Guid.NewGuid();
            db.Add(new RoomTransition { Id = multipleSourceId, RoomId = multipleInverse.Id, Alias = "M", FriendlyName = "multiple source", DestinationRoomReferenceText = multipleTargetRoom.ReferenceId, ResolvedDestinationRoomId = multipleTargetRoom.Id, DestinationTransitionAliasText = "T", ResolvedDestinationTransitionId = multipleTargetId, Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
            db.Add(new RoomTransition { Id = multipleTargetId, RoomId = multipleTargetRoom.Id, Alias = "T", FriendlyName = "target", Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
            for (var i = 0; i < 2; i++) db.Add(new RoomTransition { Id = Guid.NewGuid(), RoomId = multipleTargetRoom.Id, Alias = $"I{i}", FriendlyName = $"inverse-{i}", ResolvedDestinationRoomId = multipleInverse.Id, ResolvedDestinationTransitionId = multipleSourceId, Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
            var malformedLeft = new Subroom { Id = Guid.NewGuid(), RoomId = malformedPathway.Id, FriendlyName = "left", ReferenceId = "left", CreatedUtc = Utc, UpdatedUtc = Utc };
            var malformedRight = new Subroom { Id = Guid.NewGuid(), RoomId = malformedPathway.Id, FriendlyName = "right", ReferenceId = "right", CreatedUtc = Utc, UpdatedUtc = Utc }; db.AddRange(malformedLeft, malformedRight);
            for (var i = 0; i < 3; i++) db.Add(new SubroomConnection { Id = Guid.NewGuid(), RoomId = malformedPathway.Id, Alias = "P", FriendlyName = "path", SourceSubroomReferenceText = "left", ResolvedSourceSubroomId = malformedLeft.Id, DestinationSubroomReferenceText = "right", ResolvedDestinationSubroomId = malformedRight.Id, Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
            var unknown = Check(unrecognizedType.Id, "unknown type", true); unknown.LocationType = "not-a-type"; db.Add(unknown);
            var archivedBad = Check(archivedChild.Id, "archived bad", false, locationType: null); archivedBad.IsArchived = true; archivedBad.ArchivedUtc = Utc; db.Add(archivedBad);
            var unresolved = Check(unresolvedReference.Id, "unresolved", true); unresolved.SubroomReferenceText = "missing"; db.Add(unresolved);
            await db.SaveChangesAsync();
        }
        var actual = await new AppliedRoomStatusService(fixture).LoadAsync();
        Assert.Equal(AppliedRoomStatus.Danger, actual[duplicateA.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[duplicateB.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[geometry.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[parse.Id]);
        Assert.Equal(AppliedRoomStatus.Warning, actual[inverse.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[notVerified.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[localGame.Id]);
        Assert.Equal(AppliedRoomStatus.Success, actual[crossGameA.Id]);
        Assert.Equal(AppliedRoomStatus.Success, actual[crossGameB.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[noSubroomConnection.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[multipleInverse.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[malformedPathway.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[unrecognizedType.Id]);
        Assert.Equal(AppliedRoomStatus.Neutral, actual[archivedChild.Id]);
        Assert.Equal(AppliedRoomStatus.Danger, actual[unresolvedReference.Id]);
    }

    [Fact]
    public async Task ExactStatus_CurrentScaleRepeatedUncachedSidebarAndLinkedLoadsStayBelowFiftyMilliseconds()
    {
        var sql = new SqlTrace();
        await using var fixture = await Fixture.CreateAsync(sql);
        var rooms = Enumerable.Range(0, 458).Select(i => Room($"room-{i:D3}")).ToArray();
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(rooms);
            for (var i = 0; i < 1172; i++) db.Add(new Subroom { Id = Guid.NewGuid(), RoomId = rooms[i % rooms.Length].Id, FriendlyName = $"sub-{i}", ReferenceId = $"sub-{i}", CreatedUtc = Utc, UpdatedUtc = Utc });
            for (var i = 0; i < 1405; i++) db.Add(new RoomTransition { Id = Guid.NewGuid(), RoomId = rooms[i % rooms.Length].Id, Alias = $"t{i}", FriendlyName = $"transition-{i}", Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
            for (var i = 0; i < 1811; i++) db.Add(new SubroomConnection { Id = Guid.NewGuid(), RoomId = rooms[i % rooms.Length].Id, Alias = $"c{i}", FriendlyName = $"connection-{i}", Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
            for (var i = 0; i < 1060; i++) db.Add(Check(rooms[i % rooms.Length].Id, $"check-{i}", true));
            await db.SaveChangesAsync();
        }
        var service = new AppliedRoomStatusService(fixture);
        sql.Reset(); var first = Stopwatch.StartNew(); await service.LoadAsync(); first.Stop();
        output.WriteLine($"first-use total={first.Elapsed.TotalMilliseconds:F3}ms sql={sql.Count}/{sql.Elapsed.TotalMilliseconds:F3}ms facts={Facts(service.LastTrace!)}");
        await service.LoadAsync(); await service.LoadAsync(rooms.Take(120).Select(x => x.Id));
        foreach (var (name, ids) in new (string, IEnumerable<Guid>?)[ ] { ("sidebar", null), ("linked", rooms.Take(120).Select(x => x.Id)) })
        for (var sample = 1; sample <= 3; sample++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            sql.Reset(); var timer = Stopwatch.StartNew(); await service.LoadAsync(ids); timer.Stop();
            output.WriteLine($"{name}[{sample}] total={timer.Elapsed.TotalMilliseconds:F3}ms sql={sql.Count}/{sql.Elapsed.TotalMilliseconds:F3}ms facts={Facts(service.LastTrace!)}");
            Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(50), $"{name}[{sample}] took {timer.Elapsed.TotalMilliseconds:F3}ms");
            Assert.Equal(5, sql.Count);
        }
    }

    [Fact]
    public async Task ExactStatus_ConcentratedHighRowRoomUsesIndexedAggregation()
    {
        await using var fixture = await Fixture.CreateAsync(); var room = Room("dense"); var destinationRoom = Room("dense-destination");
        await using (var db = fixture.CreateDbContext())
        {
            var inverseTargets = new List<(RoomTransition Target, Guid SourceId, string Alias)>();
            var left = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "left", ReferenceId = "left", CreatedUtc = Utc, UpdatedUtc = Utc };
            var right = new Subroom { Id = Guid.NewGuid(), RoomId = room.Id, FriendlyName = "right", ReferenceId = "right", CreatedUtc = Utc, UpdatedUtc = Utc };
            db.AddRange(room, destinationRoom, left, right);
            for (var i = 0; i < 200; i++)
            {
                var alias = i.ToString("X3"); var sourceId = Guid.NewGuid(); var targetId = Guid.NewGuid();
                db.Add(new RoomTransition { Id = sourceId, RoomId = room.Id, Alias = alias, FriendlyName = $"source-{i}", SourceSubroomReferenceText = left.ReferenceId, ResolvedSourceSubroomId = left.Id, DestinationRoomReferenceText = destinationRoom.ReferenceId, ResolvedDestinationRoomId = destinationRoom.Id, DestinationTransitionAliasText = alias, ResolvedDestinationTransitionId = targetId, Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
                var target = new RoomTransition { Id = targetId, RoomId = destinationRoom.Id, Alias = alias, FriendlyName = $"target-{i}", Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc };
                db.Add(target); inverseTargets.Add((target, sourceId, alias));
                var connectionAlias = $"C{i:X2}";
                db.Add(new SubroomConnection { Id = Guid.NewGuid(), RoomId = room.Id, Alias = connectionAlias, FriendlyName = $"path-{i}", SourceSubroomReferenceText = left.ReferenceId, ResolvedSourceSubroomId = left.Id, DestinationSubroomReferenceText = right.ReferenceId, ResolvedDestinationSubroomId = right.Id, Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
                db.Add(new SubroomConnection { Id = Guid.NewGuid(), RoomId = room.Id, Alias = connectionAlias, FriendlyName = $"path-{i}", SourceSubroomReferenceText = right.ReferenceId, ResolvedSourceSubroomId = right.Id, DestinationSubroomReferenceText = left.ReferenceId, ResolvedDestinationSubroomId = left.Id, Requirements = "logic", RequirementsParseSucceeded = true, IsVerified = true, CreatedUtc = Utc, UpdatedUtc = Utc });
                var check = Check(room.Id, $"check-{i}", true); check.SubroomReferenceText = left.ReferenceId; check.ResolvedSubroomId = left.Id; db.Add(check);
            }
            await db.SaveChangesAsync();
            foreach (var (target, sourceId, alias) in inverseTargets)
            {
                target.DestinationRoomReferenceText = room.ReferenceId; target.ResolvedDestinationRoomId = room.Id;
                target.DestinationTransitionAliasText = alias; target.ResolvedDestinationTransitionId = sourceId;
            }
            await db.SaveChangesAsync();
        }
        var service = new AppliedRoomStatusService(fixture); await service.LoadAsync([room.Id]);
        var timer = Stopwatch.StartNew(); var result = await service.LoadAsync([room.Id]); timer.Stop();
        output.WriteLine($"concentrated total={timer.Elapsed.TotalMilliseconds:F3}ms facts={Facts(service.LastTrace!)}");
        Assert.Equal(AppliedRoomStatus.Success, result[room.Id]);
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(50), $"Concentrated status took {timer.Elapsed.TotalMilliseconds:F3}ms");
    }

    [Fact]
    public async Task BothHostLoader_ReusesCanonicalGeometryAndReplacesItOnlyForChangedContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var room = Room("room");
        await using (var db = fixture.CreateDbContext())
        {
            var map = new Map { Id = Guid.NewGuid(), InGameId = "map", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 100, MapUnitMaxY = 100 };
            var zone = new MapZone { Id = Guid.NewGuid(), Map = map, InGameId = "zone", MapUnitMinX = 10, MapUnitMinY = 20, MapUnitMaxX = 40, MapUnitMaxY = 60 };
            var scene = new MapScene { Id = Guid.NewGuid(), MapZone = zone, InGameId = "scene", ResolvedRoom = room, RoomReferenceText = room.ReferenceId };
            db.AddRange(room, map, zone, scene, new MapChunk { Id = Guid.NewGuid(), MapScene = scene, CacheIndex = 0, MapUnitMinX = 10, MapUnitMinY = 20, MapUnitMaxX = 40, MapUnitMaxY = 60, MapUnitZ = 0 });
            await db.SaveChangesAsync();
        }
        var projection = new MapRenderProjectionService();
        var loader = new AreaMapLoader(fixture, projection, new MapOverlayAssetCatalog(), new MapOverlayPlacementService(), new AppliedRoomStatusService(fixture));
        var landing = (await loader.LoadLandingAsync()).Surface!;
        output.WriteLine($"landing map total={loader.LastTrace!.TotalElapsed.TotalMilliseconds:F3}ms projection={loader.LastTrace.ProjectionElapsed.TotalMilliseconds:F3}ms sql={loader.LastTrace.SqlQueryCount} chunks={loader.LastTrace.RenderedChunkCount} cacheHit={loader.LastTrace.GeometryCacheHit}");
        Assert.False(loader.LastTrace!.GeometryCacheHit);
        var context = (await loader.LoadRoomAsync(room.Id, null))!;
        output.WriteLine($"room map total={loader.LastTrace!.TotalElapsed.TotalMilliseconds:F3}ms projection={loader.LastTrace.ProjectionElapsed.TotalMilliseconds:F3}ms sql={loader.LastTrace.SqlQueryCount} chunks={loader.LastTrace.RenderedChunkCount} cacheHit={loader.LastTrace.GeometryCacheHit}");
        Assert.True(loader.LastTrace!.GeometryCacheHit);
        Assert.Equal(landing.Geometry.CacheKey, context.Geometry.CacheKey);
        var navigated = (await loader.LoadRoomAsync(room.Id, context.Geometry))!;
        Assert.Same(context.Geometry, navigated.Geometry);
        Assert.Equal(new MapSvgBounds(10, 40, 30, 40), context.Context.InitialBounds);
        Assert.Equal($"room-context-{(await ZoneId(fixture)):N}", context.Context.ViewportKey);
        await using (var db = fixture.CreateDbContext()) await db.MapChunks.ExecuteUpdateAsync(x => x.SetProperty(c => c.MapUnitMaxX, 50d));
        var changed = (await loader.LoadLandingAsync()).Surface!;
        output.WriteLine($"changed map total={loader.LastTrace!.TotalElapsed.TotalMilliseconds:F3}ms projection={loader.LastTrace.ProjectionElapsed.TotalMilliseconds:F3}ms sql={loader.LastTrace.SqlQueryCount} chunks={loader.LastTrace.RenderedChunkCount} cacheHit={loader.LastTrace.GeometryCacheHit}");
        Assert.False(loader.LastTrace!.GeometryCacheHit);
        Assert.NotEqual(landing.Geometry.CacheKey, changed.Geometry.CacheKey);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task RoomContext_ZeroMultipleOrUnusableZonesUseFirstMapFallbackKey(int linkedZoneCount, bool unusable)
    {
        await using var fixture = await Fixture.CreateAsync(); var room = Room("context");
        var fallbackId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            var fallback = new Map { Id = fallbackId, InGameId = "a-fallback", SortOrder = 0, MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 100, MapUnitMaxY = 80 };
            var linkedMap = new Map { Id = Guid.NewGuid(), InGameId = "b-linked", SortOrder = 1, MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 50, MapUnitMaxY = 50 };
            db.AddRange(room, fallback, linkedMap);
            for (var i = 0; i < linkedZoneCount; i++)
            {
                var zone = new MapZone { Id = Guid.NewGuid(), Map = linkedMap, InGameId = $"zone-{i}", MapUnitMinX = unusable ? null : i * 10, MapUnitMinY = unusable ? null : 0, MapUnitMaxX = unusable ? null : i * 10 + 5, MapUnitMaxY = unusable ? null : 5 };
                db.Add(new MapScene { Id = Guid.NewGuid(), MapZone = zone, InGameId = $"scene-{i}", ResolvedRoom = room, RoomReferenceText = room.ReferenceId });
            }
            await db.SaveChangesAsync();
        }
        var loader = new AreaMapLoader(fixture, new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService(), new AppliedRoomStatusService(fixture));
        var view = (await loader.LoadRoomAsync(room.Id, null))!;
        Assert.Equal(fallbackId, view.Geometry.MapId);
        Assert.Equal($"room-context-map-{fallbackId:N}", view.Context.ViewportKey);
        Assert.Equal(new MapSvgBounds(0, 0, 100, 80), view.Context.InitialBounds);
    }

    [Fact]
    public async Task OverlayPlacementChangesDecorationWithoutChangingCanonicalGeometryKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var map = new Map { Id = Guid.NewGuid(), InGameId = "map", MapUnitMinX = 10, MapUnitMinY = 20, MapUnitMaxX = 110, MapUnitMaxY = 90 };
            db.Add(map); db.Add(new MapOverlay { Id = Guid.NewGuid(), Map = map, FriendlyName = "Area", ImageAssetKey = "area-map-hd", ScaleXPercent = 80, ScaleYPercent = 120, LeftOffsetPercent = -4, BottomOffsetPercent = 6 });
            await db.SaveChangesAsync();
        }
        var loader = new AreaMapLoader(fixture, new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService(), new AppliedRoomStatusService(fixture));
        var before = (await loader.LoadLandingAsync()).Surface!; var key = before.Geometry.CacheKey;
        Assert.NotNull(before.Decoration.Overlay); Assert.True(before.Decoration.Overlay!.Placement.Width > 0);
        await new MapOverlayService(fixture).SavePlacementAsync(new(before.Geometry.MapId, 90, 110, 2, 3));
        var after = (await loader.LoadLandingAsync()).Surface!;
        Assert.Equal(key, after.Geometry.CacheKey);
        Assert.NotEqual(before.Decoration.Overlay.Placement, after.Decoration.Overlay!.Placement);
        Assert.True(loader.LastTrace!.GeometryCacheHit);
    }

    [Fact]
    public void Projection_PreservesMergedOwnersHolesIslandsAndUnlinkedLowerZPriority()
    {
        var service = new MapRenderProjectionService(); var room = Guid.NewGuid();
        var ring = Enumerable.Range(0, 3).SelectMany(x => Enumerable.Range(0, 3)
            .Where(y => x != 1 || y != 1).Select(y => Chunk($"ring-{x}-{y}", room, x, y, x + 1, y + 1, 5))).ToList();
        ring.Add(Chunk("island", room, 5, 0, 6, 1, 5));
        var overlapRoom = Guid.NewGuid();
        var unlinked = Chunk("unlinked", null, 9, 0, 10, 1, 100);
        var overlappedLinked = Chunk("overlapped-linked", overlapRoom, 9, 0, 10, 1, -100);
        var lower = Chunk("lower", null, 7, 0, 8, 1, 0);
        var higher = Chunk("higher", null, 7, 0, 8, 1, 1);
        var result = service.Project(new(Guid.NewGuid(), [.. ring, unlinked, overlappedLinked, lower, higher]));
        var linked = Assert.Single(result.Owners, x => x.ActiveResolvedRoomId == room);
        Assert.Equal(3, linked.SvgPathData.Count(x => x == 'M'));
        Assert.DoesNotContain("M 0 0 L 1 0 L 1 1 L 0 1 Z", linked.SvgPathData, StringComparison.Ordinal);
        Assert.Contains(result.Owners, x => x.UnlinkedChunkId == unlinked.ChunkId);
        Assert.DoesNotContain(result.Owners, x => x.ActiveResolvedRoomId == overlapRoom);
        Assert.Contains(result.Owners, x => x.UnlinkedChunkId == lower.ChunkId);
        Assert.DoesNotContain(result.Owners, x => x.UnlinkedChunkId == higher.ChunkId);
    }

    [Fact]
    public void SharedSurface_HasTypedFullAndCompactContractsWithoutEfParameters()
    {
        var mapId = Guid.NewGuid(); var roomId = Guid.NewGuid(); var chunkId = Guid.NewGuid();
        var owner = new MapRenderProjectionOwner(roomId, null, "M 0 0 L 1 0 L 1 1 L 0 1 Z", new(0, 0, 1, 1));
        var overlay = new AreaMapOverlayView(Guid.NewGuid(), mapId, "/map.webp", 2, 100, 100, 0, 0, new(0, 0, 2, 1));
        AreaMapSurfaceView View(bool room) => new(new(mapId, new(0, 0, 1, 1), "key", [owner], 1),
            new(new Dictionary<Guid, AreaMapOwnerDecoration> { [roomId] = new(roomId, null, "Room", AppliedRoomStatus.Danger) }, new Dictionary<Guid, AreaMapOwnerDecoration>(), overlay),
            new(room ? "room-map" : "landing-map", room ? "room-key" : "map-key", new(0, 0, 1, 1), room ? roomId : null, room));
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var full = context.RenderComponent<AreaMapSurface>(p => p.Add(x => x.View, View(false)));
        Assert.Contains("image", full.Markup); Assert.Contains("linked", full.Markup); Assert.Contains("unlinked", full.Markup);
        var compact = context.RenderComponent<AreaMapSurface>(p => p.Add(x => x.View, View(true)).Add(x => x.Compact, true));
        var image = compact.Find(".map-image-toggle-compact");
        Assert.Equal("hide map image", image.GetAttribute("aria-label")); Assert.Equal("true", image.GetAttribute("aria-pressed"));
        Assert.Single(compact.FindAll(".current-room-frame"));
        var prohibited = typeof(AreaMapSurface).GetProperties().Select(x => x.PropertyType)
            .Where(x => typeof(LogicDbContext).IsAssignableFrom(x) || x.Namespace == typeof(Room).Namespace).ToArray();
        Assert.Empty(prohibited);
        Assert.DoesNotContain(typeof(LogicDbContext).Assembly.GetTypes(), x =>
            x == typeof(AreaMapSurfaceView) && x.Namespace == typeof(Room).Namespace);
    }

    [Theory]
    [InlineData(LandingAreaMapState.NoImportedMap, "No imported map frames yet.", false)]
    [InlineData(LandingAreaMapState.UnusableGeometry, "The imported map has no available chunk geometry.", false)]
    [InlineData(LandingAreaMapState.Ready, "", true)]
    public async Task Landing_RendersDistinctEmptyInvalidAndValidStatesInsideEstablishedControlBar(LandingAreaMapState state, string message, bool hasSvg)
    {
        await using var fixture = await Fixture.CreateAsync();
        var view = state == LandingAreaMapState.Ready ? SurfaceView() : null;
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IAreaMapLoader>(new StaticLandingLoader(new(state, view)));
        context.Services.AddSingleton(new MapManifestParser()); context.Services.AddSingleton(new MapManifestService(fixture));
        context.Services.AddSingleton(new MapOverlayService(fixture)); context.Services.AddSingleton(new MapOverlayPlacementService());
        context.Services.AddSingleton(new DiagnosticState());
        var page = context.RenderComponent<MapLanding>();
        page.WaitForAssertion(() => Assert.Equal(hasSvg, page.FindAll("svg.map-wireframe").Count == 1));
        if (message.Length > 0) Assert.Equal(message, page.Find(".map-empty").TextContent.Trim());
        Assert.Single(page.FindAll(".map-manifest-upload"));
        Assert.Single(page.FindAll("#map-manifest-input"));
        if (hasSvg) Assert.Equal(3, page.FindAll("[data-map-command]").Count);
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "AreaMapSurface.razor"));
        Assert.Contains("@ControlStatus", source);
    }

    [Fact]
    public void ShippedBrowserOwner_IsExplicitDisposableAndHasNoGlobalDiscoveryFallback()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "map-navigation.js"));
        var mapPart = script[..script.IndexOf("window.mapOverlayCalibration", StringComparison.Ordinal)];
        Assert.Contains("create(id)", mapPart); Assert.Contains("reconcile", mapPart); Assert.Contains("dispose", mapPart);
        Assert.Contains("AbortController", mapPart); Assert.Contains("visibilityDefaultsV4", mapPart);
        Assert.Contains("Math.hypot", mapPart); Assert.Contains("suppressClick", mapPart); Assert.Contains("setPointerCapture", mapPart);
        Assert.Contains("if (state.image === undefined) state.image = true", mapPart);
        Assert.Contains("if (state.linked === undefined) state.linked = svg.dataset.hasOverlay !== \"true\"", mapPart);
        Assert.Contains("hidden-linked-owner", mapPart); Assert.Contains("isVisible || owner === \"linked\"", mapPart);
        Assert.Contains("sessionStorage.setItem", mapPart); Assert.Contains("navigationBounds[2] / 8", mapPart);
        Assert.Contains("initialFit.slice()", mapPart); Assert.Contains("svg.isConnected", mapPart);
        Assert.DoesNotContain("MutationObserver", mapPart); Assert.DoesNotContain("document.body", mapPart);
        Assert.DoesNotContain("querySelectorAll(\"svg.map-wireframe", mapPart);
    }

    [Fact]
    public void SuccessfulWorkflowHooksAndRefreshBoundariesUseOnlyTypedMapReloads()
    {
        var root = RepositoryRoot();
        var landing = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "Pages", "MapLanding.razor"));
        var pane = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "AreaMapPanePresentation.razor"));
        var page = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "RoomEditorV2Page.razor"));
        Assert.Contains("result = await ManifestService.ApplyPlanAsync", landing); Assert.Contains("await LoadAsync();", landing);
        Assert.Contains("if (changed) await LoadAsync();", landing); Assert.Contains("await OverlayService.SavePlacementAsync", landing);
        Assert.Contains("if (changed) await MapLinksChanged.InvokeAsync();", pane);
        var roomLinkHook = page[page.IndexOf("RefreshSceneCaptureContextAfterMapLinkChangeAsync", StringComparison.Ordinal)..];
        Assert.Contains("mapLoads.LoadAsync(sourceRoomId, null, sourceGeneration, OwnsMapLoad)", roomLinkHook);
        Assert.Contains("refresh.RefreshAsync(sourceRoomId, true, true)", roomLinkHook);
        Assert.Contains("outcome.MapResolutionChanged && mapLoads is not null", page);
        Assert.DoesNotContain("AreaMaps", page[page.IndexOf("private async Task<V2SubroomCommandOutcome> Execute", StringComparison.Ordinal)..page.IndexOf("public Task<V2TransitionCommandOutcome> SaveTransitionAsync", StringComparison.Ordinal)]);
    }

    private static string Facts(AppliedRoomStatusLoadTrace trace) => $"rooms={trace.RoomFacts},subrooms={trace.SubroomFacts},transitions={trace.TransitionFacts},connections={trace.ConnectionFacts},checks={trace.CheckFacts}";
    private static AreaMapSurfaceView SurfaceView()
    {
        var map = Guid.NewGuid();
        return new(new(map, new(0, 0, 100, 100), "key", [], 0), new(new Dictionary<Guid, AreaMapOwnerDecoration>(), new Dictionary<Guid, AreaMapOwnerDecoration>(), null), new("map-wireframe", map.ToString(), new(0, 0, 100, 100), null, false));
    }
    private static MapRenderProjectionChunk Chunk(string identity, Guid? roomId, double minX, double minY, double maxX, double maxY, double z) => new(Guid.NewGuid(), identity, roomId, minX, minY, maxX, maxY, z);
    private static Room Room(string name, bool archived = false) => new() { Id = Guid.NewGuid(), ReferenceId = name, FriendlyName = name, IsArchived = archived, ArchivedUtc = archived ? Utc : null, CreatedUtc = Utc, UpdatedUtc = Utc };
    private static CheckLocation Check(Guid roomId, string name, bool? verified, bool todo = false, string? locationType = "collectible") => new() { Id = Guid.NewGuid(), RoomId = roomId, FriendlyName = name, Requirements = "logic", RequirementsParseSucceeded = true, LocationType = locationType, IsVerified = verified, IsTodo = todo, CreatedUtc = Utc, UpdatedUtc = Utc };
    private static async Task<Guid> ZoneId(Fixture fixture) { await using var db = fixture.CreateDbContext(); return await db.MapZones.Select(x => x.Id).SingleAsync(); }
    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "Silksong Rando Logic Manager"))) return current.FullName;
        throw new DirectoryNotFoundException();
    }

    internal sealed class Fixture(string directory, string path, DbCommandInterceptor? interceptor) : IDbContextFactory<LogicDbContext>, IAsyncDisposable
    {
        public static async Task<Fixture> CreateAsync(DbCommandInterceptor? interceptor = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "silksong-map-cutover", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var fixture = new Fixture(directory, Path.Combine(directory, "logic.db"), interceptor);
            await using var db = fixture.CreateDbContext(); await db.Database.MigrateAsync(); return fixture;
        }
        public LogicDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}");
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new(options.Options);
        }
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); return ValueTask.CompletedTask; }
    }
    private sealed class StaticLandingLoader(LandingAreaMapView view) : IAreaMapLoader
    {
        public Task<LandingAreaMapView> LoadLandingAsync(CancellationToken cancellationToken = default) => Task.FromResult(view);
        public Task<AreaMapSurfaceView?> LoadRoomAsync(Guid roomId, GlobalAreaMapGeometryView? retainedGeometry, CancellationToken cancellationToken = default) => Task.FromResult<AreaMapSurfaceView?>(null);
    }

    internal sealed class SqlTrace : DbCommandInterceptor
    {
        private long ticks; public int Count { get; private set; } public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref ticks));
        public void Reset() { Count = 0; Interlocked.Exchange(ref ticks, 0); }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Count++; Interlocked.Add(ref ticks, eventData.Duration.Ticks); return ValueTask.FromResult(result);
        }
    }
}
