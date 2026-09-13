using ImageMagick;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SceneImageBuilder;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class SceneImageCaptureServiceTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"silksong-scene-capture-tests-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(root, "logic.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        var source = Path.Combine(root, "data", "scene-source");
        Directory.CreateDirectory(source);
        using (var master = new MagickImage(MagickColors.Red, 2048, 1024)) master.Write(Path.Combine(source, "room-map-hd.png"));
        await SceneImageBuilderService.BuildAsync(source);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Estimate_UsesComposedRoomBoundsAndOverlayWithoutVerticalMirroring()
    {
        var roomId = await AddEligibleRoomAsync();

        var estimate = await CreateService().GetEstimateAsync(roomId);

        Assert.InRange(estimate.ScaleXPercent, 13.9, 14.1);
        Assert.InRange(estimate.ScaleYPercent, 19.9, 20.1);
        Assert.InRange(estimate.PanXPercent, 35.9, 36.1);
        Assert.InRange(estimate.PanYPercent, -30.1, -29.9);
    }

    [Fact]
    public async Task Apply_ComposesTilesPersistsTransformAndFailedRecaptureRetainsPriorCapture()
    {
        var roomId = await AddEligibleRoomAsync();
        var service = CreateService();
        var first = await service.GetEstimateAsync(roomId);

        await service.ApplyAsync(roomId, first);

        var files = new SceneImageFileService(root);
        var output = files.GetRoomImagePath(roomId);
        Assert.True(File.Exists(output));
        var firstBytes = await File.ReadAllBytesAsync(output);
        using (var image = new MagickImage(output)) Assert.Equal(2d, image.Width / (double)image.Height, 3);
        await using (var db = CreateContext())
        {
            var room = await db.Rooms.SingleAsync(item => item.Id == roomId);
            Assert.Equal(first.ScaleXPercent, room.SceneImageScaleXPercent);
            Assert.Equal(first.PanYPercent, room.SceneImagePanYPercent);
            Assert.False(room.IsSceneImageStale);
        }

        File.Delete(Path.Combine(root, "data", "scene-source", "tiles", "0-0.webp"));
        await Assert.ThrowsAnyAsync<Exception>(() => service.ApplyAsync(roomId, new(first.ScaleXPercent + 1, first.ScaleYPercent, first.PanXPercent, first.PanYPercent)));

        Assert.True(File.Exists(output));
        await using var verification = CreateContext();
        var persisted = await verification.Rooms.SingleAsync(item => item.Id == roomId);
        Assert.Equal(first.ScaleXPercent, persisted.SceneImageScaleXPercent);
        Assert.Equal(first.ScaleYPercent, persisted.SceneImageScaleYPercent);
        Assert.Equal(first.PanXPercent, persisted.SceneImagePanXPercent);
        Assert.Equal(first.PanYPercent, persisted.SceneImagePanYPercent);
        Assert.False(persisted.IsSceneImageStale);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task MigrationCurrentSqliteOwnedFilesystem_CaptureStaleArchiveRestoreAndPermanentDeleteFollowRoomImageLifecycle()
    {
        var roomId = await AddEligibleRoomAsync();
        var service = CreateService();
        var draft = await service.GetEstimateAsync(roomId);
        await service.ApplyAsync(roomId, draft);
        var files = new SceneImageFileService(root);
        Assert.True(files.Exists(roomId));

        // A changed complete pair retains the transform but makes the generated
        // file unavailable to presentation until recapture.
        Room changed;
        await using (var db = CreateContext())
        {
            var room = await db.Rooms.SingleAsync(x => x.Id == roomId);
            room.SceneUnitWidth = 101;
            changed = room;
        }
        await new LogicCatalogService(new Factory(DatabasePath)).SaveAsync(changed);
        await using (var stale = CreateContext())
        {
            var room = await stale.Rooms.SingleAsync(x => x.Id == roomId);
            Assert.True(room.IsSceneImageStale);
            Assert.Equal(draft.ScaleXPercent, room.SceneImageScaleXPercent);
        }

        var catalog = new LogicCatalogService(new Factory(DatabasePath), files);
        await catalog.SetArchivedAsync(new Room { Id = roomId }, true);
        Assert.True(files.Exists(roomId));
        await catalog.SetArchivedAsync(new Room { Id = roomId }, false);
        Assert.True(files.Exists(roomId));
        await catalog.SetArchivedAsync(new Room { Id = roomId }, true);
        await catalog.DeleteRoomPermanentlyAsync(roomId);
        Assert.False(files.Exists(roomId));
        await using var verify = CreateContext();
        Assert.Empty(await verify.Rooms.Where(x => x.Id == roomId).ToListAsync());
    }

    [Fact]
    public async Task RebuildPackageRooms_MigrationCurrentSqlite_UsesOnlyCurrentEligiblePackageRoomsWithoutWrites()
    {
        var eligibleId = await AddEligibleRoomAsync();
        var service = CreateService();
        var draft = await service.GetEstimateAsync(eligibleId);
        await service.ApplyAsync(eligibleId, draft);
        var files = new SceneImageFileService(root);
        var output = files.GetRoomImagePath(eligibleId);
        await File.WriteAllTextAsync(output, "old output");

        var archivedId = Guid.NewGuid(); var staleId = Guid.NewGuid(); var incompleteId = Guid.NewGuid(); var noDimensionsId = Guid.NewGuid();
        await using (var db = CreateContext())
        {
            db.Rooms.AddRange(
                new Room { Id = archivedId, FriendlyName = "archived", ReferenceId = "archived", IsArchived = true, SceneUnitWidth = 1, SceneUnitHeight = 1, SceneImageScaleXPercent = 100, SceneImageScaleYPercent = 100, SceneImagePanXPercent = 0, SceneImagePanYPercent = 0 },
                new Room { Id = staleId, FriendlyName = "stale", ReferenceId = "stale", SceneUnitWidth = 1, SceneUnitHeight = 1, SceneImageScaleXPercent = 100, SceneImageScaleYPercent = 100, SceneImagePanXPercent = 0, SceneImagePanYPercent = 0, IsSceneImageStale = true },
                new Room { Id = incompleteId, FriendlyName = "incomplete", ReferenceId = "incomplete", SceneUnitWidth = 1, SceneUnitHeight = 1, SceneImageScaleXPercent = 100 },
                new Room { Id = noDimensionsId, FriendlyName = "no dimensions", ReferenceId = "no-dimensions", SceneImageScaleXPercent = 100, SceneImageScaleYPercent = 100, SceneImagePanXPercent = 0, SceneImagePanYPercent = 0 });
            await db.SaveChangesAsync();
        }
        var before = await SnapshotRoomsAsync();

        var first = await service.RebuildPackageRoomsAsync(new([eligibleId, Guid.NewGuid(), archivedId, staleId, incompleteId, noDimensionsId]));

        Assert.Equal(SceneImageRebuildItemStatus.Generated, Assert.Single(first.Items, x => x.RoomId == eligibleId).Status);
        Assert.Equal(SceneImageRebuildItemStatus.Skipped, Assert.Single(first.Items, x => x.RoomId == archivedId).Status);
        Assert.Equal(SceneImageRebuildItemStatus.Skipped, Assert.Single(first.Items, x => x.RoomId == staleId).Status);
        Assert.Equal(SceneImageRebuildItemStatus.Skipped, Assert.Single(first.Items, x => x.RoomId == incompleteId).Status);
        Assert.Equal(SceneImageRebuildItemStatus.Skipped, Assert.Single(first.Items, x => x.RoomId == noDimensionsId).Status);
        Assert.DoesNotContain("old output", await File.ReadAllTextAsync(output));
        await File.WriteAllTextAsync(output, "old output again");
        var second = await service.RebuildPackageRoomsAsync(new([eligibleId]));
        Assert.Equal(SceneImageRebuildItemStatus.Generated, Assert.Single(second.Items).Status);
        Assert.DoesNotContain("old output again", await File.ReadAllTextAsync(output));
        Assert.Equivalent(before, await SnapshotRoomsAsync(), strict: true);
    }

    private async Task<object[]> SnapshotRoomsAsync()
    {
        await using var db = CreateContext();
        return await db.Rooms.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.RoomGroupId, x.ReferenceId, x.FriendlyName, x.InGameId, x.Contributors, x.Comments, x.SceneUnitWidth, x.SceneUnitHeight, x.SceneImageScaleXPercent, x.SceneImageScaleYPercent, x.SceneImagePanXPercent, x.SceneImagePanYPercent, x.IsSceneImageStale, x.SortOrder, x.IsArchived, x.ArchivedUtc, x.CreatedUtc, x.UpdatedUtc }).Cast<object>().ToArrayAsync();
    }

    private async Task<Guid> AddEligibleRoomAsync()
    {
        await using var db = CreateContext();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 100, SceneUnitHeight = 50, SortOrder = 0 };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        var map = new Map { InGameId = "Map", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 100, MapUnitMaxY = 100, SortOrder = 0 };
        db.Maps.Add(map);
        await db.SaveChangesAsync();
        db.MapOverlays.Add(new MapOverlay { MapId = map.Id, FriendlyName = "Area map", ImageAssetKey = "area-map-hd", ScaleXPercent = 100, ScaleYPercent = 100, LeftOffsetPercent = 0, BottomOffsetPercent = 0, SortOrder = 0 });
        var zone = new MapZone { MapId = map.Id, InGameId = "Zone", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 100, MapUnitMaxY = 100 };
        db.MapZones.Add(zone);
        await db.SaveChangesAsync();
        var scene = new MapScene { MapZoneId = zone.Id, InGameId = "Room", ResolvedRoomId = room.Id };
        db.MapScenes.Add(scene);
        await db.SaveChangesAsync();
        db.MapChunks.Add(new MapChunk { MapSceneId = scene.Id, CacheIndex = 0, MapUnitMinX = 10, MapUnitMinY = 10, MapUnitMaxX = 30, MapUnitMaxY = 30, MapUnitZ = 0 });
        await db.SaveChangesAsync();
        return room.Id;
    }

    private SceneImageCaptureService CreateService() => new(new Factory(DatabasePath), new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService(), new SceneImageFileService(root));
    private LogicDbContext CreateContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={DatabasePath}").Options);

    private sealed class Factory(string path) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => Create();
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Create());
        private LogicDbContext Create() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
    }
}
