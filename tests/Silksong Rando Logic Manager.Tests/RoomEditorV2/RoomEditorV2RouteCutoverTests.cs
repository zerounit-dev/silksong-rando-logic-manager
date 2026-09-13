using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components;
using Silksong_Rando_Logic_Manager.Components.Pages;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using System.Reflection;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

public sealed class RoomEditorV2RouteCutoverTests
{
    [Fact]
    public void Router_ResolvesLegacyRoomRouteAndLeavesTemporaryRouteUnmatched()
    {
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var roomId = Guid.NewGuid();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new RouteLoader(roomId));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RouteCommands());
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/rooms/{roomId:D}");

        var rendered = context.RenderComponent<Router>(parameters => parameters
            .Add(x => x.AppAssembly, typeof(RoomEditorV2Page).Assembly)
            .Add(x => x.Found, RouteView)
            .Add(x => x.NotFoundPage, typeof(TemporaryRouteUnavailable)));

        Assert.Single(rendered.FindAll(".room-editor-v2"));
        Assert.Single(rendered.FindComponents<RoomEditorV2Page>());
        Assert.Equal("Room", rendered.Find(".room-title").GetAttribute("value"));

        navigation.NavigateTo($"/rooms-v2/{roomId:D}");
        rendered.WaitForAssertion(() =>
        {
            Assert.Empty(rendered.FindAll(".room-editor-v2"));
            Assert.Empty(rendered.FindComponents<RoomEditorV2Page>());
            Assert.Equal("temporary route unavailable", rendered.Find("[data-test-not-found]").TextContent);
        });
    }

    [Fact]
    public async Task LandingMap_LinkedRoomActivationNavigatesToSoleRoomRoute()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var roomId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            var map = new Map { InGameId = "map", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 };
            var zone = new MapZone { Map = map, InGameId = "zone", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 };
            var room = new Room { Id = roomId, FriendlyName = "Room", ReferenceId = "room" };
            var scene = new MapScene { MapZone = zone, InGameId = "room", RoomReferenceText = "room", ResolvedRoomId = roomId };
            db.AddRange(map, zone, room, scene, new MapChunk { MapScene = scene, CacheIndex = 0, MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 });
            await db.SaveChangesAsync();
        }

        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<MapManifestParser>();
        context.Services.AddSingleton<MapManifestService>();
        context.Services.AddSingleton<MapOverlayService>();
        context.Services.AddSingleton<MapRenderProjectionService>();
        context.Services.AddSingleton<MapOverlayAssetCatalog>();
        context.Services.AddSingleton<MapOverlayPlacementService>();
        context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader());
        context.Services.AddSingleton<DiagnosticState>();
        var landing = context.RenderComponent<MapLanding>();

        landing.Find("path.linked-owner").Click();

        Assert.EndsWith($"/rooms/{roomId:D}", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SceneReview_SuccessfulImportNavigatesToSoleRoomRoute()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<SceneDumpParser>();
        context.Services.AddSingleton<SceneClassificationService>();
        context.Services.AddSingleton<SceneImportService>();
        context.Services.AddSingleton<TransitionInverseSetupState>();
        context.Services.AddSingleton<SceneReviewProjectionService>();
        context.Services.AddSingleton<WorkspaceChangeNotifier>();
        context.Services.AddSingleton<DiagnosticState>();
        var review = context.RenderComponent<SceneReview>();
        typeof(SceneReview).GetField("review", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(review.Instance, new SceneDumpReview("scene", [], []));

        await review.InvokeAsync(async () => await (Task)typeof(SceneReview).GetMethod("ImportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(review.Instance, null)!);

        await using var db = fixture.CreateDbContext();
        var roomId = (await db.Rooms.SingleAsync(room => room.InGameId == "scene")).Id;
        Assert.EndsWith($"/rooms/{roomId:D}", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    private static RenderFragment<RouteData> RouteView => routeData => builder =>
    {
        builder.OpenComponent<Microsoft.AspNetCore.Components.RouteView>(0);
        builder.AddAttribute(1, "RouteData", routeData);
        builder.CloseComponent();
    };

    [Route("/temporary-route-unavailable")]
    private sealed class TemporaryRouteUnavailable : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "data-test-not-found", string.Empty);
            builder.AddContent(2, "temporary route unavailable");
            builder.CloseElement();
        }
    }

    private sealed class RouteLoader(Guid roomId) : IRoomEditorV2LogicLoader
    {
        public Task<RoomEditorV2View?> LoadAsync(Guid requestedRoomId, CancellationToken cancellationToken) =>
            Task.FromResult<RoomEditorV2View?>(requestedRoomId == roomId
                ? new RoomEditorV2View(
                    new RoomHeaderView(roomId, "Room", "room", null, null, null, null, null, false, false, false, DateTime.UtcNow, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral),
                    new([], []), new([], [], [], new Dictionary<Guid, IReadOnlyList<string>>()), new([], [], []), new([], [], []))
                : null);
    }

    private sealed class RouteCommands : IRoomEditorV2CommandService
    {
        public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
    }
}
