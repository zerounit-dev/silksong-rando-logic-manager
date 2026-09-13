using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;
using Fixture = MapCutover.Tests.TypedMapAndStatusTests.Fixture;
using System.Reflection;

namespace MapCutover.Tests;

public sealed class RoomEditorMapCallbackChainTests
{
    [Fact]
    public async Task RenderedLinkedPathUsesPageApplicationNavigationAdmission()
    {
        await using var fixture = await Fixture.CreateAsync(); var source = Room("source"); var destination = Room("destination");
        await SeedMapScene(fixture, source, destination);
        using var context = Context(fixture, source.Id, destination.Id, out var maps, out var logic);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("path.linked-owner").Count));
        await LinkedPath(page, "source").ClickAsync(new());
        Assert.False(await page.Instance.RequestApplicationRoomNavigationAsync(source.Id));
        Assert.Empty(context.Services.GetRequiredService<FakeNavigationManager>().History);
        await LinkedPath(page, "destination").ClickAsync(new());
        await LinkedPath(page, "destination").ClickAsync(new());
        Assert.EndsWith($"/rooms/{destination.Id:D}", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Single(context.Services.GetRequiredService<FakeNavigationManager>().History);
        Assert.Equal(1, maps.RoomLoads); Assert.Equal(1, logic.Loads);
    }

    [Theory]
    [InlineData("pending-draft")]
    [InlineData("modal")]
    [InlineData("command")]
    [InlineData("loading")]
    [InlineData("navigation")]
    [InlineData("stale")]
    [InlineData("disposed")]
    public async Task RenderedLinkedPathRejectsEveryApplicationNavigationBlockedState(string state)
    {
        await using var fixture = await Fixture.CreateAsync(); var source = Room("source"); var destination = Room("destination");
        await SeedMapScene(fixture, source, destination);
        using var context = Context(fixture, source.Id, destination.Id, out _, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("path.linked-owner").Count));
        var path = LinkedPath(page, "destination"); SemaphoreSlim? heldGate = null;
        switch (state)
        {
            case "pending-draft": SetField(page.Instance, "meaningfulPendingDraft", true); break;
            case "modal": SetField(page.Instance, "modalState", new V2ModalRuntimeState(new V2PermanentDeleteDialogView("room", "source"), "test", null, V2ModalRuntimeStage.ProposalOpen)); break;
            case "command": heldGate = (SemaphoreSlim)GetField(page.Instance, "commandGate")!; await heldGate.WaitAsync(); break;
            case "loading": SetField(GetField(page.Instance, "refresh")!, "activeLoads", 1); break;
            case "navigation": SetField(page.Instance, "applicationNavigationActive", true); break;
            case "stale": SetField(page.Instance, "displayedRoomId", Guid.NewGuid()); break;
            case "disposed": await page.Instance.DisposeAsync(); break;
        }
        try { await path.ClickAsync(new()); }
        finally { heldGate?.Release(); }
        Assert.Empty(context.Services.GetRequiredService<FakeNavigationManager>().History);
    }

    [Fact]
    public async Task RenderedMapLinkApplyPerformsOneGuardedTypedReloadAndOneCaptureContextRefresh()
    {
        await using var fixture = await Fixture.CreateAsync(); var source = Room("source"); var destination = Room("destination");
        await SeedMapScene(fixture, source, destination);
        using var context = Context(fixture, source.Id, destination.Id, out var maps, out var logic);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".room-map-context-map-actions")));
        var mapBefore = maps.RoomLoads; var logicBefore = logic.Loads; var captureBefore = logic.CaptureContextLoads;
        page.FindAll(".room-map-context-map-actions button").Single(x => x.TextContent.Trim() == "edit map links").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".map-link-modal")));
        page.FindAll(".map-link-modal button").Single(x => x.TextContent.Trim() == "apply").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".map-link-modal")));
        Assert.Equal(mapBefore + 1, maps.RoomLoads);
        Assert.Equal(logicBefore + 1, logic.Loads);
        Assert.Equal(captureBefore + 1, logic.CaptureContextLoads);
    }

    [Theory]
    [InlineData("map")]
    [InlineData("inverse")]
    public async Task ActualRoomMapLinkModalBlocksBothNavigationSourcesAndClearsOnClose(string admittedAfterClose)
    {
        await using var fixture = await Fixture.CreateAsync(); var source = Room("source"); var destination = Room("destination");
        await SeedMapScene(fixture, source, destination);
        using var context = Context(fixture, source.Id, destination.Id, out _, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("path.linked-owner").Count));
        page.FindAll(".room-map-context-map-actions button").Single(x => x.TextContent.Trim() == "edit map links").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".map-link-modal")));
        await LinkedPath(page, "destination").ClickAsync(new());
        Assert.False(await page.Instance.RequestApplicationRoomNavigationAsync(destination.Id));
        Assert.Empty(context.Services.GetRequiredService<FakeNavigationManager>().History);
        page.FindAll(".map-link-modal button").Single(x => x.TextContent.Trim() == "cancel").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".map-link-modal")));
        if (admittedAfterClose == "map") await LinkedPath(page, "destination").ClickAsync(new());
        else Assert.True(await page.Instance.RequestApplicationRoomNavigationAsync(destination.Id));
        Assert.Single(context.Services.GetRequiredService<FakeNavigationManager>().History);
    }

    [Fact]
    public async Task ActualRoomMapLinkApplyingStateBlocksMapAndInverseUntilApplyCloses()
    {
        var hold = new HoldingMapSceneReader(); await using var fixture = await Fixture.CreateAsync(hold);
        var source = Room("source"); var destination = Room("destination"); await SeedMapScene(fixture, source, destination);
        using var context = Context(fixture, source.Id, destination.Id, out _, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("path.linked-owner").Count));
        page.FindAll(".room-map-context-map-actions button").Single(x => x.TextContent.Trim() == "edit map links").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".map-link-modal")));
        hold.Arm(); var apply = page.FindAll(".map-link-modal button").Single(x => x.TextContent.Trim() == "apply").ClickAsync(new());
        await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await LinkedPath(page, "destination").ClickAsync(new());
        Assert.False(await page.Instance.RequestApplicationRoomNavigationAsync(destination.Id));
        Assert.Empty(context.Services.GetRequiredService<FakeNavigationManager>().History);
        hold.Release(); await apply;
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".map-link-modal")));
        Assert.True(await page.Instance.RequestApplicationRoomNavigationAsync(destination.Id));
        Assert.Single(context.Services.GetRequiredService<FakeNavigationManager>().History);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RoomMapSuccessfulMergeThenCancelReloadsOnceWhileNoOpDoesNot(bool successfulMerge)
    {
        await using var fixture = await Fixture.CreateAsync(); var source = Room("source"); var destination = Room("destination");
        source.InGameId = successfulMerge ? "merge-scene" : null;
        await SeedBlankMapScene(fixture, source, destination);
        using var context = Context(fixture, source.Id, destination.Id, out var maps, out var logic);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".room-map-context-map-actions")));
        var mapBefore = maps.RoomLoads; var logicBefore = logic.Loads; var captureBefore = logic.CaptureContextLoads;
        page.FindAll(".room-map-context-map-actions button").Single(x => x.TextContent.Trim() == "edit map links").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".map-link-modal")));
        page.FindAll(".map-link-modal button").Single(x => x.TextContent.Trim() == "merge map scenes into rooms").Click();
        page.WaitForAssertion(() => Assert.Equal(successfulMerge ? "merged 1 map scene" : "merged 0 map scenes", page.Find(".map-link-merge-result").TextContent.Trim()));
        page.FindAll(".map-link-modal button").Single(x => x.TextContent.Trim() == "cancel").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".map-link-modal")));
        Assert.Equal(mapBefore + (successfulMerge ? 1 : 0), maps.RoomLoads);
        Assert.Equal(logicBefore + (successfulMerge ? 1 : 0), logic.Loads);
        Assert.Equal(captureBefore + (successfulMerge ? 1 : 0), logic.CaptureContextLoads);
    }

    [Fact]
    public async Task RenderedPageNavigationFromAToBRetainsGlobalGeometryAndLoadsDestinationContextOnly()
    {
        await using var fixture = await Fixture.CreateAsync(); var source = Room("source"); var destination = Room("destination");
        await SeedMapScene(fixture, source, destination);
        using var context = Context(fixture, source.Id, destination.Id, out var maps, out _);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Id));
        page.WaitForAssertion(() => Assert.Equal(1, maps.RoomLoads));
        page.SetParametersAndRender(p => p.Add(x => x.RoomId, destination.Id));
        page.WaitForAssertion(() => Assert.Equal(2, maps.RoomLoads));
        Assert.Equal(1, maps.GlobalGeometryLoads);
        Assert.Equal(destination.Id, maps.LastRetainedGeometryOwner);
        Assert.Contains($"room-map-{destination.Id:N}", page.Markup, StringComparison.Ordinal);
    }

    private static TestContext Context(Fixture fixture, Guid source, Guid destination, out CallbackMapLoader maps, out CallbackLogicLoader logic)
    {
        var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("loadRoomMapContextVisibility").SetResult(true);
        context.Services.AddLogging(); context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        maps = new(source, destination); logic = new();
        context.Services.AddSingleton<IAreaMapLoader>(maps); context.Services.AddSingleton<IRoomEditorV2LogicLoader>(logic);
        var validation = new RequirementValidationService(fixture, Microsoft.Extensions.Logging.Abstractions.NullLogger<RequirementValidationService>.Instance);
        var catalog = new LogicCatalogService(fixture, validation);
        context.Services.AddSingleton<IRequirementValidationService>(validation);
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RoomEditorV2CommandService(catalog, validation, fixture));
        context.Services.AddSingleton(new RequirementCatalogueService(fixture, new RequirementCatalogueWriteCoordinator(fixture), Microsoft.Extensions.Logging.Abstractions.NullLogger<RequirementCatalogueService>.Instance));
        context.Services.AddSingleton(new WorkspaceChangeNotifier()); context.Services.AddSingleton(new DiagnosticState()); context.Services.AddSingleton(new MapLinkService(fixture));
        return context;
    }

    private static async Task SeedMapScene(Fixture fixture, Room source, Room destination)
    {
        await using var db = fixture.CreateDbContext();
        var map = new Map { Id = Guid.NewGuid(), InGameId = "map", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 };
        var zone = new MapZone { Id = Guid.NewGuid(), Map = map, InGameId = "zone", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 };
        db.AddRange(source, destination, map, zone, new MapScene { Id = Guid.NewGuid(), MapZone = zone, InGameId = "scene", RoomReferenceText = source.ReferenceId, ResolvedRoom = source });
        await db.SaveChangesAsync();
    }
    private static async Task SeedBlankMapScene(Fixture fixture, Room source, Room destination)
    {
        await using var db = fixture.CreateDbContext();
        var map = new Map { Id = Guid.NewGuid(), InGameId = "map", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 };
        var zone = new MapZone { Id = Guid.NewGuid(), Map = map, InGameId = "zone", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 10, MapUnitMaxY = 10 };
        db.AddRange(source, destination, map, zone, new MapScene { Id = Guid.NewGuid(), MapZone = zone, InGameId = "merge-scene", RoomReferenceText = null });
        await db.SaveChangesAsync();
    }

    private static Room Room(string name) => new() { Id = Guid.NewGuid(), FriendlyName = name, ReferenceId = name, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
    private static AngleSharp.Dom.IElement LinkedPath(IRenderedComponent<RoomEditorV2Page> page, string label) => page.FindAll("path.linked-owner").Single(x => x.GetAttribute("aria-label") == label);
    private static object? GetField(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void SetField(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static RoomEditorV2View LogicView(Guid room) => new(
        new(room, "source", "source", null, null, null, null, null, false, false, false, false, DateTime.UtcNow, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral),
        new([], []), new([], [], [], new Dictionary<Guid, IReadOnlyList<string>>()), new([], [], []), new([], [], [], CheckLocationTypeCatalogue.Definitions));

    private sealed class CallbackLogicLoader : IRoomEditorV2LogicLoader, IRoomEditorV2SceneBatchLoader
    {
        public int Loads { get; private set; } public int CaptureContextLoads { get; private set; }
        public Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken) { Loads++; return Task.FromResult<RoomEditorV2View?>(LogicView(roomId)); }
        public Task<RoomEditorV2View?> LoadAsync(Guid roomId, bool includeScene, CancellationToken cancellationToken) => LoadAsync(roomId, includeScene, includeScene, cancellationToken);
        public Task<RoomEditorV2View?> LoadAsync(Guid roomId, bool includeScene, bool includeCaptureContext, CancellationToken cancellationToken)
        {
            Loads++; if (includeCaptureContext) CaptureContextLoads++; return Task.FromResult<RoomEditorV2View?>(LogicView(roomId));
        }
    }
    private sealed class CallbackMapLoader(Guid source, Guid destination) : IAreaMapLoader
    {
        private readonly GlobalAreaMapGeometryView geometry = new(Guid.NewGuid(), new(0, 0, 10, 10), "geometry", [new(source, null, "M 0 0 L 1 0 L 1 1 L 0 1 Z", new(0, 0, 1, 1)), new(destination, null, "M 2 0 L 3 0 L 3 1 L 2 1 Z", new(2, 0, 3, 1))], 2);
        public int RoomLoads { get; private set; }
        public int GlobalGeometryLoads { get; private set; }
        public Guid? LastRetainedGeometryOwner { get; private set; }
        public Task<LandingAreaMapView> LoadLandingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AreaMapSurfaceView?> LoadRoomAsync(Guid roomId, GlobalAreaMapGeometryView? retainedGeometry, CancellationToken cancellationToken = default)
        {
            RoomLoads++; if (retainedGeometry is null) GlobalGeometryLoads++; else LastRetainedGeometryOwner = roomId; var effective = retainedGeometry ?? geometry;
            AreaMapSurfaceView view = new(effective, new(new Dictionary<Guid, AreaMapOwnerDecoration> { [source] = new(source, null, "source", AppliedRoomStatus.Success), [destination] = new(destination, null, "destination", AppliedRoomStatus.Success) }, new Dictionary<Guid, AreaMapOwnerDecoration>(), null), new($"room-map-{roomId:N}", $"room-context-{roomId:N}", new(0, 0, 10, 10), roomId, true));
            return Task.FromResult<AreaMapSurfaceView?>(view);
        }
    }
    private sealed class HoldingMapSceneReader : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<object?> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool armed;
        public TaskCompletionSource<object?> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => armed = true;
        public void Release() => release.TrySetResult(null);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (armed && command.CommandText.Contains("MapScenes", StringComparison.Ordinal))
            {
                armed = false; Started.TrySetResult(null); await release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
