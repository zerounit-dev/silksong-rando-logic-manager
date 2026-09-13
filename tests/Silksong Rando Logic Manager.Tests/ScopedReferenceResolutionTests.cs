using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class ScopedReferenceResolutionTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"silksong-scoped-resolution-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task OrdinaryReferenceSaves_ResolveEveryAffectedReferenceClassWithoutRewritingText()
    {
        var ids = await SeedAsync();
        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));

        await using (var db = CreateContext())
        {
            var room = await db.Rooms.AsNoTracking().SingleAsync(item => item.Id == ids.DestinationRoomId);
            room.ReferenceId = "destination-new";
            var outcome = await catalog.SaveWithOutcomeAsync(room);
            Assert.True(outcome.Resolved);
            Assert.True(outcome.RequiresDocumentRefresh);
        }

        await using (var db = CreateContext())
        {
            var subroom = await db.Subrooms.AsNoTracking().SingleAsync(item => item.Id == ids.SubroomId);
            subroom.ReferenceId = "subroom-new";
            Assert.True((await catalog.SaveWithOutcomeAsync(subroom)).Resolved);
        }

        await using (var db = CreateContext())
        {
            var transition = await db.RoomTransitions.AsNoTracking().SingleAsync(item => item.Id == ids.DestinationTransitionId);
            transition.Alias = "NEW";
            var outcome = await catalog.SaveWithOutcomeAsync(transition);
            Assert.True(outcome.Resolved);
            Assert.True(outcome.RequiresDocumentRefresh);
        }

        await using (var db = CreateContext())
        {
            var transition = await db.RoomTransitions.AsNoTracking().SingleAsync(item => item.Id == ids.SourceTransitionId);
            transition.SourceSubroomReferenceText = " subroom-new ";
            Assert.True((await catalog.SaveWithOutcomeAsync(transition)).Resolved);
            var connection = await db.SubroomConnections.AsNoTracking().SingleAsync(item => item.Id == ids.ConnectionId);
            connection.SourceSubroomReferenceText = " subroom-new ";
            Assert.True((await catalog.SaveWithOutcomeAsync(connection)).Resolved);
            var check = await db.CheckLocations.AsNoTracking().SingleAsync(item => item.Id == ids.CheckId);
            check.SubroomReferenceText = " subroom-new ";
            Assert.True((await catalog.SaveWithOutcomeAsync(check)).Resolved);
            var group = await db.RoomGroups.AsNoTracking().SingleAsync(item => item.Id == ids.GroupId);
            group.ZoneReferenceText = "zone";
            Assert.True((await catalog.SaveWithOutcomeAsync(group)).Resolved);
        }

        await using var verification = CreateContext();
        var source = await verification.RoomTransitions.SingleAsync(item => item.Id == ids.SourceTransitionId);
        var connectionResult = await verification.SubroomConnections.SingleAsync(item => item.Id == ids.ConnectionId);
        var checkResult = await verification.CheckLocations.SingleAsync(item => item.Id == ids.CheckId);
        var scene = await verification.MapScenes.SingleAsync(item => item.Id == ids.MapSceneId);
        Assert.Equal("destination-new", source.DestinationRoomReferenceText);
        Assert.Equal("NEW", source.DestinationTransitionAliasText);
        Assert.Equal(ids.DestinationRoomId, source.ResolvedDestinationRoomId);
        Assert.Equal(ids.DestinationTransitionId, source.ResolvedDestinationTransitionId);
        Assert.Equal(" subroom-new ", source.SourceSubroomReferenceText);
        Assert.Equal(ids.SubroomId, source.ResolvedSourceSubroomId);
        Assert.Equal(" subroom-new ", connectionResult.SourceSubroomReferenceText);
        Assert.Equal(ids.SubroomId, connectionResult.ResolvedSourceSubroomId);
        Assert.Equal(" subroom-new ", checkResult.SubroomReferenceText);
        Assert.Equal(ids.SubroomId, checkResult.ResolvedSubroomId);
        Assert.Equal("destination-new", scene.RoomReferenceText);
        Assert.Equal(ids.DestinationRoomId, scene.ResolvedRoomId);
        Assert.Equal(ids.ZoneId, (await verification.RoomGroups.SingleAsync(item => item.Id == ids.GroupId)).ResolvedMapZoneId);
    }

    [Fact]
    public async Task TodoSaves_RequestSidebarRefreshForEveryTodoOwner()
    {
        var ids = await SeedAsync();
        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await using var db = CreateContext();
        foreach (var entity in new AuditedEntity[]
        {
            await db.RoomTransitions.AsNoTracking().SingleAsync(item => item.Id == ids.SourceTransitionId),
            await db.SubroomConnections.AsNoTracking().SingleAsync(item => item.Id == ids.ConnectionId),
            await db.CheckLocations.AsNoTracking().SingleAsync(item => item.Id == ids.CheckId)
        })
        {
            switch (entity)
            {
                case RoomTransition transition: transition.IsTodo = true; Assert.True((await catalog.SaveWithOutcomeAsync(transition)).RequiresSidebarRefresh); break;
                case SubroomConnection connection: connection.IsTodo = true; Assert.True((await catalog.SaveWithOutcomeAsync(connection)).RequiresSidebarRefresh); break;
                case CheckLocation check: check.IsTodo = true; Assert.True((await catalog.SaveWithOutcomeAsync(check)).RequiresSidebarRefresh); break;
            }
        }
    }

    [Fact]
    public async Task RoomAndSubroomRenames_ClearResolvedIdsWithoutRewritingOldAuthoredText()
    {
        var ids = await SeedAsync(false);
        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await using (var db = CreateContext())
        {
            var room = await db.Rooms.AsNoTracking().SingleAsync(item => item.Id == ids.DestinationRoomId);
            room.ReferenceId = "destination-new";
            Assert.True((await catalog.SaveWithOutcomeAsync(room)).Resolved);
            var subroom = await db.Subrooms.AsNoTracking().SingleAsync(item => item.Id == ids.SubroomId);
            subroom.ReferenceId = "subroom-new";
            Assert.True((await catalog.SaveWithOutcomeAsync(subroom)).Resolved);
        }

        await using var verification = CreateContext();
        var source = await verification.RoomTransitions.SingleAsync(item => item.Id == ids.SourceTransitionId);
        var connection = await verification.SubroomConnections.SingleAsync(item => item.Id == ids.ConnectionId);
        var check = await verification.CheckLocations.SingleAsync(item => item.Id == ids.CheckId);
        var scene = await verification.MapScenes.SingleAsync(item => item.Id == ids.MapSceneId);
        Assert.Equal("destination", source.DestinationRoomReferenceText);
        Assert.Null(source.ResolvedDestinationRoomId);
        Assert.Null(source.ResolvedDestinationTransitionId);
        Assert.Equal("destination", scene.RoomReferenceText);
        Assert.Null(scene.ResolvedRoomId);
        Assert.Equal("subroom", source.SourceSubroomReferenceText);
        Assert.Null(source.ResolvedSourceSubroomId);
        Assert.Equal("subroom", connection.SourceSubroomReferenceText);
        Assert.Null(connection.ResolvedSourceSubroomId);
        Assert.Equal("subroom", check.SubroomReferenceText);
        Assert.Null(check.ResolvedSubroomId);
    }

    [Fact]
    public async Task ScopedTransitionAndConnectionReferences_HandleUnresolvedAmbiguousAndArchivedTargetsIndependently()
    {
        var ids = await SeedAsync(false);
        var catalog = new LogicCatalogService(new TestDbContextFactory(databasePath));
        await using (var db = CreateContext())
        {
            var transition = await db.RoomTransitions.AsNoTracking().SingleAsync(item => item.Id == ids.SourceTransitionId);
            transition.DestinationRoomReferenceText = "missing";
            Assert.True((await catalog.SaveWithOutcomeAsync(transition)).Resolved);
            Assert.Null((await db.RoomTransitions.AsNoTracking().SingleAsync(item => item.Id == ids.SourceTransitionId)).ResolvedDestinationRoomId);
            transition = await db.RoomTransitions.AsNoTracking().SingleAsync(item => item.Id == ids.SourceTransitionId);
            transition.DestinationRoomReferenceText = "ambiguous";
            db.Rooms.AddRange(new Room { FriendlyName = "A", ReferenceId = "ambiguous", SortOrder = 3 }, new Room { FriendlyName = "B", ReferenceId = "ambiguous", SortOrder = 4 });
            await db.SaveChangesAsync();
            Assert.True((await catalog.SaveWithOutcomeAsync(transition)).Resolved);
            Assert.Null((await db.RoomTransitions.AsNoTracking().SingleAsync(item => item.Id == ids.SourceTransitionId)).ResolvedDestinationRoomId);
            transition = await db.RoomTransitions.AsNoTracking().SingleAsync(item => item.Id == ids.SourceTransitionId);
            transition.DestinationRoomReferenceText = "archived";
            db.Rooms.Add(new Room { FriendlyName = "Archived", ReferenceId = "archived", SortOrder = 5, IsArchived = true, ArchivedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            Assert.True((await catalog.SaveWithOutcomeAsync(transition)).Resolved);

            var connection = await db.SubroomConnections.AsNoTracking().SingleAsync(item => item.Id == ids.ConnectionId);
            connection.SourceSubroomReferenceText = "missing";
            Assert.True((await catalog.SaveWithOutcomeAsync(connection)).Resolved);
            Assert.Equal(ids.SubroomId, (await db.SubroomConnections.AsNoTracking().SingleAsync(item => item.Id == ids.ConnectionId)).ResolvedDestinationSubroomId);
            connection = await db.SubroomConnections.AsNoTracking().SingleAsync(item => item.Id == ids.ConnectionId);
            connection.DestinationSubroomReferenceText = "missing";
            Assert.True((await catalog.SaveWithOutcomeAsync(connection)).Resolved);
        }

        await using var verification = CreateContext();
        var source = await verification.RoomTransitions.SingleAsync(item => item.Id == ids.SourceTransitionId);
        var connectionResult = await verification.SubroomConnections.SingleAsync(item => item.Id == ids.ConnectionId);
        Assert.Equal("archived", source.DestinationRoomReferenceText);
        Assert.Null(source.ResolvedDestinationRoomId);
        Assert.Equal("missing", connectionResult.SourceSubroomReferenceText);
        Assert.Null(connectionResult.ResolvedSourceSubroomId);
        Assert.Equal("missing", connectionResult.DestinationSubroomReferenceText);
        Assert.Null(connectionResult.ResolvedDestinationSubroomId);
    }

    private async Task<FixtureIds> SeedAsync(bool futureReferences = true, bool archivedSource = false)
    {
        await using var db = CreateContext();
        var group = new RoomGroup { FriendlyName = "Group", ZoneReferenceText = "missing", SortOrder = 0 };
        var map = new Map { InGameId = "map", SortOrder = 0 };
        db.AddRange(group, map);
        await db.SaveChangesAsync();
        var zone = new MapZone { MapId = map.Id, InGameId = "zone" };
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var destinationRoom = new Room { FriendlyName = "Destination", ReferenceId = "destination", SortOrder = 1 };
        db.AddRange(zone, sourceRoom, destinationRoom);
        await db.SaveChangesAsync();
        var scene = new MapScene { MapZoneId = zone.Id, InGameId = "scene", RoomReferenceText = futureReferences ? "destination-new" : "destination" };
        var subroom = new Subroom { RoomId = sourceRoom.Id, FriendlyName = "Subroom", ReferenceId = "subroom", SortOrder = 0 };
        var destination = new RoomTransition { RoomId = destinationRoom.Id, Alias = "IN", FriendlyName = "Destination", SortOrder = 0 };
        db.AddRange(scene, subroom, destination);
        await db.SaveChangesAsync();
        var reference = futureReferences ? "subroom-new" : "subroom";
        var roomReference = futureReferences ? "destination-new" : "destination";
        var alias = futureReferences ? "NEW" : "IN";
        var source = new RoomTransition { RoomId = sourceRoom.Id, Alias = "OUT", FriendlyName = "Source", SourceSubroomReferenceText = reference, DestinationRoomReferenceText = roomReference, DestinationTransitionAliasText = alias, SortOrder = 0, IsArchived = archivedSource, ArchivedUtc = archivedSource ? DateTime.UtcNow : null };
        var connection = new SubroomConnection { RoomId = sourceRoom.Id, Alias = "C", FriendlyName = "Connection", SourceSubroomReferenceText = reference, DestinationSubroomReferenceText = reference, SortOrder = 0 };
        var check = new CheckLocation { RoomId = sourceRoom.Id, FriendlyName = "Check", SubroomReferenceText = reference, SortOrder = 0 };
        db.AddRange(source, connection, check);
        await db.SaveChangesAsync();
        await new LogicReferenceResolver(db).ResolveAsync();
        return new(group.Id, zone.Id, sourceRoom.Id, destinationRoom.Id, scene.Id, subroom.Id, destination.Id, source.Id, connection.Id, check.Id);
    }

    private LogicDbContext CreateContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);
    private sealed class TestDbContextFactory(string path) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => Create();
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create());
        private LogicDbContext Create() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    }
    private sealed record FixtureIds(Guid GroupId, Guid ZoneId, Guid SourceRoomId, Guid DestinationRoomId, Guid MapSceneId, Guid SubroomId, Guid DestinationTransitionId, Guid SourceTransitionId, Guid ConnectionId, Guid CheckId);
}
