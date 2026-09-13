using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using System.Data.Common;
using Silksong_Rando_Logic_Manager.Components;
using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;
using Fixture = MapCutover.Tests.TypedMapAndStatusTests.Fixture;

namespace MapCutover.Tests;

public sealed class MapLinkMergeHostTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LandingMergeThenCancelReloadsTypedMapOnlyWhenMergeChangedData(bool successfulMerge)
    {
        await using var fixture = await Fixture.CreateAsync();
        await SeedBlankMapScene(fixture, successfulMerge);
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var maps = new CountingLandingLoader();
        context.Services.AddSingleton<IAreaMapLoader>(maps);
        context.Services.AddSingleton(new MapManifestParser()); context.Services.AddSingleton(new MapManifestService(fixture));
        context.Services.AddSingleton(new MapOverlayService(fixture)); context.Services.AddSingleton(new MapOverlayPlacementService());
        context.Services.AddSingleton(new DiagnosticState()); context.Services.AddSingleton(new MapLinkService(fixture));
        var page = context.RenderComponent<MapLanding>();
        page.WaitForAssertion(() => Assert.Equal(1, maps.Loads));
        page.FindAll("button").Single(x => x.TextContent.Trim() == "edit map links").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".map-link-modal")));
        page.FindAll(".map-link-modal button").Single(x => x.TextContent.Trim() == "merge map scenes into rooms").Click();
        page.WaitForAssertion(() => Assert.Equal(successfulMerge ? "merged 1 map scene" : "merged 0 map scenes", page.Find(".map-link-merge-result").TextContent.Trim()));
        page.FindAll(".map-link-modal button").Single(x => x.TextContent.Trim() == "cancel").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".map-link-modal")));
        Assert.Equal(successfulMerge ? 2 : 1, maps.Loads);
    }

    [Fact]
    public async Task FailedMergeDoesNotReportAChangedClose()
    {
        var failure = new ThrowingMapSceneReader(); await using var fixture = await Fixture.CreateAsync(failure);
        await SeedBlankMapScene(fixture, true);
        using var context = new TestContext(); context.Services.AddSingleton(new MapLinkService(fixture));
        var closed = new List<bool>();
        var editor = context.RenderComponent<MapLinkEditor>(p => p.Add(x => x.OnClosed,
            EventCallback.Factory.Create<bool>(this, changed => closed.Add(changed))));
        editor.WaitForAssertion(() => Assert.Single(editor.FindAll(".map-link-modal")));
        failure.Arm();
        await Assert.ThrowsAnyAsync<Exception>(() => editor.FindAll("button").Single(x => x.TextContent.Trim() == "merge map scenes into rooms").ClickAsync(new()));
        Assert.Empty(closed);
    }

    private static async Task SeedBlankMapScene(Fixture fixture, bool matchingRoom)
    {
        await using var db = fixture.CreateDbContext();
        var room = new Room { Id = Guid.NewGuid(), FriendlyName = "room", ReferenceId = "room", InGameId = matchingRoom ? "merge-scene" : null, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        var map = new Map { Id = Guid.NewGuid(), InGameId = "map", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 };
        var zone = new MapZone { Id = Guid.NewGuid(), Map = map, InGameId = "zone", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 };
        db.AddRange(room, map, zone, new MapScene { Id = Guid.NewGuid(), MapZone = zone, InGameId = "merge-scene" });
        await db.SaveChangesAsync();
    }

    private sealed class CountingLandingLoader : IAreaMapLoader
    {
        private readonly LandingAreaMapView view = new(LandingAreaMapState.Ready, Surface());
        public int Loads { get; private set; }
        public Task<LandingAreaMapView> LoadLandingAsync(CancellationToken cancellationToken = default) { Loads++; return Task.FromResult(view); }
        public Task<AreaMapSurfaceView?> LoadRoomAsync(Guid roomId, GlobalAreaMapGeometryView? retainedGeometry, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private static AreaMapSurfaceView Surface()
        {
            var map = Guid.NewGuid();
            return new(new(map, new(0, 0, 10, 10), "geometry", [], 0), new(new Dictionary<Guid, AreaMapOwnerDecoration>(), new Dictionary<Guid, AreaMapOwnerDecoration>(), null), new("map-wireframe", map.ToString(), new(0, 0, 10, 10), null, false));
        }
    }
    private sealed class ThrowingMapSceneReader : DbCommandInterceptor
    {
        private bool armed;
        public void Arm() => armed = true;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (armed && command.CommandText.Contains("MapScenes", StringComparison.Ordinal)) { armed = false; throw new InvalidOperationException("held merge failure"); }
            return ValueTask.FromResult(result);
        }
    }
}
