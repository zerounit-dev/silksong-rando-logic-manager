using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests;

public sealed class MapManifestReviewViewportTests
{
    [Fact]
    public void FromBounds_PadsNegativeAndPositiveCoordinates_AndReflectsAroundPaddedMidpoint()
    {
        var viewport = MapManifestReviewViewport.FromBounds([new(-10, -4, 20, 6), new(-3, -2, 5, 30)]);

        Assert.Equal(-10.9, viewport.MinX, 8);
        Assert.Equal(-5.02, viewport.MinY, 8);
        Assert.Equal(20.9, viewport.MaxX, 8);
        Assert.Equal(31.02, viewport.MaxY, 8);
        Assert.Equal("translate(0 26) scale(1 -1)", viewport.MapUnitToSvgTransform);
    }

    [Fact]
    public void ReviewSvg_SummaryAndFocusedShapesUseTheirPaddedViewportReflectionWithoutChangingRectCoordinates()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"silksong-map-review-viewport-{Guid.NewGuid():N}.db");
        TestContext? context = null;
        try
        {
            context = new TestContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var factory = new Factory(databasePath);
            using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
            context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(factory);
            context.Services.AddSingleton(new MapManifestParser());
            context.Services.AddSingleton(new MapManifestService(factory));
            context.Services.AddSingleton(new MapOverlayService(factory));
            context.Services.AddSingleton(new MapRenderProjectionService());
            context.Services.AddSingleton(new MapOverlayAssetCatalog());
            context.Services.AddSingleton(new MapOverlayPlacementService());
            context.Services.AddScoped<AppliedRoomStatusService>(_ => new(fixture));
            context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader());
            context.Services.AddSingleton(new DiagnosticState());

            var rendered = context.Render(builder =>
            {
                builder.OpenComponent<MapLanding>(0);
                builder.CloseComponent();
            });
            var landing = rendered.FindComponent<MapLanding>();
            var zone = Row(MapReconciliationEntity.Zone, "Zone", new(-10, -4, -2, 6));
            var chunk = Row(MapReconciliationEntity.Chunk, "Zone", new(-10, -4, -2, 6));
            var plan = new MapReconciliationPlan { Manifest = new("Map", [], [], 0), Rows = [zone, chunk] };
            var review = new MapManifestReviewState(plan);
            SetPrivateField(landing.Instance, "reconciliationPlan", plan);
            SetPrivateField(landing.Instance, "reviewState", review);
            landing.Render();

            var summary = landing.Find("svg[aria-label='Read-only zone change summary']");
            Assert.Equal("translate(0 2) scale(1 -1)", summary.QuerySelector("g")!.GetAttribute("transform"));
            Assert.Equal("-4", summary.QuerySelector("rect")!.GetAttribute("y"));

            var navigation = GetPrivateField<MapManifestReviewNavigationState>(landing.Instance, "reviewNavigation");
            Assert.True(navigation.OpenZone("Zone"));
            review.FocusZone("Zone");
            landing.Render();

            var focused = landing.Find("svg[aria-label='Read-only zone review preview']");
            Assert.Equal("translate(0 2) scale(1 -1)", focused.QuerySelector("g")!.GetAttribute("transform"));
            Assert.Equal("-4", focused.QuerySelector("rect")!.GetAttribute("y"));
        }
        finally
        {
            context?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static MapReconciliationPlanRow Row(MapReconciliationEntity entity, string zone, MapUnitBounds bounds) => new()
    {
        Kind = MapReconciliationKind.Unchanged,
        Entity = entity,
        Label = entity.ToString(),
        ZoneInGameId = zone,
        CurrentBounds = bounds
    };

    private static T GetPrivateField<T>(object instance, string name) => (T)instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void SetPrivateField(object instance, string name, object value) => instance.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(instance, value);

    private sealed class Factory(string databasePath) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={databasePath}").Options);
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
