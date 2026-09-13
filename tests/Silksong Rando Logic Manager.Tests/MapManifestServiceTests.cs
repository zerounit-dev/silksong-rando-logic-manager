using System.Text;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class MapManifestServiceTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"silksong-map-manifest-{Guid.NewGuid():N}.db");

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
    public async Task Parser_ExcludesAnnotationsAndRejectsPartialBounds()
    {
        const string json = """{"gameMapRoot":{"name":"Map"},"zones":[{"mapZone":"Zone","visibleZoneUnionRootLocalBounds":{"min":{"x":0,"y":0},"max":{"x":2,"y":2}}}],"mapScenes":[{"mapZoneResolved":true,"mapZone":"Zone","mapBoundsRootLocal":{"min":{"x":0,"y":0},"max":{"x":1,"y":1},"center":{"z":3}},"initialState":"Shown","cacheReferences":[{"cacheKey":"Scene","cacheIndex":0}]},{"mapZoneResolved":false,"cacheReferences":[]}]}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var manifest = await new MapManifestParser().ParseAsync(stream);

        Assert.Equal("Map", manifest.MapInGameId);
        Assert.Single(manifest.Chunks);
        Assert.Equal(1, manifest.ExcludedAnnotations);

        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes(json.Replace("\"max\":{\"x\":1,\"y\":1}", "\"max\":{\"x\":1}")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MapManifestParser().ParseAsync(invalid));
    }

    [Fact]
    public async Task Parser_UsesAsyncReads()
    {
        const string json = """{"gameMapRoot":{"name":"Map"},"zones":[],"mapScenes":[]}""";
        await using var stream = new AsyncOnlyStream(Encoding.UTF8.GetBytes(json));

        var manifest = await new MapManifestParser().ParseAsync(stream);

        Assert.Equal("Map", manifest.MapInGameId);
    }

    [Fact]
    public async Task Parser_ExcludesNullMapSceneAnnotations()
    {
        const string json = """{"gameMapRoot":{"name":"Map"},"zones":[],"mapScenes":[null]}""";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var manifest = await new MapManifestParser().ParseAsync(stream);

        Assert.Empty(manifest.Chunks);
        Assert.Equal(1, manifest.ExcludedAnnotations);
    }

    [Fact]
    public async Task Reconcile_PreservesSceneLinkAndRemovesMissingHierarchy()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Linked", ReferenceId = "linked", InGameId = "Scene", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            roomId = room.Id;
        }

        var service = new MapManifestService(new Factory(databasePath));
        var first = await service.ReconcileAsync(Manifest(("Zone", "Scene", 0), ("Zone", "Other", 0)));
        Assert.Equal(6, first.New);

        Guid sceneId;
        Guid overlayId;
        await using (var db = CreateContext())
        {
            var scene = await db.MapScenes.SingleAsync(x => x.InGameId == "Scene");
            scene.FriendlyName = "Custom";
            scene.RoomReferenceText = " linked ";
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            sceneId = scene.Id;
            Assert.Equal(roomId, scene.ResolvedRoomId);
            var overlay = await db.MapOverlays.SingleAsync();
            Assert.Equal("Area map", overlay.FriendlyName);
            Assert.Equal("area-map-hd", overlay.ImageAssetKey);
            Assert.Equal(100, overlay.ScaleXPercent);
            Assert.Equal(100, overlay.ScaleYPercent);
            overlay.LeftOffsetPercent = 12.5;
            overlayId = overlay.Id;
            await db.SaveChangesAsync();
        }

        await service.ReconcileAsync(Manifest(("Zone", "Scene", 0, "Changed")));
        await using (var verification = CreateContext())
        {
            var scene = await verification.MapScenes.SingleAsync(x => x.Id == sceneId);
            Assert.Equal("Custom", scene.FriendlyName);
            Assert.Equal(" linked ", scene.RoomReferenceText);
            Assert.Equal(roomId, scene.ResolvedRoomId);
            Assert.Single(await verification.MapChunks.ToListAsync());
            Assert.Equal("Changed", (await verification.MapChunks.SingleAsync()).InitialState);
            var overlay = await verification.MapOverlays.SingleAsync();
            Assert.Equal(overlayId, overlay.Id);
            Assert.Equal(12.5, overlay.LeftOffsetPercent);
        }
    }

    [Fact]
    public async Task ReconciliationPlan_LeavesRemovalsAndChangesUnselected_SelectsAdditions_AndAppliesExplicitDeletionHierarchy()
    {
        var service = new MapManifestService(new Factory(databasePath));
        await service.ReconcileAsync(Manifest(("Zone", "Kept", 0), ("Zone", "Removed", 0)));

        var plan = await service.BuildPlanAsync(Manifest(("Zone", "Kept", 0, "Updated"), ("Zone", "Added", 0, "Shown")));

        Assert.Equal([MapReconciliationKind.Removed, MapReconciliationKind.Added, MapReconciliationKind.Changed, MapReconciliationKind.Unchanged], plan.Rows.Select(x => x.Kind).Distinct().ToArray());
        Assert.All(plan.Rows.Where(x => x.Kind == MapReconciliationKind.Removed), row => Assert.False(row.Selected));
        Assert.All(plan.Rows.Where(x => x.Kind == MapReconciliationKind.Added), row => Assert.True(row.Selected));
        Assert.All(plan.Rows.Where(x => x.Kind == MapReconciliationKind.Changed), row => Assert.False(row.Selected));
        Assert.All(plan.Rows.Where(x => x.Kind == MapReconciliationKind.Unchanged), row => Assert.False(row.Selected));
        Assert.Contains(plan.Rows, row => row.Entity == MapReconciliationEntity.Scene && row.Kind == MapReconciliationKind.Added && row.Locked);

        // Cancelling is simply discarding this plan: building it has no persistence side effect.
        await using (var beforeApply = CreateContext()) Assert.Equal(2, await beforeApply.MapChunks.CountAsync());

        foreach (var row in plan.Rows.Where(row => row.Kind == MapReconciliationKind.Changed)) plan.SetRowSelected(row, true);
        await service.ApplyPlanAsync(plan);

        await using (var verification = CreateContext())
        {
            Assert.Equal(3, await verification.MapChunks.CountAsync());
            Assert.Equal("Shown", await verification.MapChunks.Where(x => x.MapScene!.InGameId == "Removed").Select(x => x.InitialState).SingleAsync());
            Assert.Equal("Updated", await verification.MapChunks.Where(x => x.MapScene!.InGameId == "Kept").Select(x => x.InitialState).SingleAsync());
        }

        var removalPlan = await service.BuildPlanAsync(Manifest(("Zone", "Kept", 0, "Updated"), ("Zone", "Added", 0, "Shown")));
        var removedScene = Assert.Single(removalPlan.Rows, row => row.Kind == MapReconciliationKind.Removed && row.Entity == MapReconciliationEntity.Scene);
        var removedChunk = Assert.Single(removalPlan.Rows, row => row.Kind == MapReconciliationKind.Removed && row.Entity == MapReconciliationEntity.Chunk);
        foreach (var row in removalPlan.RowsFor(MapReconciliationKind.Removed).Where(row => !row.Locked)) removalPlan.SetRowSelected(row, true);
        Assert.All(removalPlan.RowsFor(MapReconciliationKind.Removed), row => Assert.True(row.Selected));
        foreach (var row in removalPlan.RowsFor(MapReconciliationKind.Removed).Where(row => !row.Locked).ToList()) removalPlan.SetRowSelected(row, false);
        Assert.All(removalPlan.RowsFor(MapReconciliationKind.Removed), row => Assert.False(row.Selected));
        removalPlan.SetRowSelected(removedScene, true);
        Assert.True(removedScene.Selected);
        Assert.True(removedChunk.Selected);
        Assert.True(removedChunk.Locked);

        await service.ApplyPlanAsync(removalPlan);

        await using var afterRemoval = CreateContext();
        Assert.DoesNotContain(await afterRemoval.MapScenes.ToListAsync(), scene => scene.InGameId == "Removed");
        Assert.DoesNotContain(await afterRemoval.MapChunks.ToListAsync(), chunk => chunk.MapScene!.InGameId == "Removed");
    }

    [Fact]
    public void ReviewState_FiltersOnlyFoundRows_AndRetainsPerRowSelection()
    {
        var removedScene = ReviewRow(MapReconciliationKind.Removed, MapReconciliationEntity.Scene, "Removed scene", zone: "Zone", scene: "Removed");
        var removedChunk = ReviewRow(MapReconciliationKind.Removed, MapReconciliationEntity.Chunk, "Removed chunk", zone: "Zone", scene: "Removed");
        var plan = new MapReconciliationPlan
        {
            Manifest = new MapManifest("Map", [], [], 0),
            Rows =
            [
                ReviewRow(MapReconciliationKind.Added, MapReconciliationEntity.Chunk, "Added chunk", selected: true),
                removedScene,
                removedChunk,
                ReviewRow(MapReconciliationKind.Changed, MapReconciliationEntity.Chunk, "Changed chunk", selected: true),
                ReviewRow(MapReconciliationKind.Unchanged, MapReconciliationEntity.Chunk, "Unchanged chunk")
            ]
        };
        var review = new MapManifestReviewState(plan);

        var filtered = review.GetRows("Zone", MapReconciliationKind.Removed, "chunk");
        Assert.Single(filtered.Rows);
        Assert.Equal(2, filtered.Total);

        review.SetRowSelected(removedScene, true);
        Assert.True(removedScene.Selected);
        Assert.True(removedChunk.Selected);
        Assert.True(removedChunk.Locked);

        review.SetRowSelected(removedScene, false);
        Assert.False(removedScene.Selected);
        Assert.False(removedChunk.Selected);
        Assert.False(removedChunk.Locked);
    }

    [Fact]
    public void ReviewState_PinsTheActualZoneRowBeforeCombinedFiltersWithoutDuplicatingIt()
    {
        var zone = ReviewRow(MapReconciliationKind.Changed, MapReconciliationEntity.Zone, "Zone bounds", zone: "Zone");
        var changedChunk = ReviewRow(MapReconciliationKind.Changed, MapReconciliationEntity.Chunk, "matching chunk", zone: "Zone", scene: "Scene");
        var addedChunk = ReviewRow(MapReconciliationKind.Added, MapReconciliationEntity.Chunk, "added chunk", true, "Zone", "Scene");
        var review = new MapManifestReviewState(new MapReconciliationPlan { Manifest = new("Map", [], [], 0), Rows = [changedChunk, addedChunk, zone] });

        var changedSearch = review.GetRows("Zone", MapReconciliationKind.Changed, "matching");
        Assert.Equal([zone.Id, changedChunk.Id], changedSearch.Rows.Select(row => row.Id));
        Assert.Equal(3, changedSearch.Total);

        var excludedByBoth = review.GetRows("Zone", MapReconciliationKind.Added, "added");
        Assert.Equal([zone.Id, addedChunk.Id], excludedByBoth.Rows.Select(row => row.Id));
        Assert.Equal(3, excludedByBoth.Total);
    }

    [Fact]
    public async Task ReconciliationPlan_SelectsOnlyAddedAndAreaGrowingChangedRows_AndApplyLeavesOtherChangesDeferred()
    {
        var service = new MapManifestService(new Factory(databasePath));
        await service.ReconcileAsync(Manifest(("Zone", "Scene", 0), ("Zone", "Scene", 1), ("Zone", "Scene", 2)));
        var manifest = new MapManifest("Map", [new("Zone", new(0, 0, 10, 10))],
        [
            new("Zone", "Scene", 0, "Shown", new(0, 0, 2, 2), 0), // grows from one square unit
            new("Zone", "Scene", 1, "Changed equal", new(0, 0, 1, 1), 0),
            new("Zone", "Scene", 2, "Shown", new(0, 0, .5, .5), 0),
            new("Zone", "Scene", 3, "Shown", new(0, 0, 1, 1), 0)
        ], 0);

        var plan = await service.BuildPlanAsync(manifest);
        var grow = plan.Rows.Single(row => row.Entity == MapReconciliationEntity.Chunk && row.CacheIndex == 0);
        var equal = plan.Rows.Single(row => row.Entity == MapReconciliationEntity.Chunk && row.CacheIndex == 1);
        var shrink = plan.Rows.Single(row => row.Entity == MapReconciliationEntity.Chunk && row.CacheIndex == 2);
        var added = plan.Rows.Single(row => row.Entity == MapReconciliationEntity.Chunk && row.CacheIndex == 3);

        Assert.True(grow.Selected);
        Assert.False(equal.Selected);
        Assert.False(shrink.Selected);
        Assert.True(added.Selected);

        await service.ApplyPlanAsync(plan);
        await using var verification = CreateContext();
        var chunks = await verification.MapChunks.OrderBy(chunk => chunk.CacheIndex).ToListAsync();
        Assert.Equal(4, chunks.Count);
        Assert.Equal(2, chunks[0].MapUnitMaxX);
        Assert.Equal("Shown", chunks[1].InitialState);
        Assert.Equal(1, chunks[2].MapUnitMaxX);
        Assert.Equal(1, chunks[3].MapUnitMaxX);
    }

    [Fact]
    public async Task ReviewState_VirtualizedFocusedSection_BoundsRenderedRowsAndRetainsNoninitialSelection()
    {
        var rows = Enumerable.Range(0, 100).Select(index =>
            ReviewRow(MapReconciliationKind.Changed, MapReconciliationEntity.Chunk, $"chunk {index}", zone: "Large", scene: $"Scene-{index}")).ToList();
        var review = new MapManifestReviewState(new MapReconciliationPlan { Manifest = new("Map", [], [], 0), Rows = rows });

        var section = review.GetRows("Large", MapReconciliationKind.Changed, null);
        var viewport = section.ViewportRows(80, 8);

        Assert.Equal(100, section.Rows.Count);
        Assert.Equal(8, viewport.Count);
        Assert.Equal(rows[80].Id, viewport[0].Id);
        Assert.DoesNotContain(rows[79], viewport);
        Assert.DoesNotContain(rows[88], viewport);

        var suppliedViewport = await section.LoadViewportAsync(new ItemsProviderRequest(80, 8, CancellationToken.None));
        Assert.Equal(100, suppliedViewport.TotalItemCount);
        Assert.Equal(viewport.Select(row => row.Id), suppliedViewport.Items.Select(row => row.Id));

        review.SetRowSelected(rows[80], true);
        Assert.True(rows[80].Selected);
        Assert.Empty(review.GetRows("Large", MapReconciliationKind.Changed, "no matches").Rows);
    }

    [Fact]
    public void ReviewState_ProjectsZoneSummarySelectionAndPreviewWithoutPersistingReviewState()
    {
        var add = ReviewRow(MapReconciliationKind.Added, MapReconciliationEntity.Chunk, "added", true, "Beta", "Scene"); add.ProposedBounds = new(5, 5, 7, 7);
        var change = ReviewRow(MapReconciliationKind.Changed, MapReconciliationEntity.Chunk, "changed", false, "Alpha", "Scene"); change.CurrentBounds = new(0, 0, 1, 1); change.ProposedBounds = new(10, 10, 12, 12);
        var unchanged = ReviewRow(MapReconciliationKind.Unchanged, MapReconciliationEntity.Zone, "same", false, "Gamma"); unchanged.CurrentBounds = new(20, 20, 21, 21);
        var plan = new MapReconciliationPlan { Manifest = new("Map", [], [], 0), Rows = [add, change, unchanged], MatchedGroupNames = new Dictionary<string, IReadOnlyList<string>> { ["Beta"] = ["Alpha", "zeta"] } };
        var review = new MapManifestReviewState(plan);

        Assert.Equal(["Alpha", "Beta", "Gamma"], review.Zones.Select(x => x.ZoneInGameId));
        Assert.Equal("Alpha, zeta", review.Zones.Single(x => x.ZoneInGameId == "Beta").MatchedGroups);
        Assert.Equal(new MapUnitBounds(0, 0, 7, 7), review.SelectedMapBounds());
        Assert.Contains(review.ZoneShapes("Alpha"), x => x.IsCurrent && x.Kind == MapReconciliationKind.Changed);
        Assert.Contains(review.ZoneShapes("Alpha"), x => !x.IsCurrent && x.Kind == MapReconciliationKind.Changed && !x.Selected);
        review.SetRowSelected(change, true);
        Assert.Equal(new MapUnitBounds(5, 5, 12, 12), review.SelectedMapBounds());
    }

    [Fact]
    public void ReviewState_MaterializesLargeFocusedZoneOnce_AndRebuildsForCurrentZoneSelection()
    {
        var rows = new List<MapReconciliationPlanRow>();
        for (var index = 0; index < 100; index++)
        {
            var scene = ReviewRow(MapReconciliationKind.Changed, MapReconciliationEntity.Scene, $"scene {index}", zone: "Large", scene: $"Scene-{index}");
            var chunk = ReviewRow(MapReconciliationKind.Changed, MapReconciliationEntity.Chunk, $"chunk {index}", zone: "Large", scene: $"Scene-{index}");
            chunk.CurrentBounds = new(index, 0, index + 1, 1);
            chunk.ProposedBounds = new(index + 1000, 10, index + 1001, 11);
            rows.Add(scene);
            rows.Add(chunk);
        }
        var review = new MapManifestReviewState(new MapReconciliationPlan { Manifest = new("Map", [], [], 0), Rows = rows });

        review.FocusZone("Large");
        var initialShapes = review.FocusedZoneShapes;

        Assert.Equal(1, review.FocusedZoneProjectionMaterializationCount);
        Assert.Equal(400, initialShapes.Count);
        var sceneZero = rows.Single(row => row.Entity == MapReconciliationEntity.Scene && row.SceneInGameId == "Scene-0");
        Assert.Contains(initialShapes, shape => shape.RowId == sceneZero.Id && shape.Bounds == new MapUnitBounds(0, 0, 1, 1));
        Assert.Equal(new MapUnitBounds(0, 0, 1100, 11), new MapUnitBounds(
            review.FocusedZoneViewBounds.Min(bounds => bounds.MinX), review.FocusedZoneViewBounds.Min(bounds => bounds.MinY),
            review.FocusedZoneViewBounds.Max(bounds => bounds.MaxX), review.FocusedZoneViewBounds.Max(bounds => bounds.MaxY)));

        var changedChunk = rows.Single(row => row.Entity == MapReconciliationEntity.Chunk && row.SceneInGameId == "Scene-50");
        review.SetRowSelected(changedChunk, true);

        Assert.Equal(2, review.FocusedZoneProjectionMaterializationCount);
        Assert.Contains(review.FocusedZoneShapes, shape => shape.RowId == changedChunk.Id && shape.Layer == "proposal" && shape.Selected && shape.Bounds == new MapUnitBounds(1050, 10, 1051, 11));
        Assert.Contains(review.FocusedZoneShapes, shape => shape.RowId == sceneZero.Id && shape.Bounds == new MapUnitBounds(0, 0, 1, 1));

        review.FocusZone("Large");
        Assert.Equal(2, review.FocusedZoneProjectionMaterializationCount);
        review.FocusZone(null);
        Assert.Empty(review.FocusedZoneShapes);
        review.FocusZone("Large");
        Assert.Equal(3, review.FocusedZoneProjectionMaterializationCount);
    }

    [Fact]
    public void ReviewState_SummaryMapFrameOmitsMatchingPredictedBounds_AndIncludesThemInViewBoundsWhenZoneShapesAreAbsent()
    {
        var map = ReviewRow(MapReconciliationKind.Unchanged, MapReconciliationEntity.Map, "map");
        map.CurrentBounds = new(0, 0, 4, 4);
        var chunk = ReviewRow(MapReconciliationKind.Unchanged, MapReconciliationEntity.Chunk, "chunk");
        chunk.CurrentBounds = new(0, 0, 4, 4);
        var review = new MapManifestReviewState(new MapReconciliationPlan { Manifest = new("Map", [], [], 0), Rows = [map, chunk] });

        var frames = review.SummaryMapFrameShapes().ToList();

        var frame = Assert.Single(frames);
        Assert.True(frame.IsCurrent);
        Assert.Equal(new MapUnitBounds(0, 0, 4, 4), frame.Bounds);
        Assert.False(frame.Selected);
        Assert.Equal(new MapUnitBounds(0, 0, 4, 4), Assert.Single(review.SummaryViewBounds()));
    }

    [Fact]
    public void ReviewState_SummaryMapFrameProjectsDifferingPredictedBoundsAsChangedProposal()
    {
        var map = ReviewRow(MapReconciliationKind.Changed, MapReconciliationEntity.Map, "map");
        map.CurrentBounds = new(0, 0, 4, 4);
        var changedChunk = ReviewRow(MapReconciliationKind.Changed, MapReconciliationEntity.Chunk, "chunk", true, "Zone");
        changedChunk.CurrentBounds = new(0, 0, 4, 4);
        changedChunk.ProposedBounds = new(10, 10, 12, 12);
        var review = new MapManifestReviewState(new MapReconciliationPlan { Manifest = new("Map", [], [], 0), Rows = [map, changedChunk] });

        var frames = review.SummaryMapFrameShapes().ToList();

        Assert.Collection(frames,
            current => { Assert.True(current.IsCurrent); Assert.Equal(new MapUnitBounds(0, 0, 4, 4), current.Bounds); },
            proposal =>
            {
                Assert.False(proposal.IsCurrent);
                Assert.Equal(MapReconciliationKind.Changed, proposal.Kind);
                Assert.Equal("proposal", proposal.Layer);
                Assert.False(proposal.Selected);
                Assert.False(proposal.IsSelectable);
                Assert.DoesNotContain("map-review-deferred", MapManifestReviewPreviewPresentation.CssClass(proposal));
                Assert.Equal(new MapUnitBounds(10, 10, 12, 12), proposal.Bounds);
            });
        Assert.Equal([new MapUnitBounds(0, 0, 4, 4), new MapUnitBounds(10, 10, 12, 12)], review.SummaryViewBounds());

        review.SetRowSelected(changedChunk, false);

        var deferredChunkProposal = Assert.Single(review.ZoneShapes(changedChunk.ZoneInGameId!), shape => shape.Layer == "proposal");
        Assert.True(deferredChunkProposal.IsSelectable);
        Assert.Contains("map-review-deferred", MapManifestReviewPreviewPresentation.CssClass(deferredChunkProposal));
        Assert.DoesNotContain(review.SummaryMapFrameShapes(), shape => !shape.IsCurrent);
    }

    private static MapReconciliationPlanRow ReviewRow(MapReconciliationKind kind, MapReconciliationEntity entity, string label, bool selected = false, string? zone = null, string? scene = null) => new()
    {
        Kind = kind,
        Entity = entity,
        Label = label,
        Selected = selected,
        ZoneInGameId = zone,
        SceneInGameId = scene
    };

    [Fact]
    public async Task OverlayMigration_CreatesDefaultForExistingMaps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"silksong-overlay-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new LogicDbContext(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260805214236_ReplaceLegacyGameMapWithImportedHierarchy");
            db.Maps.Add(new Map { InGameId = "Existing", SortOrder = 0 });
            await db.SaveChangesAsync();

            await migrator.MigrateAsync("20260806013622_AddMapOverlays");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"MapOverlays\" SET \"ScalePercent\" = 73.5;");
            await migrator.MigrateAsync();

            var overlay = await db.MapOverlays.SingleAsync();
            Assert.Equal("Area map", overlay.FriendlyName);
            Assert.Equal("area-map-hd", overlay.ImageAssetKey);
            Assert.Equal(73.5, overlay.ScaleXPercent);
            Assert.Equal(73.5, overlay.ScaleYPercent);
            Assert.Equal(0, overlay.LeftOffsetPercent);
            Assert.Equal(0, overlay.BottomOffsetPercent);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OverlayService_SavesFirstMapOverlayWhenDraftIdIsStale()
    {
        Guid mapId;
        Guid overlayId;
        await using (var db = CreateContext())
        {
            var map = new Map { InGameId = "Map", SortOrder = 0 };
            db.Maps.Add(map);
            await db.SaveChangesAsync();
            var newOverlay = new MapOverlay { MapId = map.Id, FriendlyName = "Area map", ImageAssetKey = "area-map-hd", SortOrder = 0 };
            db.MapOverlays.Add(newOverlay);
            await db.SaveChangesAsync();
            mapId = map.Id;
            overlayId = newOverlay.Id;
        }

        await new MapOverlayService(new Factory(databasePath)).SavePlacementAsync(new(mapId, 80, 120, -4, 6));

        await using var verification = CreateContext();
        var persistedOverlay = await verification.MapOverlays.SingleAsync(x => x.Id == overlayId);
        Assert.Equal(80, persistedOverlay.ScaleXPercent);
        Assert.Equal(120, persistedOverlay.ScaleYPercent);
        Assert.Equal(-4, persistedOverlay.LeftOffsetPercent);
        Assert.Equal(6, persistedOverlay.BottomOffsetPercent);
    }

    [Fact]
    public async Task OverlayService_ReportsMissingMapOverlayWithoutConcurrencyException()
    {
        var service = new MapOverlayService(new Factory(databasePath));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SavePlacementAsync(new(Guid.NewGuid(), 80, 120, 0, 0)));

        Assert.Equal("The selected map no longer has an image overlay. Reloaded map data; retry after reviewing the draft.", exception.Message);
    }

    [Fact]
    public async Task MapSceneResolution_PreservesAuthoredTextAndReportsArchivedTarget()
    {
        await using var db = CreateContext();
        var room = new Room { FriendlyName = "Linked", ReferenceId = "linked", SortOrder = 0 };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        var scene = await AddMapSceneAsync(db);
        scene.RoomReferenceText = " linked ";
        await db.SaveChangesAsync();

        await new LogicReferenceResolver(db).ResolveAsync();
        Assert.Equal(room.Id, scene.ResolvedRoomId);
        Assert.Equal(" linked ", scene.RoomReferenceText);

        room.IsArchived = true;
        await db.SaveChangesAsync();
        var report = await new LogicReferenceResolver(db).GetResolutionReportAsync();

        Assert.Contains(report.References, x =>
            x.EntityType == nameof(MapScene) &&
            x.EntityId == scene.Id &&
            x.FieldName == nameof(MapScene.RoomReferenceText) &&
            x.Status == ReferenceResolutionStatus.TargetArchived);
    }

    [Fact]
    public async Task RoomGroupZoneResolution_PreservesTextAndReportsMissingAndAmbiguousMatches()
    {
        await using var db = CreateContext();
        var firstMap = new Map { InGameId = "Map one", SortOrder = 0 };
        var secondMap = new Map { InGameId = "Map two", SortOrder = 1 };
        var group = new RoomGroup { FriendlyName = "Area", ZoneReferenceText = " forge ", SortOrder = 0 };
        db.AddRange(firstMap, secondMap, group);
        await db.SaveChangesAsync();
        var firstZone = new MapZone { MapId = firstMap.Id, InGameId = "Forge" };
        db.MapZones.Add(firstZone);
        await db.SaveChangesAsync();

        await new LogicReferenceResolver(db).ResolveAsync();
        Assert.Equal(" forge ", group.ZoneReferenceText);
        Assert.Equal(firstZone.Id, group.ResolvedMapZoneId);

        group.ZoneReferenceText = " missing ";
        await db.SaveChangesAsync();
        await new LogicReferenceResolver(db).ResolveAsync();
        Assert.Null(group.ResolvedMapZoneId);
        var report = await new LogicReferenceResolver(db).GetResolutionReportAsync();
        Assert.Contains(report.References, item =>
            item.EntityType == nameof(RoomGroup) &&
            item.EntityId == group.Id &&
            item.FieldName == nameof(RoomGroup.ZoneReferenceText) &&
            item.Status == ReferenceResolutionStatus.Unresolved);

        db.MapZones.Add(new MapZone { MapId = secondMap.Id, InGameId = "FORGE" });
        group.ZoneReferenceText = "forge";
        await db.SaveChangesAsync();
        await new LogicReferenceResolver(db).ResolveAsync();
        Assert.Null(group.ResolvedMapZoneId);
        report = await new LogicReferenceResolver(db).GetResolutionReportAsync();
        Assert.Contains(report.References, item =>
            item.EntityType == nameof(RoomGroup) &&
            item.EntityId == group.Id &&
            item.FieldName == nameof(RoomGroup.ZoneReferenceText) &&
            item.Status == ReferenceResolutionStatus.Ambiguous);
    }

    [Fact]
    public async Task AutoMatchUngroupedRooms_GroupsExactCacheKeySingleZoneRoomsWithoutLinkingScenes()
    {
        Guid forgeGroupId;
        Guid linkedRoomId;
        Guid multiZoneRoomId;
        Guid linkedSceneId;
        Guid manualSceneId;
        Guid duplicateSceneId;
        await using (var db = CreateContext())
        {
            var map = new Map { InGameId = "Map", SortOrder = 0 };
            db.Maps.Add(map);
            await db.SaveChangesAsync();
            var forgeZone = new MapZone { MapId = map.Id, InGameId = "Forge" };
            var boneZone = new MapZone { MapId = map.Id, InGameId = "Bone" };
            db.MapZones.AddRange(forgeZone, boneZone);
            var forgeGroup = new RoomGroup { FriendlyName = "Forge", ZoneReferenceText = "forge", SortOrder = 0 };
            var boneGroup = new RoomGroup { FriendlyName = "Bone", ZoneReferenceText = "bone", SortOrder = 1 };
            db.RoomGroups.AddRange(forgeGroup, boneGroup);
            await db.SaveChangesAsync();
            db.MapScenes.AddRange(
                new MapScene { MapZoneId = forgeZone.Id, InGameId = " linked-scene " },
                new MapScene { MapZoneId = forgeZone.Id, InGameId = "multi-zone" },
                new MapScene { MapZoneId = boneZone.Id, InGameId = "MULTI-ZONE" },
                new MapScene { MapZoneId = forgeZone.Id, InGameId = "manual-scene", RoomReferenceText = "manual-reference" },
                new MapScene { MapZoneId = forgeZone.Id, InGameId = "duplicate-scene" });
            var linkedRoom = new Room { FriendlyName = "Linked room", ReferenceId = "linked-reference", InGameId = "Linked-Scene", SortOrder = 7 };
            var multiZoneRoom = new Room { FriendlyName = "Multi zone", ReferenceId = "multi-reference", InGameId = "multi-zone", SortOrder = 8 };
            var manualRoom = new Room { FriendlyName = "Manual", ReferenceId = "manual-reference", InGameId = "manual-scene", SortOrder = 9 };
            var duplicateOne = new Room { FriendlyName = "Duplicate one", ReferenceId = "duplicate-one", InGameId = "duplicate-scene", SortOrder = 10 };
            var duplicateTwo = new Room { FriendlyName = "Duplicate two", ReferenceId = "duplicate-two", InGameId = "DUPLICATE-SCENE", SortOrder = 11 };
            db.Rooms.AddRange(linkedRoom, multiZoneRoom, manualRoom, duplicateOne, duplicateTwo);
            await db.SaveChangesAsync();
            forgeGroupId = forgeGroup.Id;
            linkedRoomId = linkedRoom.Id;
            multiZoneRoomId = multiZoneRoom.Id;
            linkedSceneId = (await db.MapScenes.SingleAsync(scene => scene.InGameId == " linked-scene ")).Id;
            manualSceneId = (await db.MapScenes.SingleAsync(scene => scene.InGameId == "manual-scene")).Id;
            duplicateSceneId = (await db.MapScenes.SingleAsync(scene => scene.InGameId == "duplicate-scene")).Id;
        }

        var result = await new LogicCatalogService(new Factory(databasePath)).AutoMatchUngroupedRoomsAsync();

        await using var verification = CreateContext();
        Assert.Equal(4, result);
        var persistedLinkedRoom = await verification.Rooms.SingleAsync(room => room.Id == linkedRoomId);
        Assert.Equal(forgeGroupId, persistedLinkedRoom.RoomGroupId);
        Assert.Equal(7, persistedLinkedRoom.SortOrder);
        Assert.Null(await verification.Rooms.Where(room => room.Id == multiZoneRoomId).Select(room => room.RoomGroupId).SingleAsync());
        Assert.Null(await verification.MapScenes.Where(scene => scene.Id == linkedSceneId).Select(scene => scene.RoomReferenceText).SingleAsync());
        Assert.Equal("manual-reference", await verification.MapScenes.Where(scene => scene.Id == manualSceneId).Select(scene => scene.RoomReferenceText).SingleAsync());
        Assert.Null(await verification.MapScenes.Where(scene => scene.Id == duplicateSceneId).Select(scene => scene.RoomReferenceText).SingleAsync());
    }

    [Fact]
    public async Task Reconcile_AutoLinksUniqueActiveGameIdAndLeavesOtherCandidatesBlank()
    {
        await using (var db = CreateContext())
        {
            db.Rooms.AddRange(
                new Room { FriendlyName = "Unique", ReferenceId = "unique-ref", InGameId = " unique-scene ", SortOrder = 0 },
                new Room { FriendlyName = "Duplicate one", ReferenceId = "duplicate-one", InGameId = "duplicate-scene", SortOrder = 1 },
                new Room { FriendlyName = "Duplicate two", ReferenceId = "duplicate-two", InGameId = "DUPLICATE-SCENE", SortOrder = 2 },
                new Room { FriendlyName = "Archived", ReferenceId = "archived-ref", InGameId = "archived-scene", IsArchived = true, SortOrder = 3 });
            await db.SaveChangesAsync();
        }

        var result = await new MapManifestService(new Factory(databasePath)).ReconcileAsync(Manifest(
            ("Zone", "UNIQUE-SCENE", 0),
            ("Zone", "missing-scene", 0),
            ("Zone", "duplicate-scene", 0),
            ("Zone", "archived-scene", 0)));

        Assert.Equal(1, result.AutoLinked);
        Assert.Equal(3, result.UnresolvedAutoLinkCandidates);
        var unchanged = await new MapManifestService(new Factory(databasePath)).ReconcileAsync(Manifest(
            ("Zone", "UNIQUE-SCENE", 0),
            ("Zone", "missing-scene", 0),
            ("Zone", "duplicate-scene", 0),
            ("Zone", "archived-scene", 0)));
        Assert.Equal(0, unchanged.AutoLinked);
        Assert.Equal(0, unchanged.UnresolvedAutoLinkCandidates);
        await using var verification = CreateContext();
        var scenes = await verification.MapScenes.OrderBy(x => x.InGameId).ToListAsync();
        Assert.Equal("unique-ref", scenes.Single(x => x.InGameId == "UNIQUE-SCENE").RoomReferenceText);
        Assert.NotNull(scenes.Single(x => x.InGameId == "UNIQUE-SCENE").ResolvedRoomId);
        Assert.All(scenes.Where(x => x.InGameId != "UNIQUE-SCENE"), x => Assert.True(string.IsNullOrWhiteSpace(x.RoomReferenceText)));
    }

    [Fact]
    public async Task ReconciliationPlan_UsesInclusiveGeometryToleranceAndExactChunkState()
    {
        await using (var db = CreateContext())
        {
            var map = new Map { InGameId = "Map", SortOrder = 0, MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 };
            db.Maps.Add(map); await db.SaveChangesAsync();
            var zone = new MapZone { MapId = map.Id, InGameId = "Zone", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 };
            db.MapZones.Add(zone); await db.SaveChangesAsync();
            var scene = new MapScene { MapZoneId = zone.Id, InGameId = "Scene" };
            db.MapScenes.Add(scene); await db.SaveChangesAsync();
            db.MapChunks.Add(new MapChunk { MapSceneId = scene.Id, CacheIndex = 0, InitialState = "Shown", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10, MapUnitZ = 2 });
            await db.SaveChangesAsync();
        }

        var service = new MapManifestService(new Factory(databasePath));
        var boundary = new MapManifest("Map", [new("Zone", new(0.01, 0, 10, 10))], [new("Zone", "Scene", 0, "Shown", new(0, 0.01, 10, 10), 2.01)], 0);
        var boundaryPlan = await service.BuildPlanAsync(boundary);
        Assert.All(boundaryPlan.Rows.Where(row => row.Entity is MapReconciliationEntity.Map or MapReconciliationEntity.Zone or MapReconciliationEntity.Chunk), row => Assert.Equal(MapReconciliationKind.Unchanged, row.Kind));

        var overBoundary = boundary with { Zones = [new("Zone", new(0.0101, 0, 10, 10))], Chunks = [new("Zone", "Scene", 0, "Different", new(0, 0.0101, 10, 10), 2.0101)] };
        var changedPlan = await service.BuildPlanAsync(overBoundary);
        Assert.Equal(MapReconciliationKind.Changed, Assert.Single(changedPlan.Rows, row => row.Entity == MapReconciliationEntity.Zone).Kind);
        Assert.Equal(MapReconciliationKind.Changed, Assert.Single(changedPlan.Rows, row => row.Entity == MapReconciliationEntity.Chunk).Kind);
    }

    [Fact]
    public async Task ApplyPlan_UpdatesMatchedGeometryAvailabilityWithoutDeletingStructure()
    {
        Guid chunkId;
        await using (var db = CreateContext())
        {
            var map = new Map { InGameId = "Map", SortOrder = 0 };
            db.Maps.Add(map); await db.SaveChangesAsync();
            var zone = new MapZone { MapId = map.Id, InGameId = "Zone" };
            db.MapZones.Add(zone); await db.SaveChangesAsync();
            var scene = new MapScene { MapZoneId = zone.Id, InGameId = "Scene" };
            db.MapScenes.Add(scene); await db.SaveChangesAsync();
            var chunk = new MapChunk { MapSceneId = scene.Id, CacheIndex = 0, InitialState = "Old" };
            db.MapChunks.Add(chunk); await db.SaveChangesAsync();
            chunkId = chunk.Id;
        }

        var service = new MapManifestService(new Factory(databasePath));
        var addedPlan = await service.BuildPlanAsync(new("Map", [new("Zone", new(1.23456, 2, 3, 4))], [new("Zone", "Scene", 0, "New", new(1.23456, 2, 3, 4), 5.6789)], 0));
        var addedZone = Assert.Single(addedPlan.Rows, row => row.Entity == MapReconciliationEntity.Zone);
        var addedChunk = Assert.Single(addedPlan.Rows, row => row.Entity == MapReconciliationEntity.Chunk);
        Assert.Equal(MapReconciliationKind.Added, addedZone.Kind); Assert.True(addedZone.Selected);
        Assert.Equal(MapReconciliationKind.Added, addedChunk.Kind); Assert.True(addedChunk.Selected);
        await service.ApplyPlanAsync(addedPlan);

        await using (var verification = CreateContext())
        {
            Assert.Single(await verification.MapChunks.ToListAsync());
            var chunk = await verification.MapChunks.SingleAsync(chunk => chunk.Id == chunkId);
            Assert.Equal(1.23456, chunk.MapUnitMinX); Assert.Equal(5.6789, chunk.MapUnitZ); Assert.Equal("New", chunk.InitialState);
        }

        var removedPlan = await service.BuildPlanAsync(new("Map", [new("Zone", MapUnitBounds.Unavailable)], [new("Zone", "Scene", 0, "Cleared", MapUnitBounds.Unavailable, null)], 0));
        var removedZone = Assert.Single(removedPlan.Rows, row => row.Entity == MapReconciliationEntity.Zone);
        var removedChunk = Assert.Single(removedPlan.Rows, row => row.Entity == MapReconciliationEntity.Chunk);
        Assert.Equal(MapReconciliationKind.Removed, removedZone.Kind); Assert.Equal(MapReconciliationKind.Removed, removedChunk.Kind);
        removedPlan.SetRowSelected(removedZone, true); removedPlan.SetRowSelected(removedChunk, true);
        await service.ApplyPlanAsync(removedPlan);

        await using var cleared = CreateContext();
        Assert.Single(await cleared.MapChunks.ToListAsync());
        var persisted = await cleared.MapChunks.SingleAsync(chunk => chunk.Id == chunkId);
        Assert.Null(persisted.MapUnitMinX); Assert.Null(persisted.MapUnitZ); Assert.Equal("Cleared", persisted.InitialState);
        Assert.Null((await cleared.Maps.SingleAsync()).MapUnitMinX);
    }

    [Fact]
    public async Task MapLinkEditor_AtomicallyAppliesDraftsAndAutoLinksBlankEdits()
    {
        Guid linkedSceneId;
        Guid manualSceneId;
        await using (var db = CreateContext())
        {
            db.Rooms.Add(new Room { FriendlyName = "Linked", ReferenceId = "linked-ref", InGameId = "cache", SortOrder = 0 });
            await db.SaveChangesAsync();
            var linkedScene = await AddMapSceneAsync(db);
            linkedScene.InGameId = "cache";
            var manualScene = new MapScene { MapZoneId = linkedScene.MapZoneId, InGameId = "manual", RoomReferenceText = "manual-text" };
            db.MapScenes.Add(manualScene);
            await db.SaveChangesAsync();
            linkedSceneId = linkedScene.Id;
            manualSceneId = manualScene.Id;
        }

        var links = new MapLinkService(new Factory(databasePath));
        await links.SaveAsync([new(linkedSceneId, ""), new(manualSceneId, "manual-text")]);

        await using var verification = CreateContext();
        var linked = await verification.MapScenes.SingleAsync(x => x.Id == linkedSceneId);
        var manual = await verification.MapScenes.SingleAsync(x => x.Id == manualSceneId);
        Assert.Equal("linked-ref", linked.RoomReferenceText);
        Assert.NotNull(linked.ResolvedRoomId);
        Assert.Equal("manual-text", manual.RoomReferenceText);
    }

    [Fact]
    public async Task MapLinkEditor_MergeBlankMapScenesIntoRooms_LinksOnlyUniqueBlankReferences()
    {
        Guid linkedSceneId;
        Guid suffixSceneId;
        Guid manualSceneId;
        Guid duplicateSceneId;
        await using (var db = CreateContext())
        {
            db.Rooms.AddRange(
                new Room { FriendlyName = "Linked", ReferenceId = "linked-ref", InGameId = "linked-scene", SortOrder = 0 },
                new Room { FriendlyName = "Bone east", ReferenceId = "bone-east-ref", InGameId = "Bone_East", SortOrder = 1 },
                new Room { FriendlyName = "Bone east 03", ReferenceId = "bone-east-03-ref", InGameId = "Bone_East_03", SortOrder = 2 },
                new Room { FriendlyName = "Duplicate one", ReferenceId = "duplicate-one", InGameId = "duplicate-scene", SortOrder = 3 },
                new Room { FriendlyName = "Duplicate two", ReferenceId = "duplicate-two", InGameId = "DUPLICATE-SCENE", SortOrder = 4 });
            await db.SaveChangesAsync();
            var linkedScene = await AddMapSceneAsync(db);
            linkedScene.InGameId = " linked-scene ";
            var manualScene = new MapScene { MapZoneId = linkedScene.MapZoneId, InGameId = "linked-scene", RoomReferenceText = "manual-reference" };
            var suffixScene = new MapScene { MapZoneId = linkedScene.MapZoneId, InGameId = "Bone_East_03_right" };
            var duplicateScene = new MapScene { MapZoneId = linkedScene.MapZoneId, InGameId = "duplicate-scene" };
            db.MapScenes.AddRange(manualScene, suffixScene, duplicateScene);
            await db.SaveChangesAsync();
            linkedSceneId = linkedScene.Id;
            suffixSceneId = suffixScene.Id;
            manualSceneId = manualScene.Id;
            duplicateSceneId = duplicateScene.Id;
        }

        var merged = await new MapLinkService(new Factory(databasePath)).MergeBlankMapScenesIntoRoomsAsync();

        await using var verification = CreateContext();
        Assert.Equal(2, merged);
        Assert.Equal("linked-ref", await verification.MapScenes.Where(scene => scene.Id == linkedSceneId).Select(scene => scene.RoomReferenceText).SingleAsync());
        Assert.Equal("bone-east-03-ref", await verification.MapScenes.Where(scene => scene.Id == suffixSceneId).Select(scene => scene.RoomReferenceText).SingleAsync());
        Assert.Equal("manual-reference", await verification.MapScenes.Where(scene => scene.Id == manualSceneId).Select(scene => scene.RoomReferenceText).SingleAsync());
        Assert.Null(await verification.MapScenes.Where(scene => scene.Id == duplicateSceneId).Select(scene => scene.RoomReferenceText).SingleAsync());
        Assert.NotNull(await verification.MapScenes.Where(scene => scene.Id == linkedSceneId).Select(scene => scene.ResolvedRoomId).SingleAsync());
    }

    [Fact]
    public async Task RoomReferenceSnapshot_UpdatesCapturedMapSceneAndReresolves()
    {
        Guid roomId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Linked", ReferenceId = "old-linked", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var mapScene = await AddMapSceneAsync(db);
            mapScene.RoomReferenceText = "old-linked";
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            roomId = room.Id;
        }

        var catalog = new LogicCatalogService(new Factory(databasePath));
        var candidates = await catalog.GetRoomReferenceUpdateCandidatesAsync(roomId);
        Assert.Single(candidates);
        Assert.Equal(nameof(MapScene), candidates[0].EntityType);

        await using (var db = CreateContext())
        {
            var room = await db.Rooms.SingleAsync(x => x.Id == roomId);
            room.ReferenceId = "new-linked";
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
        }

        await catalog.UpdateReferenceTextAsync(candidates, "new-linked");
        await using var verification = CreateContext();
        var scene = await verification.MapScenes.SingleAsync();
        Assert.Equal("new-linked", scene.RoomReferenceText);
        Assert.Equal(roomId, scene.ResolvedRoomId);
    }

    [Fact]
    public async Task DeleteArchivedRoom_ClearsMapSceneResolverIdWithoutChangingAuthoredText()
    {
        Guid roomId;
        Guid sceneId;
        await using (var db = CreateContext())
        {
            var room = new Room { FriendlyName = "Linked", ReferenceId = "linked", SortOrder = 0 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var mapScene = await AddMapSceneAsync(db);
            mapScene.RoomReferenceText = " linked ";
            await db.SaveChangesAsync();
            await new LogicReferenceResolver(db).ResolveAsync();
            room.IsArchived = true;
            await db.SaveChangesAsync();
            roomId = room.Id;
            sceneId = mapScene.Id;
        }

        await new LogicCatalogService(new Factory(databasePath)).DeleteRoomPermanentlyAsync(roomId);

        await using var verification = CreateContext();
        var scene = await verification.MapScenes.SingleAsync(x => x.Id == sceneId);
        Assert.Equal(" linked ", scene.RoomReferenceText);
        Assert.Null(scene.ResolvedRoomId);
    }

    [Fact]
    public async Task ReplacementMigration_RemovesLegacyMapStorageAndPreservesRooms()
    {
        var path = Path.Combine(Path.GetTempPath(), $"silksong-map-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = new LogicDbContext(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260805080622_AddRoomSceneBounds");
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Rooms" ("Id", "RoomGroupId", "ReferenceId", "FriendlyName", "InGameId", "Comments", "SortOrder", "IsArchived", "ArchivedUtc", "CreatedUtc", "UpdatedUtc", "SceneBoundsMaxX", "SceneBoundsMaxY", "SceneBoundsMinX", "SceneBoundsMinY")
                VALUES ({Guid.NewGuid()}, NULL, {"preserved"}, {"Preserved"}, NULL, {""}, 0, 0, NULL, {now}, {now}, NULL, NULL, NULL, NULL);
                """);

            await migrator.MigrateAsync();
            Assert.Equal("Preserved", (await db.Rooms.SingleAsync()).FriendlyName);
            Assert.False(await TableExistsAsync(db, "GameMapConfigurations"));
            Assert.False(await TableExistsAsync(db, "MapImageCalibrations"));
            Assert.False(await TableExistsAsync(db, "RoomMapCoordinates"));
            Assert.True(await TableExistsAsync(db, "Maps"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static MapManifest Manifest(params (string Zone, string Scene, int Index, string State)[] values) => new("Map", [new ImportedMapZone("Zone", new(0, 0, 10, 10))], values.Select(x => new ImportedMapChunk(x.Zone, x.Scene, x.Index, x.State, new(0, 0, 1, 1), 0)).ToList(), 0);
    private static MapManifest Manifest(params (string Zone, string Scene, int Index)[] values) => Manifest(values.Select(x => (x.Zone, x.Scene, x.Index, "Shown")).ToArray());
    private static async Task<MapScene> AddMapSceneAsync(LogicDbContext db)
    {
        var map = new Map { InGameId = "Map", SortOrder = 0 };
        db.Maps.Add(map);
        await db.SaveChangesAsync();
        var zone = new MapZone { MapId = map.Id, InGameId = "Zone" };
        db.MapZones.Add(zone);
        await db.SaveChangesAsync();
        var scene = new MapScene { MapZoneId = zone.Id, InGameId = "Scene" };
        db.MapScenes.Add(scene);
        await db.SaveChangesAsync();
        return scene;
    }
    private LogicDbContext CreateContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);
    private static async Task<bool> TableExistsAsync(LogicDbContext db, string table)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        var parameter = command.CreateParameter(); parameter.ParameterName = "$name"; parameter.Value = table; command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }
    private sealed class Factory(string path) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").Options);
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class AsyncOnlyStream(byte[] buffer) : Stream
    {
        private readonly MemoryStream inner = new(buffer);
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Synchronous reads are not supported.");
        public override int Read(Span<byte> buffer) => throw new NotSupportedException("Synchronous reads are not supported.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
