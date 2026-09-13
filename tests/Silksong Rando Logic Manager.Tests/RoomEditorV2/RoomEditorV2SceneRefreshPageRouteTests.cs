using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>
/// Migration-current SQLite route traces.  These intentionally enter the page
/// through its public callbacks or a rendered table's JS-invokable boundary;
/// they never supply a refresh impact to the coordinator.
/// </summary>
public sealed class RoomEditorV2SceneRefreshPageRouteTests
{
    [Theory]
    [MemberData(nameof(RelevantRoutes))]
    public async Task RelevantActualPageRoute_LoadsSceneExactlyOnceAndNeverLoadsMap(string route)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var initiallyArchived = route is "subroom-restore" or "subroom-delete" or "transition-restore" or "transition-delete" or "check-restore" or "check-delete" or "connection-restore" or "connection-delete" or "room-restore";
        var seed = await SeedAsync(fixture, initiallyArchived);
        using var context = PageContext(fixture, mapContextVisible: true);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        if (!initiallyArchived) page.WaitForAssertion(() => Assert.Single(page.FindAll(".room-map-context")));
        var before = Trace(page).SceneLoaderInvocations;
        var mapBefore = MapTrace(context);
        var traceBefore = Trace(page).Events.Count;

        await InvokeRelevantRoute(page, seed, route);

        page.WaitForAssertion(() => Assert.Equal(before + 1, Trace(page).SceneLoaderInvocations));
        Assert.Equal(0, Trace(page).MapLoaderInvocations);
        // Restoring an archived room mounts and loads the typed map view.
        if (route != "room-restore") Assert.Equal(mapBefore, MapTrace(context));
        Assert.Equal(traceBefore + 3, Trace(page).Events.Count);
        if (route == "transition-name")
            Assert.Equal("Link this room to exactly one map before capturing a scene image.", page.Find("[data-scene-status-text='true']").TextContent);
        await AssertRouteOutcomeAsync(fixture, seed, route);
    }

    [Theory]
    [MemberData(nameof(IrrelevantRoutes))]
    public async Task MatrixIrrelevantActualPageRoute_LoadsNeitherSceneNorMap(string route)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        if (route is "transition-inverse-update" or "transition-inverse-do-not-update") await SeedInverseTargetAsync(fixture, seed);
        var scene = new RenderedSceneLoader();
        using var context = PageContext(fixture, mapContextVisible: true,
            loader: new SceneSourceLoader(new RoomEditorV2LogicLoader(fixture), scene));
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".room-map-context")));
        page.WaitForAssertion(() => Assert.Equal("/data/scenes/" + seed.Room.Id.ToString("D") + ".webp?v=73", page.Find("[data-scene-layout-image='true']").GetAttribute("href")));
        var before = Trace(page).SceneLoaderInvocations;
        var mapBefore = MapTrace(context);
        var sceneLoadsBefore = scene.Count;
        var traceBefore = Trace(page).Events.Count;

        await InvokeIrrelevantRoute(page, seed, route);

        page.WaitForAssertion(() => Assert.Equal(before, Trace(page).SceneLoaderInvocations));
        Assert.Equal(0, Trace(page).MapLoaderInvocations);
        Assert.Equal(mapBefore, MapTrace(context));
        Assert.Equal(sceneLoadsBefore, scene.Count);
        Assert.Equal("/data/scenes/" + seed.Room.Id.ToString("D") + ".webp?v=73", page.Find("[data-scene-layout-image='true']").GetAttribute("href"));
        Assert.Single(page.FindAll("[data-scene-layout-frame='true']"));
        Assert.Equal(traceBefore + (route is "transition-inverse-update" or "transition-inverse-do-not-update" ? 4 : 2), Trace(page).Events.Count);
        await AssertRouteOutcomeAsync(fixture, seed, route);
    }

    public static IEnumerable<object[]> RelevantRoutes()
    {
        foreach (var route in new[]
        {
            "subroom-create", "subroom-archive", "subroom-restore", "subroom-delete", "subroom-name", "subroom-reference-update", "subroom-reference-do-not-update",
            "transition-create", "transition-archive", "transition-restore", "transition-delete", "transition-alias", "transition-name", "transition-metadata-x", "transition-metadata-y", "transition-metadata-override",
            "check-create", "check-archive", "check-restore", "check-delete", "check-name", "check-metadata-x", "check-metadata-y", "check-metadata-override",
            "connection-create", "connection-archive", "connection-restore", "connection-delete", "connection-alias", "connection-name", "connection-source", "connection-destination",
            "room-archive", "room-restore", "room-scene-dimensions"
        }) yield return [route];
    }

    public static IEnumerable<object[]> IrrelevantRoutes()
    {
        foreach (var route in new[]
        {
            "subroom-reorder", "subroom-notes", "transition-reorder", "transition-notes",
            "transition-inverse-update", "transition-inverse-do-not-update", "transition-source", "transition-destination", "transition-requirements", "transition-todo", "transition-verification", "transition-metadata-non-xy",
            "check-reorder", "check-notes", "check-subroom", "check-requirements", "check-apworld", "check-todo", "check-verification", "check-metadata-non-xy",
            "connection-reorder", "connection-notes", "connection-requirements", "connection-todo", "connection-verification"
        }) yield return [route];
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task RoomReferenceProposal_UsesCommittedMapResolutionDelta(bool updateReferences, int expectedSceneDelta)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        await AddCapturedMapLinkAsync(fixture, seed.Room);
        using var context = PageContext(fixture, mapContextVisible: true);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".room-map-context")));
        var before = Trace(page).SceneLoaderInvocations;
        var mapBefore = MapTrace(context);
        var traceBefore = Trace(page).Events.Count;

        var reference = page.Find("textarea[aria-label='Room reference ID']");
        reference.Input("renamed");
        await reference.TriggerEventAsync("onblur", new Microsoft.AspNetCore.Components.Web.FocusEventArgs());
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == (updateReferences ? "update references" : "do not update references")).Click();

        page.WaitForAssertion(() => Assert.Equal(before + expectedSceneDelta, Trace(page).SceneLoaderInvocations));
        Assert.Equal(0, Trace(page).MapLoaderInvocations);
        Assert.Equal(mapBefore + 1, MapTrace(context));
        Assert.Equal(traceBefore + 2 + expectedSceneDelta, Trace(page).Events.Count);
        await using var verify = fixture.CreateDbContext();
        var durableRoom = await verify.Rooms.SingleAsync(x => x.Id == seed.Room.Id);
        var durableScene = await verify.MapScenes.SingleAsync();
        Assert.Equal("renamed", durableRoom.ReferenceId);
        Assert.Equal(updateReferences ? "renamed" : "room", durableScene.RoomReferenceText);
        Assert.Equal(updateReferences ? seed.Room.Id : null, durableScene.ResolvedRoomId);
    }

    [Fact]
    public async Task SuccessfulHostedMapLinkEditorCallback_ReplacesOldUnavailableCaptureContextAfterMapEligibilityChanges()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 10, SceneUnitHeight = 10 };
        var map = new Map { InGameId = "map", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 20, MapUnitMaxY = 20 };
        var zone = new MapZone { Map = map, InGameId = "zone", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 20, MapUnitMaxY = 20 };
        var scene = new MapScene { MapZone = zone, InGameId = "room-scene" };
        await using (var db = fixture.CreateDbContext())
        {
            db.AddRange(room, map, zone, scene,
                new MapChunk { MapScene = scene, CacheIndex = 0, MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 20, MapUnitMaxY = 20 });
            await db.SaveChangesAsync();
        }

        using var context = PageContext(fixture, mapContextVisible: true);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        const string unavailable = "Link this room to exactly one map before capturing a scene image.";
        page.WaitForAssertion(() =>
        {
            Assert.Equal(unavailable, page.Find("[data-scene-status-text='true']").TextContent);
            Assert.True(page.Find("[data-scene-capture='true']").HasAttribute("disabled"));
        });
        page.FindAll(".room-map-context-map-actions button").Single(button => button.TextContent.Trim() == "edit map links").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".map-link-modal")));
        page.Find(".map-link-table input[list='map-link-room-references']").Change("room");
        page.FindAll(".map-link-modal button").Single(button => button.TextContent.Trim() == "apply").Click();
        await using (var saved = fixture.CreateDbContext())
        {
            var savedMapScene = await saved.MapScenes.SingleAsync(candidate => candidate.Id == scene.Id);
            Assert.Equal(room.ReferenceId, savedMapScene.RoomReferenceText);
            Assert.Equal(room.Id, savedMapScene.ResolvedRoomId);
        }
        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll(".map-link-modal"));
            Assert.Equal("The linked map needs a usable area overlay before capturing a scene image.", page.Find("[data-scene-status-text='true']").TextContent);
            Assert.Empty(page.FindAll("[data-scene-capture-map-links='true']"));
            Assert.True(page.Find("[data-scene-capture='true']").HasAttribute("disabled"));
        });
        await using var verify = fixture.CreateDbContext();
        var updatedMapScene = await verify.MapScenes.SingleAsync(candidate => candidate.Id == scene.Id);
        Assert.Equal(room.ReferenceId, updatedMapScene.RoomReferenceText);
        Assert.Equal(room.Id, updatedMapScene.ResolvedRoomId);
    }

    [Fact]
    public async Task ArchivedRoomPermanentDelete_UsesActualPageModalAndClearsTheMissingRouteWithoutInventingSceneWork()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture, archived: true);
        using var context = PageContext(fixture, mapContextVisible: true);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        var before = Trace(page).SceneLoaderInvocations;

        page.Find("#room-permanent-delete").Click();
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "permanently delete").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".room-document")));
        Assert.Equal(before, Trace(page).SceneLoaderInvocations);
        Assert.Equal(0, Trace(page).MapLoaderInvocations);
    }

    [Fact]
    public async Task SceneDimensionsModal_IsActiveOnlyAccessibleAndKeepsInvalidOrCancelledDraftsLocal()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        using var context = PageContext(fixture, mapContextVisible: true);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        var before = Trace(page).Events.Count;

        page.Find("#room-scene-dimensions").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("#v2-page-modal-dialog")));
        var dialog = page.Find("#v2-page-modal-dialog");
        Assert.Equal("dialog", dialog.GetAttribute("role")); Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        Assert.Equal("v2-page-modal-title", dialog.GetAttribute("aria-labelledby"));
        Assert.Equal(2, page.FindAll("#v2-page-modal-dialog input").Count);
        Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "focusV2ModalDialog");
        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));

        page.Find("#v2-page-modal-dialog input").Input("12");
        page.FindAll("#v2-page-modal-dialog input").ElementAt(1).Input("");
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "Apply").Click();
        Assert.Contains("Enter both dimensions", page.Find("#v2-page-modal-dialog").TextContent);
        Assert.Equal(before, Trace(page).Events.Count);

        await page.InvokeAsync(() => page.Find("#v2-page-modal-dialog").TriggerEventAsync("onkeydown", new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" }));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Equal(before, Trace(page).Events.Count);
        Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "focusEditorField" && (string?)call.Arguments[0] == "room-scene-dimensions");

        using var archivedContext = PageContext(fixture);
        var archivedRoom = new Room { Id = Guid.NewGuid(), FriendlyName = "archived", ReferenceId = "archived", IsArchived = true, ArchivedUtc = DateTime.UtcNow };
        await using (var db = fixture.CreateDbContext()) { db.Add(archivedRoom); await db.SaveChangesAsync(); }
        var archivedPage = archivedContext.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, archivedRoom.Id));
        Assert.Empty(archivedPage.FindAll("#room-scene-dimensions"));
    }

    [Fact]
    public async Task BackToMap_UsesThePageLandingRouteIntent()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        using var context = PageContext(fixture);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));

        page.Find(".back-to-map-button").Click();

        Assert.EndsWith("/", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SceneDimensionsModal_ValidApplyThenBlankClearRefreshesRenderedSceneStateAndRetainsStaleTransform()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        await using (var db = fixture.CreateDbContext())
        {
            var room = await db.Rooms.SingleAsync(x => x.Id == seed.Room.Id);
            room.SceneImageScaleXPercent = 100; room.SceneImageScaleYPercent = 100;
            room.SceneImagePanXPercent = 0; room.SceneImagePanYPercent = 0;
            await db.SaveChangesAsync();
        }
        using var context = PageContext(fixture, mapContextVisible: true);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".scene-layout-room-bounds")));
        var before = Trace(page).Events.Count;

        await ApplyDimensionsAsync(page, "12", "24");
        page.WaitForAssertion(() => Assert.Equal(before + 3, Trace(page).Events.Count));
        await using (var applied = fixture.CreateDbContext())
        {
            var room = await applied.Rooms.SingleAsync(x => x.Id == seed.Room.Id);
            Assert.Equal((12d, 24d, true, 100d, 0d), (room.SceneUnitWidth, room.SceneUnitHeight, room.IsSceneImageStale, room.SceneImageScaleXPercent, room.SceneImagePanXPercent));
        }
        Assert.Single(page.FindAll(".scene-layout-room-bounds"));
        Assert.Equal("needs recapture", page.Find("[data-scene-status-text='true']").TextContent);

        await ApplyDimensionsAsync(page, "", "");
        page.WaitForAssertion(() =>
        {
            Assert.Single(page.FindAll("svg[data-scene-layout-canvas='true']"));
            Assert.Equal("scene dimensions unavailable", page.Find("[data-scene-status-text='true']").TextContent);
        });
        await using var cleared = fixture.CreateDbContext();
        var clearedRoom = await cleared.Rooms.SingleAsync(x => x.Id == seed.Room.Id);
        Assert.Equal((null, null, true, 100d, 0d), (clearedRoom.SceneUnitWidth, clearedRoom.SceneUnitHeight, clearedRoom.IsSceneImageStale, clearedRoom.SceneImageScaleXPercent, clearedRoom.SceneImagePanXPercent));
    }

    [Fact]
    public async Task SceneDimensionsModal_AllLocalRejectionsAndCancelRoutesWriteNothing_AndPendingDraftRefusesAdmission()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        using var context = PageContext(fixture, mapContextVisible: true);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));
        await using var beforeContext = fixture.CreateDbContext();
        var before = await beforeContext.Rooms.AsNoTracking().SingleAsync(x => x.Id == seed.Room.Id);
        var traceBefore = Trace(page).Events.Count;

        foreach (var invalid in new[] { ("12", ""), ("NaN", "24"), ("0", "24") })
        {
            await ApplyDimensionsAsync(page, invalid.Item1, invalid.Item2);
            Assert.NotEmpty(page.FindAll("#v2-page-modal-dialog [role='alert']"));
            Assert.Equal(traceBefore, Trace(page).Events.Count);
            page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "Cancel").Click();
            page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        }

        page.Find("#room-scene-dimensions").Click();
        page.Find(".v2-page-modal-backdrop").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "focusEditorField" && (string?)call.Arguments[0] == "room-scene-dimensions");

        typeof(RoomEditorV2Page).GetField("meaningfulPendingDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page.Instance, true);
        page.Find("#room-scene-dimensions").Click();
        Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
        await using var afterContext = fixture.CreateDbContext();
        var after = await afterContext.Rooms.AsNoTracking().SingleAsync(x => x.Id == seed.Room.Id);
        Assert.Equal((before.SceneUnitWidth, before.SceneUnitHeight, before.UpdatedUtc), (after.SceneUnitWidth, after.SceneUnitHeight, after.UpdatedUtc));
    }

    [Fact]
    public async Task RenderedSceneDimensionsApply_HeldCommittingDisablesEveryControlAndBlocksDurableWorkAndNavigation()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var seed = await SeedAsync(fixture);
        var held = new HeldSceneDimensionsCommands(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        using var context = PageContext(fixture, mapContextVisible: true, commands: held);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, seed.Room.Id));

        page.Find("#room-scene-dimensions").Click();
        page.Find("#v2-page-modal-dialog input").Input("12");
        page.FindAll("#v2-page-modal-dialog input").ElementAt(1).Input("24");
        // Enter Committing through the delivered rendered Apply event, not by
        // changing modal runtime state in the test.
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "Apply").Click();
        await held.WaitUntilEnteredAsync();
        page.WaitForAssertion(() =>
        {
            var dialog = page.Find("#v2-page-modal-dialog");
            Assert.Equal("Committing", dialog.GetAttribute("data-v2-modal-stage"));
            Assert.All(dialog.QuerySelectorAll("input,button"), control => Assert.True(control.HasAttribute("disabled")));
        });

        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));
        var competing = await page.InvokeAsync(() => page.Instance.SaveTransitionAsync(TransitionBaseline(seed.Transition), TransitionDraft(seed.Transition, notes: "blocked"), "competing"));
        Assert.Equal(V2TransitionCommandStatus.ExpectedFailure, competing.Status);
        page.Find("#v2-page-modal-dialog").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        page.Find(".v2-page-modal-backdrop").Click();
        Assert.Single(page.FindAll("#v2-page-modal-dialog"));
        Assert.Equal(1, held.Calls);

        held.Release();
        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            Assert.Contains("refresh #2", page.Markup);
            Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == "room-scene-dimensions"));
        });
        await using var verify = fixture.CreateDbContext();
        var saved = await verify.Rooms.SingleAsync(x => x.Id == seed.Room.Id);
        Assert.Equal((12d, 24d), (saved.SceneUnitWidth, saved.SceneUnitHeight));
    }

    [Theory]
    [InlineData(false, V2RoomSceneDimensionsCommandStatus.Committed)]
    [InlineData(true, V2RoomSceneDimensionsCommandStatus.Conflict)]
    public async Task HeldSceneDimensionsApply_RouteCleanupAndDisposalDoNotApplyStaleModalOrResult(bool makeSourceConflict, V2RoomSceneDimensionsCommandStatus expectedSourceOutcome)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var source = await SeedAsync(fixture);
        var destination = new Room { FriendlyName = "destination", ReferenceId = "destination", SceneUnitWidth = 30, SceneUnitHeight = 40, SortOrder = 1 };
        await using (var db = fixture.CreateDbContext()) { db.Add(destination); await db.SaveChangesAsync(); }
        var held = new HeldSceneDimensionsCommands(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        using var context = PageContext(fixture, mapContextVisible: true, commands: held);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Room.Id));

        page.Find("#room-scene-dimensions").Click(); page.Find("#v2-page-modal-dialog input").Input("12"); page.FindAll("#v2-page-modal-dialog input").ElementAt(1).Input("24");
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "Apply").Click();
        await held.WaitUntilEnteredAsync();
        if (makeSourceConflict)
        {
            // Change the source durable baseline while its real rendered Apply is
            // held so release returns the noncommitted correction outcome.
            await using var sourceConflict = fixture.CreateDbContext();
            var sourceRoom = await sourceConflict.Rooms.SingleAsync(x => x.Id == source.Room.Id);
            sourceRoom.UpdatedUtc = sourceRoom.UpdatedUtc.AddTicks(1);
            await sourceConflict.SaveChangesAsync();
        }
        page.SetParametersAndRender(p => p.Add(x => x.RoomId, destination.Id));
        page.WaitForAssertion(() => { Assert.Empty(page.FindAll("#v2-page-modal-dialog")); Assert.Contains("destination", page.Markup); Assert.Contains("refresh #2", page.Markup); });
        // Preserve an existing destination draft without admitting another
        // operation: the source completion alone must cause no destination render.
        var headerDraftField = typeof(RoomEditorV2Page).GetField("headerDraft", BindingFlags.Instance | BindingFlags.NonPublic)!;
        headerDraftField.SetValue(page.Instance, new RoomHeaderDraft("destination draft", null, null, null, "destination"));
        var focusRequestsBeforeRelease = context.JSInterop.Invocations.Count(x => x.Identifier == "focusEditorField");
        var diagnosticBeforeRelease = page.Find(".v2-refresh-diagnostic").TextContent;
        var traceBeforeRelease = Trace(page).Events.ToArray();
        var renderCountBeforeRelease = page.RenderCount;
        held.Release();
        await held.WaitUntilCompletedAsync();
        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            Assert.Contains("destination", page.Markup);
            Assert.Equal("destination draft", Assert.IsType<RoomHeaderDraft>(headerDraftField.GetValue(page.Instance)).FriendlyName);
            Assert.Equal(diagnosticBeforeRelease, page.Find(".v2-refresh-diagnostic").TextContent);
            Assert.Equal(focusRequestsBeforeRelease, context.JSInterop.Invocations.Count(x => x.Identifier == "focusEditorField"));
            Assert.Equal(traceBeforeRelease, Trace(page).Events);
            Assert.Equal(renderCountBeforeRelease, page.RenderCount);
            Assert.Null(typeof(RoomEditorV2Page).GetField("modalState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance));
            Assert.DoesNotContain("The room scene dimensions changed elsewhere.", page.Markup);
        });
        Assert.Equal(expectedSourceOutcome, held.LastSceneDimensionsOutcome?.Status);

        var disposalHeld = new HeldSceneDimensionsCommands(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        using var disposalContext = PageContext(fixture, mapContextVisible: true, commands: disposalHeld);
        var disposedPage = disposalContext.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, destination.Id));
        disposedPage.Find("#room-scene-dimensions").Click(); disposedPage.Find("#v2-page-modal-dialog input").Input("50"); disposedPage.FindAll("#v2-page-modal-dialog input").ElementAt(1).Input("60");
        disposedPage.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "Apply").Click();
        await disposalHeld.WaitUntilEnteredAsync();
        await disposedPage.InvokeAsync(() => disposedPage.Instance.DisposeAsync());
        disposalHeld.Release();
        await disposalHeld.WaitUntilCompletedAsync();
        Assert.Null(typeof(RoomEditorV2Page).GetField("modalState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(disposedPage.Instance));
        Assert.DoesNotContain(disposalContext.JSInterop.Invocations, x => x.Identifier == "focusEditorField");
        await using var verify = fixture.CreateDbContext();
        var durableSource = await verify.Rooms.SingleAsync(x => x.Id == source.Room.Id);
        var durableDestination = await verify.Rooms.SingleAsync(x => x.Id == destination.Id);
        Assert.Equal(makeSourceConflict ? (10d, 10d) : (12d, 24d), (durableSource.SceneUnitWidth, durableSource.SceneUnitHeight));
        Assert.Equal((50d, 60d), (durableDestination.SceneUnitWidth, durableDestination.SceneUnitHeight));
    }

    private static async Task ApplyDimensionsAsync(IRenderedComponent<RoomEditorV2Page> page, string width, string height)
    {
        page.Find("#room-scene-dimensions").Click();
        page.Find("#v2-page-modal-dialog input").Input(width);
        page.FindAll("#v2-page-modal-dialog input").ElementAt(1).Input(height);
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "Apply").Click();
        await Task.CompletedTask;
    }

    private static TestContext PageContext(MigratedSqliteFixture fixture, bool mapContextVisible = false, IRoomEditorV2CommandService? commands = null, IRoomEditorV2LogicLoader? loader = null)
    {
        var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("loadRoomMapContextVisibility").SetResult(mapContextVisible);
        context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true);
        context.JSInterop.SetupVoid("focusEditorField", _ => true);
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader());
        context.Services.AddSingleton<MapRenderProjectionService>();
        context.Services.AddSingleton<MapOverlayAssetCatalog>();
        context.Services.AddSingleton<MapOverlayPlacementService>();
        context.Services.AddSingleton<DiagnosticState>();
        context.Services.AddSingleton<MapLinkService>();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader ?? new RoomEditorV2LogicLoader(fixture, new SceneLayoutLoader(fixture)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(commands ?? new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        return context;
    }

    private sealed class SceneSourceLoader(IRoomEditorV2LogicLoader inner, ISceneLayoutLoader scene) : IRoomEditorV2LogicLoader, IRoomEditorV2SceneSource
    {
        ISceneLayoutLoader? IRoomEditorV2SceneSource.SceneLoader => scene;
        public Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken) => inner.LoadAsync(roomId, cancellationToken);
    }

    private sealed class RenderedSceneLoader : ISceneLayoutLoader
    {
        public int Count { get; private set; }
        public Task<SceneLayoutView?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult<SceneLayoutView?>(new(true, 10, 10,
                [new(Guid.NewGuid(), "frame", null, 1, 2, 3, 4)], [],
                new(true, false, true, 73), new(true, "ready", new(1, 1, 0, 0))));
        }
    }

    private static async Task InvokeRelevantRoute(IRenderedComponent<RoomEditorV2Page> page, Seed seed, string route)
    {
        switch (route)
        {
            // These two table-only APIs are the real JS callback boundary.  The
            // rendered row provides the persisted action identity, never a field ID.
            case "subroom-archive": await SubroomAction(page, "archive", seed.Subroom.Id); return;
            case "subroom-restore": await SubroomAction(page, "restore", seed.Subroom.Id); return;
            case "subroom-delete": await DeleteSubroom(page, seed.Subroom.Id); return;
            case "check-archive": await CheckAction(page, "archive", seed.Check.Id); return;
            case "check-restore": await CheckAction(page, "restore", seed.Check.Id); return;
            case "check-delete": await DeleteCheck(page, seed.Check.Id); return;
            case "subroom-create": await CreateSubroom(page); return;
            case "check-create": await CreateCheck(page); return;
            case "subroom-name": await SaveSubroom(page, seed.Subroom, name: "subroom renamed"); return;
            case "subroom-reference-update": await SubroomReferenceProposal(page, seed.Subroom.Id, true); return;
            case "subroom-reference-do-not-update": await SubroomReferenceProposal(page, seed.Subroom.Id, false); return;
            case "check-name": await SaveCheck(page, seed.Check, name: "check renamed"); return;
            case "check-metadata-x": await CheckMetadata(page, seed.Check.Id, "annotation scene X", "2"); return;
            case "check-metadata-y": await CheckMetadata(page, seed.Check.Id, "annotation scene Y", "3"); return;
            case "check-metadata-override": await CheckMetadata(page, seed.Check.Id, "in-game X", "4"); return;

            case "transition-create": await page.InvokeAsync(() => page.Instance.CreateTransitionAsync(NewTransition(), "test")); return;
            case "transition-archive": await page.InvokeAsync(() => page.Instance.SetTransitionArchiveAsync(seed.Transition.Id, true)); return;
            case "transition-restore": await page.InvokeAsync(() => page.Instance.SetTransitionArchiveAsync(seed.Transition.Id, false)); return;
            case "transition-delete": await DeleteTransition(page, seed.Transition.Id); return;
            case "transition-alias": await page.InvokeAsync(() => page.Instance.SaveTransitionAsync(TransitionBaseline(seed.Transition), TransitionDraft(seed.Transition, alias: "b"), "test")); return;
            case "transition-name": await page.InvokeAsync(() => page.Instance.SaveTransitionAsync(TransitionBaseline(seed.Transition), TransitionDraft(seed.Transition, name: "exit renamed"), "test")); return;
            case "transition-metadata-x": await TransitionMetadata(page, seed.Transition.Id, "annotation X", "2"); return;
            case "transition-metadata-y": await TransitionMetadata(page, seed.Transition.Id, "annotation Y", "3"); return;
            case "transition-metadata-override": await TransitionMetadata(page, seed.Transition.Id, "in-game X", "4"); return;

            case "connection-create": await page.InvokeAsync(() => page.Instance.CreateConnectionAsync(NewConnection(), "test")); return;
            case "connection-archive": await page.InvokeAsync(() => page.Instance.SetConnectionArchiveAsync(seed.Connection.Id, true)); return;
            case "connection-restore": await page.InvokeAsync(() => page.Instance.SetConnectionArchiveAsync(seed.Connection.Id, false)); return;
            case "connection-delete": await DeleteConnection(page, seed.Connection.Id); return;
            case "connection-alias": await page.InvokeAsync(() => page.Instance.SaveConnectionAsync(ConnectionBaseline(seed.Connection), ConnectionDraft(seed.Connection, alias: "b"), "test")); return;
            case "connection-name": await page.InvokeAsync(() => page.Instance.SaveConnectionAsync(ConnectionBaseline(seed.Connection), ConnectionDraft(seed.Connection, name: "connection renamed"), "test")); return;
            case "connection-source": await page.InvokeAsync(() => page.Instance.SaveConnectionAsync(ConnectionBaseline(seed.Connection), ConnectionDraft(seed.Connection, source: "two"), "test")); return;
            case "connection-destination": await page.InvokeAsync(() => page.Instance.SaveConnectionAsync(ConnectionBaseline(seed.Connection), ConnectionDraft(seed.Connection, destination: "one"), "test")); return;

            case "room-archive": page.Find("#room-archive").Click(); return;
            case "room-restore": page.Find("#room-restore").Click(); return;
            case "room-scene-dimensions":
                page.Find("#room-scene-dimensions").Click();
                page.Find("#v2-page-modal-dialog input").Input("12");
                page.FindAll("#v2-page-modal-dialog input").ElementAt(1).Input("24");
                page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "Apply").Click();
                return;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

    private static async Task InvokeIrrelevantRoute(IRenderedComponent<RoomEditorV2Page> page, Seed seed, string route)
    {
        switch (route)
        {
            case "subroom-reorder": await page.InvokeAsync(() => page.FindComponent<SubroomTablePresentation>().Instance.DropSubroomAsync(seed.Subroom.Id.ToString(), "active", 0)); return;
            case "subroom-notes": await SaveSubroom(page, seed.Subroom, notes: "notes"); return;
            case "transition-reorder": await page.InvokeAsync(() => page.Instance.ReorderTransitionAsync(seed.Transition.Id, 0)); return;
            case "transition-notes": await page.InvokeAsync(() => page.Instance.SaveTransitionAsync(TransitionBaseline(seed.Transition), TransitionDraft(seed.Transition, notes: "notes"), "test")); return;
            case "transition-inverse-update": await TransitionInverseProposal(page, seed.Transition.Id, true); return;
            case "transition-inverse-do-not-update": await TransitionInverseProposal(page, seed.Transition.Id, false); return;
            case "transition-source": await TransitionText(page, seed.Transition.Id, "source", "two"); return;
            case "transition-destination": await TransitionText(page, seed.Transition.Id, "destination-room", "other"); return;
            case "transition-requirements": await TransitionText(page, seed.Transition.Id, "requirements", "changed requirements"); return;
            case "transition-todo": await TransitionControl(page, seed.Transition.Id, "todo", "true"); return;
            case "transition-verification": await TransitionControl(page, seed.Transition.Id, "verification", "true"); return;
            case "transition-metadata-non-xy": await TransitionMetadata(page, seed.Transition.Id, "game ID", "transition-game"); return;
            case "check-reorder": await page.InvokeAsync(() => page.FindComponent<CheckTablePresentation>().Instance.DropCheckAsync(seed.Check.Id.ToString(), "active", 0)); return;
            case "check-notes": await SaveCheck(page, seed.Check, notes: "notes"); return;
            case "check-subroom": await CheckText(page, seed.Check.Id, "subroom", "one"); return;
            case "check-requirements": await CheckText(page, seed.Check.Id, "requirements", "changed requirements"); return;
            case "check-apworld": await CheckControl(page, seed.Check.Id, "apworld", "false"); return;
            case "check-todo": await CheckControl(page, seed.Check.Id, "todo", "true"); return;
            case "check-verification": await CheckControl(page, seed.Check.Id, "verification", "true"); return;
            case "check-metadata-non-xy": await CheckMetadata(page, seed.Check.Id, "game ID", "check-game"); return;
            case "connection-reorder": await page.InvokeAsync(() => page.Instance.ReorderConnectionAsync(seed.Connection.Id, 0)); return;
            case "connection-notes": await page.InvokeAsync(() => page.Instance.SaveConnectionAsync(ConnectionBaseline(seed.Connection), ConnectionDraft(seed.Connection, notes: "notes"), "test")); return;
            case "connection-requirements": await ConnectionText(page, seed.Connection.Id, "requirements", "changed requirements"); return;
            case "connection-todo": await ConnectionControl(page, seed.Connection.Id, "todo", "true"); return;
            case "connection-verification": await ConnectionControl(page, seed.Connection.Id, "verification", "true"); return;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

    private static async Task SubroomAction(IRenderedComponent<RoomEditorV2Page> p, string action, Guid id) { ShowArchivedIfNeeded(p, "data-v2-subroom-row", id); await p.InvokeAsync(() => p.FindComponent<SubroomTablePresentation>().Instance.ChildActionSubroomAsync(action, RenderedId(p, "data-v2-subroom-row", id))); }
    private static async Task CheckAction(IRenderedComponent<RoomEditorV2Page> p, string action, Guid id) { ShowArchivedIfNeeded(p, "data-v2-check-row", id); await p.InvokeAsync(() => p.FindComponent<CheckTablePresentation>().Instance.ChildActionCheckAsync(action, RenderedId(p, "data-v2-check-row", id))); }
    private static void ShowArchivedIfNeeded(IRenderedComponent<RoomEditorV2Page> page, string attribute, Guid id)
    {
        if (page.FindAll($"tr[{attribute}]").Any(x => x.GetAttribute(attribute) == id.ToString())) return;
        page.FindAll("button").Single(x => x.TextContent.Trim() == "show archived room contents").Click();
    }
    private static string RenderedId(IRenderedComponent<RoomEditorV2Page> page, string attribute, Guid id)
    {
        var row = page.FindAll($"tr[{attribute}]").Single(x => x.GetAttribute(attribute) == id.ToString());
        return row.GetAttribute(attribute)!;
    }

    private static async Task DeleteSubroom(IRenderedComponent<RoomEditorV2Page> page, Guid id) { await SubroomAction(page, "open-delete", id); page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "permanently delete").Click(); }
    private static async Task DeleteCheck(IRenderedComponent<RoomEditorV2Page> page, Guid id) { await CheckAction(page, "open-delete", id); page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "permanently delete").Click(); }
    private static async Task DeleteTransition(IRenderedComponent<RoomEditorV2Page> page, Guid id) { await page.InvokeAsync(() => page.Instance.RequestTransitionPermanentDeleteAsync(id, "test")); page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "permanently delete").Click(); }
    private static async Task DeleteConnection(IRenderedComponent<RoomEditorV2Page> page, Guid id) { await page.InvokeAsync(() => page.Instance.RequestConnectionPermanentDeleteAsync(id, "test")); page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "permanently delete").Click(); }

    private static async Task SubroomReferenceProposal(IRenderedComponent<RoomEditorV2Page> page, Guid id, bool update)
    {
        var input = page.Find($"tr[data-v2-subroom-row='{id}'] [data-v2-subroom-field='reference']"); input.Input("renamed");
        await page.InvokeAsync(() => page.FindComponent<SubroomTablePresentation>().Instance.BlurSubroomAsync(input.GetAttribute("data-v2-subroom-client-row")!, "reference", null));
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == (update ? "update references" : "do not update references")).Click();
    }
    private static async Task TransitionText(IRenderedComponent<RoomEditorV2Page> page, Guid id, string field, string value) { var input = page.Find($"tr[data-v2-transition-row='{id}'] [data-v2-transition-field='{field}']"); input.Input(value); await page.InvokeAsync(() => page.FindComponent<TransitionTablePresentation>().Instance.BlurTransitionAsync(input.GetAttribute("data-v2-transition-client-row")!, field, null)); }
    private static Task TransitionControl(IRenderedComponent<RoomEditorV2Page> page, Guid id, string field, string value) { var input = page.Find($"tr[data-v2-transition-row='{id}'] [data-v2-transition-field='{field}']"); return page.InvokeAsync(() => page.FindComponent<TransitionTablePresentation>().Instance.ControlTransitionAsync(input.GetAttribute("data-v2-transition-client-row")!, field, value)); }
    private static async Task TransitionInverseProposal(IRenderedComponent<RoomEditorV2Page> page, Guid id, bool update)
    {
        await TransitionText(page, id, "destination-room", "other");
        await TransitionText(page, id, "destination-alias", "b");
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == (update ? "update inverse" : "do not update inverse")).Click();
    }
    private static async Task CheckText(IRenderedComponent<RoomEditorV2Page> page, Guid id, string field, string value) { var input = page.Find($"tr[data-v2-check-row='{id}'] [data-v2-check-field='{field}']"); input.Input(value); await page.InvokeAsync(() => page.FindComponent<CheckTablePresentation>().Instance.BlurCheckAsync(input.GetAttribute("data-v2-check-client-row")!, field, null)); }
    private static Task CheckControl(IRenderedComponent<RoomEditorV2Page> page, Guid id, string field, string value) { var input = page.Find($"tr[data-v2-check-row='{id}'] [data-v2-check-field='{field}']"); return page.InvokeAsync(() => page.FindComponent<CheckTablePresentation>().Instance.ControlCheckAsync(input.GetAttribute("data-v2-check-client-row")!, field, value)); }
    private static async Task ConnectionText(IRenderedComponent<RoomEditorV2Page> page, Guid id, string field, string value) { var input = page.Find($"tr[data-v2-connection-row='{id}'] [data-v2-connection-field='{field}']"); input.Input(value); await page.InvokeAsync(() => page.FindComponent<ConnectionTablePresentation>().Instance.BlurConnectionAsync(input.GetAttribute("data-v2-connection-client-row")!, field, null)); }
    private static Task ConnectionControl(IRenderedComponent<RoomEditorV2Page> page, Guid id, string field, string value) { var input = page.Find($"tr[data-v2-connection-row='{id}'] [data-v2-connection-field='{field}']"); return page.InvokeAsync(() => page.FindComponent<ConnectionTablePresentation>().Instance.ControlConnectionAsync(input.GetAttribute("data-v2-connection-client-row")!, field, value)); }

    private static async Task CreateSubroom(IRenderedComponent<RoomEditorV2Page> page) { var field = page.Find("[data-v2-subroom-tail='true'] [data-v2-subroom-field='name']"); field.Input("created"); await page.InvokeAsync(() => page.FindComponent<SubroomTablePresentation>().Instance.BlurSubroomAsync(field.GetAttribute("data-v2-subroom-client-row")!, "name", null)); }
    private static async Task CreateCheck(IRenderedComponent<RoomEditorV2Page> page) { var field = page.Find("[data-v2-check-tail='true'] [data-v2-check-field='name']"); field.Input("created"); await page.InvokeAsync(() => page.FindComponent<CheckTablePresentation>().Instance.BlurCheckAsync(field.GetAttribute("data-v2-check-client-row")!, "name", null)); }
    private static async Task SaveSubroom(IRenderedComponent<RoomEditorV2Page> page, Subroom row, string? name = null, string? notes = null) { var field = page.Find($"tr[data-v2-subroom-row='{row.Id}'] [data-v2-subroom-field='{(name is null ? "notes" : "name")}']"); field.Input(name ?? notes!); await page.InvokeAsync(() => page.FindComponent<SubroomTablePresentation>().Instance.BlurSubroomAsync(field.GetAttribute("data-v2-subroom-client-row")!, name is null ? "notes" : "name", null)); }
    private static async Task SaveCheck(IRenderedComponent<RoomEditorV2Page> page, CheckLocation row, string? name = null, string? notes = null) { var field = page.Find($"tr[data-v2-check-row='{row.Id}'] [data-v2-check-field='{(name is null ? "notes" : "name")}']"); field.Input(name ?? notes!); await page.InvokeAsync(() => page.FindComponent<CheckTablePresentation>().Instance.BlurCheckAsync(field.GetAttribute("data-v2-check-client-row")!, name is null ? "notes" : "name", null)); }
    private static async Task TransitionMetadata(IRenderedComponent<RoomEditorV2Page> page, Guid id, string label, string value) { page.Find($"#v2-transition-metadata-{id}").Click(); page.Find($"#v2-page-modal-dialog [aria-label='{label}']").Input(value); page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "Apply").Click(); await Task.CompletedTask; }
    private static async Task CheckMetadata(IRenderedComponent<RoomEditorV2Page> page, Guid id, string label, string value) { page.Find($"#v2-check-metadata-{id}").Click(); page.Find($"#v2-page-modal-dialog [aria-label='{label}']").Input(value); page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "Apply").Click(); await Task.CompletedTask; }

    private static V2RoomOperationTrace Trace(IRenderedComponent<RoomEditorV2Page> page) => ((RoomEditorV2RefreshCoordinator)typeof(RoomEditorV2Page).GetField("refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance)!).OperationTrace;
    private static int MapTrace(TestContext context) => ((TestAreaMapLoader)context.Services.GetRequiredService<IAreaMapLoader>()).RoomLoads;

    private static async Task<Seed> SeedAsync(MigratedSqliteFixture fixture, bool archived = false)
    {
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 10, SceneUnitHeight = 10, IsArchived = archived, ArchivedUtc = archived ? DateTime.UtcNow : null };
        var subroom = new Subroom { RoomId = room.Id, FriendlyName = "one", ReferenceId = "one", IsArchived = archived, ArchivedUtc = archived ? DateTime.UtcNow : null };
        var other = new Subroom { RoomId = room.Id, FriendlyName = "two", ReferenceId = "two", SortOrder = 1 };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "exit", SourceSubroomReferenceText = "one", ResolvedSourceSubroomId = subroom.Id, Requirements = "r", IsArchived = archived, ArchivedUtc = archived ? DateTime.UtcNow : null };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r", IsArchived = archived, ArchivedUtc = archived ? DateTime.UtcNow : null };
        var connection = new SubroomConnection { RoomId = room.Id, Alias = "a", FriendlyName = "path", SourceSubroomReferenceText = "one", DestinationSubroomReferenceText = "two", Requirements = "r", IsArchived = archived, ArchivedUtc = archived ? DateTime.UtcNow : null };
        await using var db = fixture.CreateDbContext(); db.AddRange(room, subroom, other, transition, check, connection); await db.SaveChangesAsync();
        return new(room, subroom, transition, check, connection);
    }

    private static async Task AddCapturedMapLinkAsync(MigratedSqliteFixture fixture, Room room)
    {
        await using var db = fixture.CreateDbContext();
        var map = new Map { InGameId = "map" }; db.Add(map); await db.SaveChangesAsync();
        var zone = new MapZone { MapId = map.Id, InGameId = "zone" }; db.Add(zone); await db.SaveChangesAsync();
        db.Add(new MapScene { MapZoneId = zone.Id, InGameId = "scene", RoomReferenceText = room.ReferenceId, ResolvedRoomId = room.Id });
        await db.SaveChangesAsync();
    }

    private static async Task SeedInverseTargetAsync(MigratedSqliteFixture fixture, Seed seed)
    {
        await using var db = fixture.CreateDbContext();
        var targetRoom = new Room { FriendlyName = "other", ReferenceId = "other", SortOrder = 1 };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "b", FriendlyName = "inverse" };
        db.AddRange(targetRoom, target);
        await db.SaveChangesAsync();
    }

    private static async Task AssertRouteOutcomeAsync(MigratedSqliteFixture fixture, Seed seed, string route)
    {
        await using var db = fixture.CreateDbContext();
        switch (route)
        {
            case "subroom-reference-update":
                Assert.Equal("renamed", (await db.Subrooms.SingleAsync(x => x.Id == seed.Subroom.Id)).ReferenceId);
                Assert.Equal("renamed", (await db.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id)).SourceSubroomReferenceText);
                break;
            case "subroom-reference-do-not-update":
                Assert.Equal("renamed", (await db.Subrooms.SingleAsync(x => x.Id == seed.Subroom.Id)).ReferenceId);
                Assert.Equal("one", (await db.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id)).SourceSubroomReferenceText);
                break;
            case "transition-source": Assert.Equal("two", (await db.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id)).SourceSubroomReferenceText); break;
            case "transition-destination": Assert.Equal("other", (await db.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id)).DestinationRoomReferenceText); break;
            case "transition-requirements": Assert.Equal("changed requirements", (await db.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id)).Requirements); break;
            case "transition-todo": Assert.True((await db.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id)).IsTodo); break;
            case "transition-verification": Assert.True((await db.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id)).IsVerified); break;
            case "transition-metadata-non-xy": Assert.Equal("transition-game", (await db.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id)).InGameId); break;
            case "transition-inverse-update":
                Assert.Equal("other", (await db.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id)).DestinationRoomReferenceText);
                Assert.Equal(seed.Room.ReferenceId, (await db.RoomTransitions.SingleAsync(x => x.FriendlyName == "inverse")).DestinationRoomReferenceText);
                break;
            case "transition-inverse-do-not-update":
                Assert.Equal("other", (await db.RoomTransitions.SingleAsync(x => x.Id == seed.Transition.Id)).DestinationRoomReferenceText);
                Assert.Null((await db.RoomTransitions.SingleAsync(x => x.FriendlyName == "inverse")).DestinationRoomReferenceText);
                break;
            case "check-subroom": Assert.Equal("one", (await db.CheckLocations.SingleAsync(x => x.Id == seed.Check.Id)).SubroomReferenceText); break;
            case "check-requirements": Assert.Equal("changed requirements", (await db.CheckLocations.SingleAsync(x => x.Id == seed.Check.Id)).Requirements); break;
            case "check-apworld": Assert.False((await db.CheckLocations.SingleAsync(x => x.Id == seed.Check.Id)).IsIncludedInApworld); break;
            case "check-todo": Assert.True((await db.CheckLocations.SingleAsync(x => x.Id == seed.Check.Id)).IsTodo); break;
            case "check-verification": Assert.True((await db.CheckLocations.SingleAsync(x => x.Id == seed.Check.Id)).IsVerified); break;
            case "check-metadata-non-xy": Assert.Equal("check-game", (await db.CheckLocations.SingleAsync(x => x.Id == seed.Check.Id)).InGameId); break;
            case "connection-requirements": Assert.Equal("changed requirements", (await db.SubroomConnections.SingleAsync(x => x.Id == seed.Connection.Id)).Requirements); break;
            case "connection-todo": Assert.True((await db.SubroomConnections.SingleAsync(x => x.Id == seed.Connection.Id)).IsTodo); break;
            case "connection-verification": Assert.True((await db.SubroomConnections.SingleAsync(x => x.Id == seed.Connection.Id)).IsVerified); break;
            case "room-scene-dimensions":
                var room = await db.Rooms.SingleAsync(x => x.Id == seed.Room.Id);
                Assert.Equal((12d, 24d), (room.SceneUnitWidth, room.SceneUnitHeight));
                break;
        }
    }

    private sealed record Seed(Room Room, Subroom Subroom, RoomTransition Transition, CheckLocation Check, SubroomConnection Connection);
    /// <summary>Test-only hold around the production dimensions command.</summary>
    private sealed class HeldSceneDimensionsCommands(IRoomEditorV2CommandService inner) : IRoomEditorV2CommandService
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public V2RoomSceneDimensionsCommandOutcome? LastSceneDimensionsOutcome { get; private set; }
        public Task WaitUntilEnteredAsync() => entered.Task;
        public Task WaitUntilCompletedAsync() => completed.Task;
        public void Release() => release.TrySetResult();
        public async Task<V2RoomSceneDimensionsCommandOutcome> SaveRoomSceneDimensionsAsync(Guid roomId, RoomSceneDimensionsDurableBaseline baseline, RoomSceneDimensionsDraft draft)
        {
            Calls++; entered.TrySetResult(); await release.Task;
            try { return LastSceneDimensionsOutcome = await inner.SaveRoomSceneDimensionsAsync(roomId, baseline, draft); }
            finally { completed.TrySetResult(); }
        }
        public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => inner.SaveSubroomAsync(roomId, baseline, draft);
        public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft) => inner.CreateSubroomAsync(roomId, draft);
        public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => inner.ReorderSubroomAsync(roomId, entityId, targetIndex);
        public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => inner.SetSubroomArchiveAsync(roomId, entityId, archived);
        public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => inner.DeleteSubroomAsync(roomId, entityId);
    }
    private static TransitionDraft NewTransition() => new(Guid.NewGuid(), "new", "new", null, null, null, null, null, null, null, null, null, null, null, null, "r", "", false, null);
    private static ConnectionDraft NewConnection() => new(Guid.NewGuid(), "new", "new", "one", "two", "r", "", false, null, null, false, null);
    private static TransitionDurableBaseline TransitionBaseline(RoomTransition x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.Alias, x.FriendlyName, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, x.Requirements, x.Notes, x.IsTodo, x.IsVerified);
    private static TransitionDraft TransitionDraft(RoomTransition x, string? alias = null, string? name = null, string? notes = null) => new(Guid.NewGuid(), alias ?? x.Alias, name ?? x.FriendlyName, x.InGameId, x.InGamePositionX, x.InGamePositionY, x.InGamePositionZ, x.LocalPositionX, x.LocalPositionY, x.LocalPositionZ, x.AnnotationSceneUnitX, x.AnnotationSceneUnitY, x.SourceSubroomReferenceText, x.DestinationRoomReferenceText, x.DestinationTransitionAliasText, x.Requirements, notes ?? x.Notes, x.IsTodo, x.IsVerified);
    private static ConnectionDurableBaseline ConnectionBaseline(SubroomConnection x) => new(x.Id, x.UpdatedUtc, x.SortOrder, x.IsArchived, x.Alias, x.FriendlyName, x.SourceSubroomReferenceText, x.DestinationSubroomReferenceText, x.Requirements, x.Notes, x.EnableAnnotation, x.SceneUnitX, x.SceneUnitY, x.IsTodo, x.IsVerified);
    private static ConnectionDraft ConnectionDraft(SubroomConnection x, string? alias = null, string? name = null, string? source = null, string? destination = null, string? notes = null) => new(Guid.NewGuid(), alias ?? x.Alias, name ?? x.FriendlyName, source ?? x.SourceSubroomReferenceText, destination ?? x.DestinationSubroomReferenceText, x.Requirements, notes ?? x.Notes, x.EnableAnnotation, x.SceneUnitX, x.SceneUnitY, x.IsTodo, x.IsVerified);
}
