using Bunit;
using Bunit.Web.AngleSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using System.Reflection;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

public sealed class RoomEditorV2PresentationTests
{
    [Fact]
    public void Header_OrdersMatchingFreeformTextareasWithSharedMetadataSizingAndEditableBlurControls()
    {
        using var context = new TestContext();
        var view = new RoomHeaderView(Guid.NewGuid(), "Room", "reference", "game", "contributors", "comments", null, null, false, false, false, DateTime.UtcNow, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral);
        var component = context.RenderComponent<DocumentHeaderPresentation>(p => p.Add(x => x.View, view).Add(x => x.Draft, new RoomHeaderDraft("Room", "game", "contributors", "comments")));
        var fields = component.FindAll(".room-meta-authored textarea").ToArray();
        Assert.Equal(["Room reference ID", "Room game ID", "Room contributors"], fields.Select(x => x.GetAttribute("aria-label")));
        Assert.All(fields, field => Assert.Equal("1", field.GetAttribute("rows")));
        Assert.Null(fields[0].GetAttribute("list"));
        Assert.Equal("off", fields[0].GetAttribute("autocomplete"));
        Assert.Empty(component.FindAll("datalist"));
        Assert.Equal("Room contributors", fields[2].GetAttribute("aria-label"));
        Assert.Null(fields[1].GetAttribute("readonly")); Assert.Null(fields[2].GetAttribute("readonly"));
        Assert.Contains("class=\"room-meta-authored\"", component.Markup); Assert.Contains("<label>game", component.Markup); Assert.Contains("<label>contributors", component.Markup);
    }
    [Fact]
    public void RefreshDiagnostic_RendersOnlyAppliedSequenceAndServerElapsed()
    {
        using var context = new TestContext();
        var component = context.RenderComponent<V2RefreshDiagnosticPresentation>(p => p.Add(x => x.Diagnostic, new V2RefreshDiagnosticView(3, TimeSpan.FromMilliseconds(12.34))));
        Assert.Contains("refresh #3 · server 12.3 ms", component.Markup);
        Assert.Equal("refresh #3 · server 12.3 ms", component.Find(".v2-refresh-diagnostic").TextContent);
    }

    [Fact]
    public void MapContextControlsAndPanel_AreAbsentUntilBrowserVisibilityResolves()
    {
        using var context = new TestContext();
        var header = context.RenderComponent<RoomHeaderPresentation>(p => p.Add(x => x.View, View(Guid.NewGuid()).Header)
            .Add(x => x.ShowMapContext, true).Add(x => x.MapContextVisibilityResolved, false));

        Assert.Empty(header.FindAll(".room-map-context, .room-map-context-toggle"));
        Assert.NotNull(header.Find(".back-to-map-button"));
        Assert.Empty(header.FindAll("#room-scene-dimensions"));
    }

    [Theory]
    [InlineData(false, "show map context", "false")]
    [InlineData(true, "hide map context", "true")]
    public async Task MapContextVisibility_DeferredInitialLoadAdmitsOnceAcrossRerenderAndNavigationThenTogglesOnce(bool visible, string toggleText, string pressed)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader());
        context.Services.AddSingleton<MapRenderProjectionService>();
        context.Services.AddSingleton<MapOverlayAssetCatalog>();
        context.Services.AddSingleton<MapOverlayPlacementService>();
        context.Services.AddSingleton<DiagnosticState>();
        context.Services.AddSingleton<MapLinkService>();
        var visibilityLoad = context.JSInterop.Setup<bool>("loadRoomMapContextVisibility");
        var firstRoom = Guid.NewGuid();
        var secondRoom = Guid.NewGuid();
        var thirdRoom = Guid.NewGuid();
        var loader = new HeaderLoader(View(firstRoom));
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader);
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new HeaderCommands());
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, firstRoom));

        page.WaitForAssertion(() => Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "loadRoomMapContextVisibility")));
        Assert.Empty(page.FindAll(".room-map-context, .room-map-context-toggle"));

        page.Render();
        page.SetParametersAndRender(p => p.Add(x => x.RoomId, secondRoom));
        Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "loadRoomMapContextVisibility"));
        Assert.Empty(page.FindAll(".room-map-context, .room-map-context-toggle"));
        var navigationLoadCount = loader.LoadCount;

        visibilityLoad.SetResult(visible);
        page.WaitForAssertion(() =>
        {
            Assert.Equal(navigationLoadCount, loader.LoadCount);
            Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "loadRoomMapContextVisibility"));
            var toggle = page.Find(".room-map-context-toggle");
            Assert.Equal(toggleText, toggle.TextContent.Trim());
            Assert.Equal(pressed, toggle.GetAttribute("aria-pressed"));
            Assert.Equal(visible ? 1 : 0, page.FindAll(".room-map-context").Count);
            Assert.Equal(visible ? 1 : 0, context.JSInterop.Invocations.Count(x => x.Identifier == "initializeV2SceneViewport"));
            Assert.Equal(0, context.JSInterop.Invocations.Count(x => x.Identifier == "disposeV2SceneViewport"));
        });

        page.Find(".room-map-context-toggle").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Equal(navigationLoadCount, loader.LoadCount);
            Assert.Single(context.JSInterop.Invocations, x => x.Identifier == "saveRoomMapContextVisibility");
            Assert.Equal(!visible, (bool)context.JSInterop.Invocations.Single(x => x.Identifier == "saveRoomMapContextVisibility").Arguments[0]!);
        });

        page.SetParametersAndRender(p => p.Add(x => x.RoomId, thirdRoom));
        page.WaitForAssertion(() =>
        {
            Assert.Equal(navigationLoadCount + 1, loader.LoadCount);
            Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "loadRoomMapContextVisibility"));
            Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "saveRoomMapContextVisibility"));
            Assert.Equal(!visible ? 1 : 0, page.FindAll(".room-map-context").Count);
        });
    }

    [Fact]
    public async Task TypedAreaMapHost_UsesSoleRoomRouteAndSuccessfulMapLinkCloseRefreshesSceneExactlyOnce()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            var map = new Map { InGameId = "map", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 100, MapUnitMaxY = 100 };
            var zone = new MapZone { Map = map, InGameId = "zone", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 100, MapUnitMaxY = 100 };
            var sourceRoom = new Room { Id = source, FriendlyName = "Source", ReferenceId = "source" };
            var destinationRoom = new Room { Id = destination, FriendlyName = "Destination", ReferenceId = "destination" };
            var sourceScene = new MapScene { MapZone = zone, InGameId = "source", RoomReferenceText = "source", ResolvedRoomId = source };
            var destinationScene = new MapScene { MapZone = zone, InGameId = "destination", RoomReferenceText = "destination", ResolvedRoomId = destination };
            db.AddRange(map, zone, sourceRoom, destinationRoom, sourceScene, destinationScene,
                new MapChunk { MapScene = sourceScene, CacheIndex = 0, MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 20, MapUnitMaxY = 20 },
                new MapChunk { MapScene = destinationScene, CacheIndex = 0, MapUnitMinX = 30, MapUnitMinY = 0, MapUnitMaxX = 50, MapUnitMaxY = 20 });
            await db.SaveChangesAsync();
        }

        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("loadRoomMapContextVisibility").SetResult(true);
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddScoped<IAreaMapLoader>(_ => new AreaMapLoader(fixture, new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService(), new AppliedRoomStatusService(fixture))); context.Services.AddSingleton<MapRenderProjectionService>();
        context.Services.AddSingleton<MapOverlayAssetCatalog>(); context.Services.AddSingleton<MapOverlayPlacementService>();
        context.Services.AddSingleton<DiagnosticState>(); context.Services.AddSingleton<MapLinkService>();
        var loader = new MapHostLoader(View(source));
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader); context.Services.AddSingleton<IRoomEditorV2CommandService>(new HeaderCommands());
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source));
        page.WaitForAssertion(() =>
        {
            var host = page.Find(".room-map-context");
            Assert.Contains("with-zone-map", host.ClassName);
            Assert.Contains("v2-scene-zone-preference-pending", host.ClassName);
            Assert.Single(page.FindAll(".room-context-zone-pane"));
            Assert.Single(page.FindAll(".room-context-divider"));
            Assert.Single(page.FindAll("svg.room-context-wireframe"));
            Assert.Empty(page.FindAll(".area-map-placeholder"));
            Assert.Single(page.FindAll(".current-room-frame"));
        });

        var beforeLoads = loader.LoadCount; var beforeScenes = loader.Scene.Count;
        page.FindAll(".room-map-context-map-actions button").Single(x => x.TextContent.Trim() == "edit map links").Click();
        page.FindAll(".map-link-modal button").Single(x => x.TextContent.Trim() == "apply").Click();
        page.WaitForAssertion(() => { Assert.Empty(page.FindAll(".map-link-modal")); Assert.Equal(beforeLoads + 1, loader.LoadCount); Assert.Equal(beforeScenes + 1, loader.Scene.Count); });

        page.FindAll("path.linked-owner").Single(x => x.GetAttribute("aria-label") == "Destination").Click();
        page.WaitForAssertion(() => Assert.EndsWith($"/rooms/{destination:D}", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
    }

    [Fact]
    public void ArchivedRoom_OmitsMapContextPanelAndControlAfterVisibilityResolves()
    {
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("loadRoomMapContextVisibility").SetResult(true);
        var roomId = Guid.NewGuid();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new HeaderLoader(View(roomId) with { Header = View(roomId).Header with { IsArchived = true } }));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new HeaderCommands());
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));

        page.WaitForAssertion(() =>
        {
            Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "loadRoomMapContextVisibility"));
            Assert.Empty(page.FindAll(".room-map-context, .room-map-context-toggle"));
        });
    }

    [Fact]
    public void V2RoomDocument_RendersHeaderScrollingContentAndReminderAsGridBands()
    {
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var roomId = Guid.NewGuid();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new HeaderLoader(View(roomId)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new HeaderCommands());

        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));

        page.WaitForAssertion(() =>
        {
            Assert.Single(page.FindAll(".room-editor-v2 > article.room-document > .room-sticky-context"));
            Assert.Single(page.FindAll(".room-editor-v2 > article.room-document > .room-document-content"));
            Assert.Single(page.FindAll(".room-editor-v2 > article.room-document > .room-reminder-footer"));
            Assert.NotEmpty(page.FindAll(".room-document-content > .document-section"));
        });
    }

    [Theory]
    [InlineData(V2RoomHeaderSaveState.Saving, "Saving")]
    [InlineData(V2RoomHeaderSaveState.Conflict, "Conflict")]
    [InlineData(V2RoomHeaderSaveState.Failed, "Save failed")]
    [InlineData(V2RoomHeaderSaveState.Missing, "Reload required")]
    public void HeaderSaveStatus_RendersEveryNonSavedTerminalOrPendingOutcome(V2RoomHeaderSaveState state, string text)
    {
        using var context = new TestContext();
        var component = context.RenderComponent<RoomSaveStatusPresentation>(p => p.Add(x => x.Status, new V2RoomHeaderSaveStatus(state)));
        Assert.Equal(text, component.Find(".room-save-status").TextContent);
        Assert.NotEqual("Saved", component.Find(".room-save-status").TextContent);
    }

    [Fact]
    public void MissingHeaderStatus_MakesEveryOrdinaryHeaderEditorReadOnly()
    {
        using var context = new TestContext();
        var view = new RoomHeaderView(Guid.NewGuid(), "Room", "reference", "game", "contributors", "comments", null, null, false, false, false, DateTime.UtcNow, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral);
        var component = context.RenderComponent<DocumentHeaderPresentation>(p => p.Add(x => x.View, view).Add(x => x.Draft, new RoomHeaderDraft("local", "game", "contributors", "comments")).Add(x => x.Status, new V2RoomHeaderSaveStatus(V2RoomHeaderSaveState.Missing)));
        Assert.Equal(4, component.FindAll("input[readonly], textarea[readonly]").Count);
        Assert.Equal("Reload required", component.Find(".room-save-status").TextContent);
    }

    [Fact]
    public async Task HeaderBlur_ShowsSavingThenConflictRetainsLocalDraftAndMissingDisablesEditorsWithoutRefresh()
    {
        using var context = new TestContext();
        var roomId = Guid.NewGuid(); var loader = new HeaderLoader(View(roomId)); var commands = new HeaderCommands();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader);
        context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));
        var title = page.Find("textarea[aria-label='Room friendly name']");
        title.Input("local");
        var blur = title.TriggerEventAsync("onblur", new FocusEventArgs());
        page.WaitForAssertion(() => Assert.Equal("Saving", page.Find(".room-save-status").TextContent));

        commands.Pending.SetResult(new(V2RoomHeaderCommandStatus.Conflict, "changed", new(roomId, DateTime.UtcNow, "durable", null, null, null), new("local", null, null, null)));
        await blur;
        page.WaitForAssertion(() => { Assert.Equal("Conflict", page.Find(".room-save-status").TextContent); Assert.Equal("local", page.Find("textarea[aria-label='Room friendly name']").GetAttribute("value")); Assert.Equal(1, loader.LoadCount); });

        commands.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var missingBlur = page.Find("textarea[aria-label='Room friendly name']").TriggerEventAsync("onblur", new FocusEventArgs());
        page.WaitForAssertion(() => Assert.Equal("Saving", page.Find(".room-save-status").TextContent));
        commands.Pending.SetResult(new(V2RoomHeaderCommandStatus.Missing, "gone"));
        await missingBlur;
        page.WaitForAssertion(() => { Assert.Equal("Reload required", page.Find(".room-save-status").TextContent); Assert.NotNull(page.Find("textarea[aria-label='Room friendly name']").GetAttribute("readonly")); Assert.Equal(1, loader.LoadCount); });
    }

    [Theory]
    [InlineData("textarea[aria-label='Room friendly name']", "local title")]
    [InlineData("textarea[aria-label='Room game ID']", "local game")]
    [InlineData("textarea[aria-label='Room contributors']", "local contributors")]
    [InlineData("textarea[aria-label='Room comments']", "local comments")]
    public async Task HeaderBlur_UnexpectedShowsSavingThenSaveFailedRetainsEditableDraftWithoutRefresh(string selector, string localValue)
    {
        using var context = new TestContext();
        var roomId = Guid.NewGuid(); var loader = new HeaderLoader(View(roomId)); var commands = new HeaderCommands();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader);
        context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));
        if (selector.Contains("comments", StringComparison.Ordinal)) page.Find(".document-comments").Click();
        var field = page.Find(selector);
        field.Input(localValue);
        var blur = field.TriggerEventAsync("onblur", new FocusEventArgs());
        page.WaitForAssertion(() =>
        {
            Assert.Equal("Saving", page.Find(".room-save-status").TextContent);
            Assert.NotEqual("Saved", page.Find(".room-save-status").TextContent);
        });

        commands.Pending.SetResult(new(V2RoomHeaderCommandStatus.Unexpected, "database unavailable", null, new("local title", "local game", "local contributors", "local comments")));
        await blur;

        page.WaitForAssertion(() =>
        {
            Assert.Equal("Save failed", page.Find(".room-save-status").TextContent);
            Assert.NotEqual("Saved", page.Find(".room-save-status").TextContent);
            if (selector.Contains("comments", StringComparison.Ordinal)) page.Find(".document-comments").Click();
            var retained = page.Find(selector);
            Assert.Null(retained.GetAttribute("readonly"));
            Assert.Equal(localValue, retained.GetAttribute("value"));
            Assert.Equal(1, loader.LoadCount);
        });
    }

    [Theory]
    [InlineData("update references", true)]
    [InlineData("do not update references", false)]
    public async Task HeaderReferenceProposal_PageWorkflowUsesFreeformTextareaBlocksCommandsAndRefreshesBeforeInitiatorFocus(string action, bool updateReferences)
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true); context.JSInterop.SetupVoid("focusEditorField", _ => true);
        var roomId = Guid.NewGuid(); var loader = new ReferenceWorkflowLoader(View(roomId) with { Header = View(roomId).Header with { ReferenceId = "old" } });
        var commands = new ReferenceWorkflowCommands(loader);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader); context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));

        var reference = page.Find("textarea[aria-label='Room reference ID']");
        Assert.Null(reference.GetAttribute("list"));
        Assert.Equal("off", reference.GetAttribute("autocomplete"));
        Assert.Empty(page.FindAll("datalist#v2-room-reference-suggestions"));
        reference.Input("renamed");
        await reference.TriggerEventAsync("onblur", new FocusEventArgs());
        page.WaitForAssertion(() =>
        {
            Assert.Equal("ProposalOpen", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage"));
            Assert.Equal(["revert change", "do not update references", "update references"], page.FindAll("#v2-page-modal-dialog button").Select(x => x.TextContent.Trim()));
        });

        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));
        Assert.Equal(V2RoomHeaderCommandStatus.Unchanged, (await page.InvokeAsync(page.Instance.RequestHeaderSaveTestIntentAsync)).Status);
        Assert.Equal(1, commands.PrepareCalls);
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == action).Click();

        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            Assert.Equal(updateReferences, commands.UpdateReferences);
            Assert.Equal(2, loader.LoadCount);
            Assert.Equal("renamed", page.Find("textarea[aria-label='Room reference ID']").TextContent);
            Assert.Equal(1, FocusCallCount(context, "room-reference-id"));
        });
    }

    [Fact]
    public void ArchivedSubroomAndCheckRows_SuppressDiagnosticsTodoAndDragButKeepLifecycleActions()
    {
        using var context = new TestContext();
        var id = Guid.NewGuid();
        var subroom = context.RenderComponent<SubroomTablePresentation>(p => p
            .Add(x => x.RoomId, Guid.NewGuid()).Add(x => x.ShowArchivedChildren, true)
            .Add(x => x.View, new SubroomTableView([], [new(id, 0, true, DateTime.UtcNow, "duplicate", "duplicate", "notes", null, null, null, null, V2Severity.Danger, V2Severity.Danger, V2Severity.Danger)])));
        var check = context.RenderComponent<CheckTablePresentation>(p => p
            .Add(x => x.RoomId, Guid.NewGuid()).Add(x => x.ShowArchivedChildren, true)
            .Add(x => x.View, new CheckTableView([], [Check(id, true)], ["subroom"])));

        AssertArchivedMinimal(ArchivedPartition(subroom.Markup, "data-v2-subroom-table=\"archived\""));
        AssertArchivedMinimal(ArchivedPartition(check.Markup, "data-v2-check-table=\"archived\""));
    }

    [Fact]
    public void ArchivedTransitionAndConnectionRows_SuppressDerivedStateAndSupplementalActions()
    {
        using var context = new TestContext();
        var id = Guid.NewGuid();
        var transition = context.RenderComponent<TransitionTablePresentation>(p => p
            .Add(x => x.ShowArchivedChildren, true)
            .Add(x => x.View, new TransitionTableView([], [Transition(id, true)], ["room"], new Dictionary<Guid, IReadOnlyList<string>> { [id] = ["a"] })));
        var connection = context.RenderComponent<ConnectionTablePresentation>(p => p
            .Add(x => x.ShowArchivedChildren, true)
            .Add(x => x.View, new ConnectionTableView([], [Connection(id, true)], ["subroom"])));

        var archivedTransition = ArchivedPartition(transition.Markup, "data-v2-transition-table=\"archived\"");
        var archivedConnection = ArchivedPartition(connection.Markup, "data-v2-connection-table=\"archived\"");
        AssertArchivedMinimal(archivedTransition);
        Assert.DoesNotContain("transition-inverse-state\"><i", archivedTransition);
        AssertArchivedMinimal(archivedConnection);
        Assert.DoesNotContain("connection-state\"><i", archivedConnection);
        Assert.DoesNotContain("scaffold inverse connection", archivedConnection);
    }

    [Fact]
    public void ActiveRows_RetainOrdinaryDiagnosticsTodoAndDragPresentation()
    {
        using var context = new TestContext();
        var id = Guid.NewGuid();
        var subroom = context.RenderComponent<SubroomTablePresentation>(p => p
            .Add(x => x.RoomId, Guid.NewGuid())
            .Add(x => x.View, new SubroomTableView([new(id, 0, false, DateTime.UtcNow, "duplicate", "duplicate", "notes", null, null, null, null, V2Severity.Danger, V2Severity.Danger, V2Severity.Danger)], [])));
        var check = context.RenderComponent<CheckTablePresentation>(p => p
            .Add(x => x.RoomId, Guid.NewGuid())
            .Add(x => x.View, new CheckTableView([Check(id, false)], [], ["subroom"])));
        var connection = context.RenderComponent<ConnectionTablePresentation>(p => p
            .Add(x => x.View, new ConnectionTableView([Connection(id, false)], [], ["subroom"])));

        var subroomGrip = Assert.Single(subroom.FindAll("table[data-v2-subroom-table='active'] button[data-v2-subroom-grip]"));
        var checkGrip = Assert.Single(check.FindAll("table[data-v2-check-table='active'] button[data-v2-check-grip]"));
        Assert.Equal("true", subroomGrip.GetAttribute("draggable"));
        Assert.Equal("true", checkGrip.GetAttribute("draggable"));
        Assert.NotEmpty(check.FindAll("table[data-v2-check-table='active'] .danger, table[data-v2-check-table='active'] .todo"));
        Assert.NotEmpty(connection.FindAll("table[data-v2-connection-table='active'] .danger, table[data-v2-connection-table='active'] .todo, table[data-v2-connection-table='active'] .connection-state i"));
    }

    [Fact]
    public void ActiveTransitionAndConnectionActions_RenderCompleteSlotsButtonsAndNoEmptyElementFrames()
    {
        using var context = new TestContext();
        var transitionId = Guid.NewGuid(); var connectionId = Guid.NewGuid();
        var transitionMetadata = 0; var transitionPlacement = 0; var transitionArchive = 0;
        var connectionScaffold = 0; var connectionArchive = 0;
        var transition = context.RenderComponent<TransitionTablePresentation>(p => p
            .Add(x => x.View, new TransitionTableView([Transition(transitionId, false)], [], ["room"], new Dictionary<Guid, IReadOnlyList<string>>()))
            .Add(x => x.SceneActionsAvailable, true)
            .Add(x => x.EditInGameDataRequested, (TransitionDurableBaseline _, string _) => { transitionMetadata++; return Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Unchanged)); })
            .Add(x => x.PlaceAnnotationRequested, (TransitionDurableBaseline _, string _) => { transitionPlacement++; return Task.CompletedTask; })
            .Add(x => x.SetArchive, (Guid _, bool _) => { transitionArchive++; return Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.ExpectedFailure)); }));
        var connection = context.RenderComponent<ConnectionTablePresentation>(p => p
            .Add(x => x.View, new ConnectionTableView([Connection(connectionId, false)], [], ["subroom"]))
            .Add(x => x.SceneActionsAvailable, true)
            .Add(x => x.Scaffold, (Guid _, string _) => { connectionScaffold++; return Task.FromResult(new V2ConnectionCommandOutcome(V2ConnectionCommandStatus.ExpectedFailure)); })
            .Add(x => x.SetArchive, (Guid _, bool _) => { connectionArchive++; return Task.FromResult(new V2ConnectionCommandOutcome(V2ConnectionCommandStatus.ExpectedFailure)); }));

        var transitionActions = transition.Find("tr[data-v2-transition-row] .row-actions");
        Assert.Equal(3, transitionActions.Children.Length);
        Assert.Equal(["edit in-game data", "re-arm placement", "archive"], transitionActions.QuerySelectorAll("button").Select(x => x.GetAttribute("aria-label")));
        Assert.All(transitionActions.QuerySelectorAll("button"), button => Assert.Null(button.GetAttribute("disabled")));
        transitionActions.QuerySelector("button[aria-label='edit in-game data']")!.Click();
        transitionActions.QuerySelector("button[aria-label='re-arm placement']")!.Click();
        transitionActions.QuerySelector("button[aria-label='archive']")!.Click();
        Assert.Equal(1, transitionMetadata); Assert.Equal(1, transitionPlacement); Assert.Equal(1, transitionArchive);

        var connectionActions = connection.Find("tr[data-v2-connection-row] .row-actions");
        Assert.Equal(3, connectionActions.Children.Length);
        Assert.Equal(["scaffold inverse connection", "Connection annotation is unavailable because this connection group is ineligible.", "archive"], connectionActions.QuerySelectorAll("button").Select(x => x.GetAttribute("aria-label")));
        Assert.Single(connectionActions.Children[1].QuerySelectorAll(".annotation-crosshairs-warning .fa-crosshairs"));
        Assert.Null(connectionActions.QuerySelector("button[aria-label='scaffold inverse connection']")!.GetAttribute("disabled"));
        Assert.True(connectionActions.QuerySelector("button[aria-label^='Connection annotation']")!.HasAttribute("disabled"));
        Assert.Null(connectionActions.QuerySelector("button[aria-label='archive']")!.GetAttribute("disabled"));
        connectionActions.QuerySelector("button[aria-label='scaffold inverse connection']")!.Click();
        connectionActions.QuerySelector("button[aria-label='archive']")!.Click();
        Assert.Equal(1, connectionScaffold); Assert.Equal(1, connectionArchive);

        Assert.All(transition.FindAll("*"), element => Assert.False(string.IsNullOrEmpty(element.TagName)));
        Assert.All(connection.FindAll("*"), element => Assert.False(string.IsNullOrEmpty(element.TagName)));
    }

    [Fact]
    public void V2RazorActionFragments_RejectEmptyWrapperSyntaxAndRenderNoEmptyTagName()
    {
        var root = FindSolutionRoot();
        var v2Razor = Directory.GetFiles(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2"), "*.razor", SearchOption.AllDirectories);
        Assert.All(v2Razor, path => Assert.DoesNotContain("@<>", File.ReadAllText(path), StringComparison.Ordinal));

        using var context = new TestContext();
        var transition = context.RenderComponent<TransitionTablePresentation>(p => p.Add(x => x.View, new TransitionTableView([Transition(Guid.NewGuid(), false)], [], [], new Dictionary<Guid, IReadOnlyList<string>>())));
        var connection = context.RenderComponent<ConnectionTablePresentation>(p => p.Add(x => x.View, new ConnectionTableView([Connection(Guid.NewGuid(), false)], [], [])));
        Assert.DoesNotContain("<></>", transition.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<></>", connection.Markup, StringComparison.Ordinal);
        Assert.All(transition.FindAll("*"), element => Assert.False(string.IsNullOrWhiteSpace(element.TagName)));
        Assert.All(connection.FindAll("*"), element => Assert.False(string.IsNullOrWhiteSpace(element.TagName)));
    }

    [Fact]
    public async Task TransitionAndConnectionWriteAdaptersUseTheSharedOwner()
    {
        using var context = new TestContext();
        var roomId = Guid.NewGuid(); var transitionId = Guid.NewGuid(); var connectionId = Guid.NewGuid();
        var focus = new List<(Guid RoomId, string Id)>();
        var transition = context.RenderComponent<TransitionTablePresentation>(p => p.Add(x => x.RoomId, roomId).Add(x => x.View, new TransitionTableView([Transition(transitionId, false)], [Transition(Guid.NewGuid(), true)], ["room"], new Dictionary<Guid, IReadOnlyList<string>>())).Add(x => x.ShowArchivedChildren, true).Add(x => x.FocusRequested, (Guid source, string id) => { focus.Add((source, id)); return Task.CompletedTask; }));
        var connection = context.RenderComponent<ConnectionTablePresentation>(p => p.Add(x => x.RoomId, roomId).Add(x => x.View, new ConnectionTableView([Connection(connectionId, false)], [Connection(Guid.NewGuid(), true)], ["subroom"])).Add(x => x.ShowArchivedChildren, true).Add(x => x.FocusRequested, (Guid source, string id) => { focus.Add((source, id)); return Task.CompletedTask; }));

        foreach (var item in new[] { (transition.Markup, "transition"), (connection.Markup, "connection") })
        {
            Assert.Contains($"data-v2-{item.Item2}-field", item.Markup);
            Assert.Contains($"data-v2-{item.Item2}-grip=\"true\"", item.Markup);
            Assert.Contains($"data-v2-{item.Item2}-tail=\"true\"", item.Markup);
            Assert.Contains("editor-row-", item.Markup);
        }
        Assert.Contains("data-v2-transition-field=\"todo\"", transition.Markup);
        Assert.Contains("data-v2-transition-field=\"verification\"", transition.Markup);
        Assert.All(connection.FindAll("input:not([type='checkbox']), textarea"), control => Assert.Null(control.GetAttribute("readonly")));
        Assert.All(transition.FindAll("input, textarea, select"), control => Assert.Null(control.GetAttribute("readonly")));
        Assert.All(transition.FindAll("input[type='checkbox'], select"), control => Assert.Null(control.GetAttribute("disabled")));
        Assert.All(connection.FindAll("input[type='checkbox'], select"), control => Assert.Null(control.GetAttribute("disabled")));
        var transitionClient = transition.Find("input[data-v2-transition-field='alias']").GetAttribute("data-v2-transition-client-row")!;
        var connectionClient = connection.Find("input[data-v2-connection-field='alias']").GetAttribute("data-v2-connection-client-row")!;
        Assert.True(await transition.Instance.NavigateTransitionAsync(transitionClient, "alias", "right"));
        Assert.True(await connection.Instance.NavigateConnectionAsync(connectionClient, "alias", "right"));
        Assert.Equal(2, focus.Count); Assert.All(focus, item => Assert.Equal(roomId, item.RoomId));
        Assert.False(await transition.Instance.NavigateTransitionAsync("not-a-guid", "alias", "right"));
        Assert.False(await connection.Instance.NavigateConnectionAsync(connectionClient, "invalid", "right"));
        transition.SetParametersAndRender(p => p.Add(x => x.RoomId, Guid.NewGuid()));
        connection.SetParametersAndRender(p => p.Add(x => x.RoomId, Guid.NewGuid()));
        Assert.False(await transition.Instance.NavigateTransitionAsync(transitionClient, "alias", "right"));
        Assert.False(await connection.Instance.NavigateConnectionAsync(connectionClient, "alias", "right"));
    }

    [Fact]
    public void ActiveTransitionTextInput_RetainsTypedDraftAndStableFieldContractAcrossUnchangedSameRoomReconciliation()
    {
        using var context = new TestContext();
        var roomId = Guid.NewGuid();
        var transitionId = Guid.NewGuid();
        var durable = Transition(transitionId, false);
        var table = context.RenderComponent<TransitionTablePresentation>(p => p
            .Add(x => x.RoomId, roomId)
            .Add(x => x.View, new TransitionTableView([durable], [], ["room"], new Dictionary<Guid, IReadOnlyList<string>>())));

        var input = table.Find("input[data-v2-transition-field='alias']");
        var clientDraftId = input.GetAttribute("data-v2-transition-client-row")!;
        var fieldId = input.Id;
        input.Input("typed alias");

        table.SetParametersAndRender(p => p
            .Add(x => x.RoomId, roomId)
            .Add(x => x.View, new TransitionTableView([durable], [], ["room"], new Dictionary<Guid, IReadOnlyList<string>>())));

        var retained = table.Find($"#{fieldId}");
        Assert.Equal($"editor-row-{clientDraftId}-alias", retained.Id);
        Assert.Equal(clientDraftId, retained.GetAttribute("data-v2-transition-client-row"));
        Assert.Equal("input", retained.TagName.ToLowerInvariant());
        Assert.Equal("typed alias", retained.GetAttribute("value"));
    }

    [Theory]
    [InlineData("requirements", "typed requirements")]
    [InlineData("notes", "typed notes")]
    public void ActiveExactOneInverseTransition_OnInputRetainsDownstreamTextareaIdentityWhenInverseBecomesPassive(string field, string typedValue)
    {
        using var context = new TestContext();
        var roomId = Guid.NewGuid();
        var transitionId = Guid.NewGuid();
        var transition = Transition(transitionId, false) with { InverseState = V2InverseState.One, ResolvedInverseRoomId = Guid.NewGuid() };
        var table = context.RenderComponent<TransitionTablePresentation>(p => p
            .Add(x => x.RoomId, roomId)
            .Add(x => x.View, new TransitionTableView([transition], [], ["room"], new Dictionary<Guid, IReadOnlyList<string>>())));

        var requirements = table.Find("textarea[data-v2-transition-field='requirements']");
        var notes = table.Find("textarea[data-v2-transition-field='notes']");
        var edited = field == "requirements" ? requirements : notes;
        var clientDraftId = edited.GetAttribute("data-v2-transition-client-row")!;
        var fieldId = edited.Id;
        Assert.Equal("BUTTON", table.Find($"#v2-transition-inverse-{transitionId}").TagName);

        edited.Input(typedValue);

        var retained = table.Find($"#{fieldId}");
        Assert.Same(edited.Unwrap(), retained.Unwrap());
        Assert.Same(requirements.Unwrap(), table.Find("textarea[data-v2-transition-field='requirements']").Unwrap());
        Assert.Same(notes.Unwrap(), table.Find("textarea[data-v2-transition-field='notes']").Unwrap());
        Assert.Equal("I", table.Find($"tr[data-v2-transition-row='{transitionId}'] .transition-inverse-state > *").TagName);
        Assert.Equal(typedValue, retained.GetAttribute("value"));
        Assert.Equal($"editor-row-{clientDraftId}-{field}", retained.Id);
        Assert.Equal(clientDraftId, retained.GetAttribute("data-v2-transition-client-row"));
    }

    [Fact]
    public void PageCreatesOneRootOwnerAndRegistersAllFourTypedTableCallbacks()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true);
        var roomId = Guid.NewGuid(); context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new HeaderLoader(View(roomId))); context.Services.AddSingleton<IRoomEditorV2CommandService>(new HeaderCommands());
        context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));
        var identifiers = context.JSInterop.Invocations.Select(x => x.Identifier).ToArray();
        Assert.Equal(1, identifiers.Count(x => x == "createV2ChildInteractionOwner"));
        Assert.Equal(4, identifiers.Count(x => x == "register"));
    }

    [Fact]
    public void V2ChildInteractionSource_HasOneRootLocalOwnerWithoutLegacyDocumentDispatcher()
    {
        var root = FindSolutionRoot();
        var editor = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "editor.js"));
        var page = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "RoomEditorV2Page.razor"));
        var transition = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "TransitionTablePresentation.razor"));
        var connection = File.ReadAllText(Path.Combine(root, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "ConnectionTablePresentation.razor"));

        Assert.Equal(1, CountOccurrences(editor, "window.createV2ChildInteractionOwner ="));
        Assert.Equal(1, CountOccurrences(page, "createV2ChildInteractionOwner"));
        Assert.Contains("root.addEventListener", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("document.addEventListener(\"keydown\", keydown)", editor, StringComparison.Ordinal);
        Assert.Contains("DropTransitionAsync", editor, StringComparison.Ordinal);
        Assert.Contains("DropConnectionAsync", editor, StringComparison.Ordinal);
        Assert.Contains("DropTransitionAsync", transition, StringComparison.Ordinal);
        Assert.Contains("DropConnectionAsync", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("registerEditorDraftNavigation", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("window.draftRowHasFocus", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void ModalRuntime_HostUsesAccessibleDialogFocusAndEscapeBackdropNoWriteCancellation()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose; context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true);
        var cancelled = 0;
        var modal = context.RenderComponent<V2PageModalHost>(p => p.Add(x => x.Content, new V2InverseSetupProposalView("source", "target")).Add(x => x.Stage, V2ModalRuntimeStage.ProposalOpen).Add(x => x.FocusRequested, true).Add(x => x.Cancel, EventCallback.Factory.Create(this, () => cancelled++)));
        var dialog = modal.Find("#v2-page-modal-dialog");
        Assert.Equal("dialog", dialog.GetAttribute("role")); Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        Assert.Equal("ProposalOpen", dialog.GetAttribute("data-v2-modal-stage"));
        Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "focusV2ModalDialog");
        modal.Find(".confirmation-backdrop.v2-page-modal-backdrop").Click(); Assert.Equal(1, cancelled);
        dialog.KeyDown(new KeyboardEventArgs { Key = "Escape" }); Assert.Equal(2, cancelled);
    }

    [Fact]
    public void ModalRuntime_StableFocusRoutePrefersPendingOnlyForCommittedAndOtherwiseInitiator()
    {
        Assert.Equal("pending", new V2ModalFocusRoute("initiator", "pending", V2ModalFocusOutcome.PendingTarget).TargetId);
        Assert.Equal("initiator", new V2ModalFocusRoute("initiator", "pending", V2ModalFocusOutcome.Initiator).TargetId);
        Assert.Equal("initiator", new V2ModalFocusRoute("initiator", "pending", V2ModalFocusOutcome.Correction).TargetId);
    }

    [Fact]
    public async Task ModalRuntime_PageIntents_GateHeaderAndApplicationNavigationAndRouteEveryFocusOutcomeWithoutCommands()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true); context.JSInterop.SetupVoid("focusEditorField", _ => true);
        var roomId = Guid.NewGuid(); var nextRoomId = Guid.NewGuid(); var loader = new HeaderLoader(View(roomId)); var commands = new HeaderCommands();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader); context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.InverseProposal, "initiator", "pending"))));
        page.WaitForAssertion(() =>
        {
            Assert.Equal("ProposalOpen", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage"));
            Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "focusV2ModalDialog");
        });
        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(nextRoomId)));
        var blockedSave = await page.InvokeAsync(() => page.Instance.RequestHeaderSaveTestIntentAsync());
        Assert.Equal(V2RoomHeaderCommandStatus.Unchanged, blockedSave.Status);
        Assert.Equal(0, commands.SaveCalls);
        page.Find("#v2-page-modal-dialog").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Equal(0, commands.SaveCalls);
        Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == "initiator");

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.PermanentDelete, "initiator"))));
        page.Find(".v2-page-modal-backdrop").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Equal(0, commands.SaveCalls);

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.InverseProposal, "initiator"))));
        page.FindAll("button").Single(x => x.TextContent == "revert change").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Equal(0, commands.SaveCalls);

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.InverseProposal, "initiator", "pending"))));
        await page.InvokeAsync(() => page.Instance.BeginModalTestCommitAsync()); await page.InvokeAsync(() => page.Instance.CompleteModalTestCommitAfterRefreshAsync());
        page.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == "pending"));

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.PermanentDelete, "initiator"))));
        await page.InvokeAsync(() => page.Instance.CompleteModalTestFailureAsync());
        page.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "focusEditorField" && (string)x.Arguments[0]! == "initiator"));

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.InverseProposal, "initiator"))));
        page.SetParametersAndRender(p => p.Add(x => x.RoomId, nextRoomId));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.True(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(roomId)));
        Assert.EndsWith($"/rooms/{roomId}", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModalRuntime_OpenModal_RejectsDeliveredSubroomAndCheckCreateCommandPaths()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var roomId = Guid.NewGuid(); var commands = new HeaderCommands();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new HeaderLoader(View(roomId)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));
        var subrooms = PrivateField<SubroomTablePresentation>(page.Instance, "subroomTable");
        var checks = PrivateField<CheckTablePresentation>(page.Instance, "checkTable");

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.InverseProposal, "subroom-initiator"))));
        var subroomName = page.Find("table[data-v2-subroom-table='active'] textarea[data-v2-subroom-field='name']");
        subroomName.Input("blocked subroom");
        await page.InvokeAsync(() => subrooms.BlurSubroomAsync(subroomName.GetAttribute("data-v2-subroom-client-row")!, "name", null));
        page.WaitForAssertion(() => Assert.Contains("Close the open dialog first.", page.Markup));
        Assert.Equal(0, commands.CreateSubroomCalls);
        page.Find("#v2-page-modal-dialog").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.InverseProposal, "check-initiator"))));
        var checkName = page.Find("table[data-v2-check-table='active'] textarea[data-v2-check-field='name']");
        checkName.Input("blocked check");
        await page.InvokeAsync(() => checks.BlurCheckAsync(checkName.GetAttribute("data-v2-check-client-row")!, "name", null));
        page.WaitForAssertion(() => Assert.Contains("Close the open dialog first.", page.Markup));
        Assert.Equal(0, commands.CreateCheckCalls);
    }

    [Theory]
    [InlineData("update inverse", "pending-target")]
    [InlineData("do not update inverse", null)]
    public async Task ModalRuntime_VisibleInverseCommitActions_FocusExactlyOnceAfterRefresh(string action, string? pendingTarget)
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var roomId = Guid.NewGuid(); var commands = new HeaderCommands();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new HeaderLoader(View(roomId)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.InverseProposal, "inverse-initiator", pendingTarget))));
        page.FindAll("button").Single(x => x.TextContent == action).Click();
        page.WaitForAssertion(() => Assert.Equal("Committing", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage")));
        await page.InvokeAsync(page.Instance.CompleteModalTestCommitAfterRefreshAsync);
        var expectedTarget = pendingTarget ?? "inverse-initiator";
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, expectedTarget)));
        Assert.Equal(1, FocusCallCount(context));
        Assert.Equal(0, commands.SaveCalls);
    }

    [Fact]
    public async Task TransitionInverseCommit_CommittingBlocksEveryDismissalAndSecondActionUntilItsSoleFocusRouteCompletes()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusEditorField", _ => true);
        var room = Guid.NewGuid(); var target = Guid.NewGuid(); var loader = new HeaderLoader(View(room)); var commands = new TransitionPageCommands(target) { ApplyPending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader); context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room));
        var draft = new TransitionDraft(Guid.NewGuid(), "out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "changed", false, null);
        var baseline = TransitionBaseline("old");

        Assert.Equal(V2TransitionCommandStatus.Proposal, (await page.InvokeAsync(() => page.Instance.SaveTransitionAsync(baseline, draft, "initiator", "pending"))).Status);
        page.FindAll("button").Single(x => x.TextContent == "update inverse").Click();
        page.WaitForAssertion(() => Assert.Equal("Committing", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage")));
        Assert.Equal(1, commands.ApplyInverseCalls);
        Assert.All(page.FindAll("#v2-page-modal-dialog button"), button => Assert.NotNull(button.GetAttribute("disabled")));

        page.Find("#v2-page-modal-dialog").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        page.Find(".v2-page-modal-backdrop").Click();
        foreach (var button in page.FindAll("#v2-page-modal-dialog button")) button.Click();
        Assert.Equal("Committing", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage"));
        Assert.Equal(1, commands.ApplyInverseCalls);
        Assert.Equal(0, FocusCallCount(context));

        commands.ApplyPending.SetResult(new(V2TransitionCommandStatus.Committed));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, "pending")));
        Assert.Equal(1, FocusCallCount(context));
    }

    [Fact]
    public async Task TransitionInverseEditRevert_TransfersTypedRetainedDraftToTheExistingTableBeforeInitiatorFocusWithoutAWrite()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusEditorField", _ => true);
        var room = Guid.NewGuid(); var target = Guid.NewGuid(); var loader = new HeaderLoader(View(room)); var commands = new TransitionPageCommands(target);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader); context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room));
        var draft = new TransitionDraft(Guid.NewGuid(), "changed", "Changed", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "typed", true, true);
        var baseline = TransitionBaseline("baseline");

        Assert.Equal(V2TransitionCommandStatus.Proposal, (await page.InvokeAsync(() => page.Instance.SaveTransitionAsync(baseline, draft, "initiator", "discarded"))).Status);
        page.FindAll("button").Single(x => x.TextContent == "revert change").Click();
        var table = PrivateField<TransitionTablePresentation>(page.Instance, "transitionTable");
        page.WaitForAssertion(() => Assert.Equal("baseline", table.RetainedDraftFor(draft.ClientDraftId)?.Notes));
        Assert.Equal(draft.ClientDraftId, table.RetainedDraftFor(draft.ClientDraftId)?.ClientDraftId);
        Assert.Equal("out", table.RetainedDraftFor(draft.ClientDraftId)?.Alias);
        Assert.Equal(1, commands.RevertInverseCalls);
        Assert.Equal(0, commands.ApplyInverseCalls);
        Assert.Equal(1, loader.LoadCount);
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, "initiator")));
    }

    [Fact]
    public async Task TransitionInverseCreateRevert_TransfersTheExactTypedDraftToTheUncommittedTableOwnerBeforeInitiatorFocusWithoutVisibleTableControls()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusEditorField", _ => true);
        var room = Guid.NewGuid(); var target = Guid.NewGuid(); var loader = new HeaderLoader(View(room)); var commands = new TransitionPageCommands(target);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader); context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room));
        var draft = new TransitionDraft(Guid.NewGuid(), "out", "Created", "game", 1, 2, 3, 4, 5, 6, 7, 8, "source", "target", "in", "requirements", "notes", true, false);

        Assert.Equal(V2TransitionCommandStatus.Proposal, (await page.InvokeAsync(() => page.Instance.CreateTransitionAsync(draft, "initiator", "discarded"))).Status);
        page.FindAll("button").Single(x => x.TextContent == "revert change").Click();
        var table = PrivateField<TransitionTablePresentation>(page.Instance, "transitionTable");
        page.WaitForAssertion(() => Assert.Equal(draft, table.RetainedDraftFor(draft.ClientDraftId)));
        Assert.Equal(1, commands.RevertCreateInverseCalls);
        Assert.Equal(0, commands.ApplyInverseCalls);
        Assert.Equal(1, loader.LoadCount);
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, "initiator")));
        var transitionPresentation = File.ReadAllText(Path.Combine(FindSolutionRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "TransitionTablePresentation.razor"));
        Assert.Contains("Create=", File.ReadAllText(Path.Combine(FindSolutionRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "RoomEditorV2Page.razor")));
        Assert.Contains("@onclick", transitionPresentation);
    }

    [Fact]
    public async Task TransitionInverseCreateRevert_ReappliesTheExactDraftToTheRenderedTailBeforeInitiatorFocusWithoutSqliteWrite()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var sourceRoom = new Room { FriendlyName = "Source", ReferenceId = "source", SortOrder = 0 };
        var targetRoom = new Room { FriendlyName = "Target", ReferenceId = "target", SortOrder = 1 };
        var target = new RoomTransition { RoomId = targetRoom.Id, Alias = "in", FriendlyName = "In", Requirements = "r", SortOrder = 0 };
        await using (var seed = fixture.CreateDbContext()) { seed.AddRange(sourceRoom, targetRoom, target); await seed.SaveChangesAsync(); }

        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusEditorField", _ => true);
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new RoomEditorV2LogicLoader(fixture));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, sourceRoom.Id));
        var tailAliasId = Assert.IsType<string>(page.Find("tr[data-v2-transition-tail='true'] input[aria-label='Transition alias']").Id);
        var clientDraftId = Guid.Parse(tailAliasId["editor-row-".Length..^"-alias".Length]);
        var draft = new TransitionDraft(clientDraftId, "out", "Created", "game", 1, 2, 3, 4, 5, 6, 7, 8, "source-subroom", "target", "in", "requirements", "notes", true, false);

        Assert.Equal(V2TransitionCommandStatus.Proposal, (await page.InvokeAsync(() => page.Instance.CreateTransitionAsync(draft, "initiator", "discarded"))).Status);
        page.FindAll("button").Single(x => x.TextContent == "revert change").Click();
        page.Render();

        page.WaitForAssertion(() =>
        {
            Assert.Equal("out", page.Find($"#editor-row-{draft.ClientDraftId}-alias").GetAttribute("value"));
            Assert.Equal("Created", page.Find($"#editor-row-{draft.ClientDraftId}-name").GetAttribute("value"));
            Assert.Equal("source-subroom", page.Find($"#editor-row-{draft.ClientDraftId}-source").GetAttribute("value"));
            Assert.Equal("target", page.Find($"#editor-row-{draft.ClientDraftId}-destination-room").GetAttribute("value"));
            Assert.Equal("in", page.Find($"#editor-row-{draft.ClientDraftId}-destination-alias").GetAttribute("value"));
            Assert.Equal("requirements", page.Find($"#editor-row-{draft.ClientDraftId}-requirements").GetAttribute("value"));
            Assert.Equal("notes", page.Find($"#editor-row-{draft.ClientDraftId}-notes").GetAttribute("value"));
            Assert.NotNull(page.Find($"#editor-row-{draft.ClientDraftId}-todo").GetAttribute("checked"));
            Assert.Equal("false", page.Find($"#editor-row-{draft.ClientDraftId}-verification").GetAttribute("value"));
            Assert.Single(page.FindAll($"#editor-row-{draft.ClientDraftId}-alias"));
            Assert.Equal(1, FocusCallCount(context, "initiator"));
        });
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(0, await verify.RoomTransitions.CountAsync(x => x.RoomId == sourceRoom.Id));
    }

    [Fact]
    public async Task ModalRuntime_RenderedPermanentDeleteCancel_ClosesProposalAndFocusesItsTriggerExactlyOnce()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var roomId = Guid.NewGuid();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new HeaderLoader(View(roomId)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new HeaderCommands());
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.PermanentDelete, "delete-subroom-trigger"))));
        Assert.Equal(V2ModalRuntimeStage.ProposalOpen, ModalStage(page.Instance));
        page.FindAll("button").Single(button => button.TextContent == "Cancel").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Null(ModalStage(page.Instance));
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, "delete-subroom-trigger")));
        Assert.Equal(1, FocusCallCount(context));
    }

    [Fact]
    public async Task ModalRuntime_RenderedInverseRevert_ClosesProposalAndFocusesInitiatorExactlyOnce()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var roomId = Guid.NewGuid();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new HeaderLoader(View(roomId)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new HeaderCommands());
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.InverseProposal, "inverse-revert-initiator", "discarded-pending-target"))));
        Assert.Equal("ProposalOpen", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage"));
        page.FindAll("button").Single(button => button.TextContent == "revert change").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Null(ModalStage(page.Instance));
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, "inverse-revert-initiator")));
        Assert.Equal(1, FocusCallCount(context));
    }

    [Fact]
    public async Task ModalRuntime_RenderedPermanentDeleteConfirm_CompletesCommitAndFocusesInitiatorExactlyOnce()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var roomId = Guid.NewGuid();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new HeaderLoader(View(roomId)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new HeaderCommands());
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.PermanentDelete, "permanent-delete-trigger"))));
        Assert.Equal("ProposalOpen", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage"));
        page.FindAll("button").Single(button => button.TextContent == "permanently delete").Click();
        page.WaitForAssertion(() => Assert.Equal("Committing", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage")));
        await page.InvokeAsync(page.Instance.CompleteModalTestCommitAfterRefreshAsync);

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Null(ModalStage(page.Instance));
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, "permanent-delete-trigger")));
        Assert.Equal(1, FocusCallCount(context));
    }

    [Theory]
    [InlineData("escape")]
    [InlineData("backdrop")]
    public async Task ModalRuntime_IsolatedNoWriteCancellation_ClosesProposalAndFocusesInitiatorExactlyOnce(string route)
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var roomId = Guid.NewGuid();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new HeaderLoader(View(roomId)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new HeaderCommands());
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));
        const string initiator = "cancel-initiator";

        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.InverseProposal, initiator, "discarded-pending-target"))));
        Assert.Equal("ProposalOpen", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage"));
        if (route == "escape") page.Find("#v2-page-modal-dialog").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        else page.Find(".v2-page-modal-backdrop").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Null(ModalStage(page.Instance));
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, initiator)));
        Assert.Equal(1, FocusCallCount(context));
    }

    [Fact]
    public async Task ModalRuntime_PageDisposal_ClearsAndRejectsFurtherRuntimeIntents()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var roomId = Guid.NewGuid(); context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new HeaderLoader(View(roomId))); context.Services.AddSingleton<IRoomEditorV2CommandService>(new HeaderCommands());
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, roomId));
        Assert.True(await page.InvokeAsync(() => page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.PermanentDelete, "initiator"))));
        await page.InvokeAsync(() => page.Instance.DisposeAsync());
        Assert.False(await page.Instance.OpenModalTestIntentAsync(new(V2ModalTestIntentKind.PermanentDelete, "initiator")));
        Assert.False(await page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task TransitionPageEntryPoints_RouteInverseAndArchivedDeleteThroughModalCommandsWithoutTableTriggers()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusV2ModalDialog", _ => true); context.JSInterop.SetupVoid("focusEditorField", _ => true);
        var room = Guid.NewGuid(); var target = Guid.NewGuid(); var loader = new HeaderLoader(View(room)); var commands = new TransitionPageCommands(target);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader); context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room));
        var draft = new TransitionDraft(Guid.NewGuid(), "out", "Out", null, null, null, null, null, null, null, null, null, null, "target", "in", "r", "n", false, null);
        var baseline = new TransitionDurableBaseline(Guid.NewGuid(), DateTime.UtcNow, 0, false, "out", "Out", null, null, null, null, null, null, null, null, null, null, "", "", "r", "", false, null);

        Assert.Equal(V2TransitionCommandStatus.Proposal, (await page.InvokeAsync(() => page.Instance.SaveTransitionAsync(baseline, draft, "source", "next"))).Status);
        Assert.Single(page.FindAll("#v2-page-modal-dialog"));
        page.FindAll("button").Single(x => x.TextContent == "update inverse").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Equal(1, commands.ApplyInverseCalls); Assert.Equal(2, loader.LoadCount); // initial load + exactly one committed refresh
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, "next")));

        Assert.Equal(V2TransitionCommandStatus.Proposal, (await page.InvokeAsync(() => page.Instance.RequestTransitionPermanentDeleteAsync(target, "delete"))).Status);
        page.FindAll("button").Single(x => x.TextContent == "permanently delete").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        Assert.Equal(1, commands.DeleteCalls); Assert.Equal(3, loader.LoadCount);
        Assert.DoesNotContain("Save=", File.ReadAllText(Path.Combine(FindSolutionRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "TransitionTablePresentation.razor")));
    }

    [Fact]
    public async Task ConnectionPageEntryPoints_RefreshCommittedCommandsFocusScaffoldAndRouteArchivedDelete()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("focusEditorField", _ => true);
        var room = Guid.NewGuid(); var created = Guid.NewGuid(); var loader = new HeaderLoader(View(room)); var commands = new ConnectionPageCommands(created);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(loader); context.Services.AddSingleton<IRoomEditorV2CommandService>(commands);
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room));
        var baseline = new ConnectionDurableBaseline(Guid.NewGuid(), DateTime.UtcNow, 0, false, "a", "Name", "source", "destination", "r", "n", false, null, null, false, null);
        var draft = new ConnectionDraft(Guid.NewGuid(), "a", "Name", "source", "destination", "r", "n", false, null, null, false, null);

        Assert.Equal(V2ConnectionCommandStatus.Committed, (await page.InvokeAsync(() => page.Instance.SaveConnectionAsync(baseline, draft))).Status);
        Assert.Equal(V2ConnectionCommandStatus.Unchanged, (await page.InvokeAsync(() => page.Instance.SaveConnectionAsync(baseline, draft))).Status);
        Assert.Equal(V2ConnectionCommandStatus.Committed, (await page.InvokeAsync(() => page.Instance.ScaffoldInverseConnectionAsync(baseline.EntityId, "scaffold-trigger"))).Status);
        page.Render();
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, "editor-row-" + PrivateField<ConnectionTablePresentation>(page.Instance, "connectionTable").FieldIdFor(created, "requirements").Split("editor-row-")[1])));

        Assert.Equal(V2ConnectionCommandStatus.ExpectedFailure, (await page.InvokeAsync(() => page.Instance.RequestConnectionPermanentDeleteAsync(baseline.EntityId, "connection-delete-trigger"))).Status);
        Assert.Single(page.FindAll("#v2-page-modal-dialog"));
        page.FindAll("button").Single(x => x.TextContent == "Cancel").Click();
        page.WaitForAssertion(() => Assert.Equal(1, FocusCallCount(context, "connection-delete-trigger")));
        Assert.Equal(V2ConnectionCommandStatus.ExpectedFailure, (await page.InvokeAsync(() => page.Instance.RequestConnectionPermanentDeleteAsync(baseline.EntityId, "connection-delete-trigger"))).Status);
        commands.DeleteStatus = V2ConnectionCommandStatus.ExpectedFailure;
        page.FindAll("button").Single(x => x.TextContent == "permanently delete").Click();
        page.WaitForAssertion(() => { Assert.Empty(page.FindAll("#v2-page-modal-dialog")); Assert.Equal(2, FocusCallCount(context, "connection-delete-trigger")); });
        commands.DeleteStatus = V2ConnectionCommandStatus.Committed;
        Assert.Equal(V2ConnectionCommandStatus.ExpectedFailure, (await page.InvokeAsync(() => page.Instance.RequestConnectionPermanentDeleteAsync(baseline.EntityId, "connection-delete-trigger"))).Status);
        page.FindAll("button").Single(x => x.TextContent == "permanently delete").Click();
        page.WaitForAssertion(() => Assert.Equal(2, commands.DeleteCalls));
        var connectionSource = File.ReadAllText(Path.Combine(FindSolutionRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "Components", "RoomEditorV2", "ConnectionTablePresentation.razor"));
        Assert.Contains("Func<ConnectionDurableBaseline,ConnectionDraft", connectionSource); Assert.Contains("PermanentDeleteRequested", connectionSource);
    }

    [Fact]
    public async Task SubroomAndCheckUncommittedSavingAndFailedRows_HaveNeitherGripNorDraggableAttribute()
    {
        using var context = new TestContext();
        var subroomCreate = new TaskCompletionSource<V2SubroomCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkCreate = new TaskCompletionSource<V2CheckCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subroom = context.RenderComponent<SubroomTablePresentation>(p => p
            .Add(x => x.RoomId, Guid.NewGuid())
            .Add(x => x.View, new SubroomTableView([], []))
            .Add(x => x.Create, _ => subroomCreate.Task));
        var check = context.RenderComponent<CheckTablePresentation>(p => p
            .Add(x => x.RoomId, Guid.NewGuid())
            .Add(x => x.View, new CheckTableView([], [], []))
            .Add(x => x.Create, _ => checkCreate.Task));

        AssertNoGripOrDraggable(subroom.Find("tr[data-v2-subroom-state='Uncommitted']").OuterHtml, "data-v2-subroom-grip");
        AssertNoGripOrDraggable(check.Find("tr[data-v2-check-state='Uncommitted']").OuterHtml, "data-v2-check-grip");

        var subroomTail = subroom.Find("textarea[data-v2-subroom-field='name']");
        var checkTail = check.Find("textarea[data-v2-check-field='name']");
        subroomTail.Input("draft subroom");
        checkTail.Input("draft check");
        var subroomSave = subroom.Instance.BlurSubroomAsync(subroomTail.GetAttribute("data-v2-subroom-client-row")!, "name", null);
        var checkSave = check.Instance.BlurCheckAsync(checkTail.GetAttribute("data-v2-check-client-row")!, "name", null);
        await subroom.InvokeAsync(() => RenderLifecycleState(subroom.Instance));
        await check.InvokeAsync(() => RenderLifecycleState(check.Instance));

        AssertNoGripOrDraggable(subroom.Find("tr[data-v2-subroom-state='Saving']").OuterHtml, "data-v2-subroom-grip");
        AssertNoGripOrDraggable(check.Find("tr[data-v2-check-state='Saving']").OuterHtml, "data-v2-check-grip");

        subroomCreate.SetResult(new(V2SubroomCommandStatus.ExpectedFailure, "expected failure"));
        checkCreate.SetResult(new(V2CheckCommandStatus.ExpectedFailure, "expected failure"));
        await Task.WhenAll(subroomSave, checkSave);

        subroom.WaitForAssertion(() => AssertNoGripOrDraggable(subroom.Find("tr[data-v2-subroom-state='Failed']").OuterHtml, "data-v2-subroom-grip"));
        check.WaitForAssertion(() => AssertNoGripOrDraggable(check.Find("tr[data-v2-check-state='Failed']").OuterHtml, "data-v2-check-grip"));
    }

    [Fact]
    public async Task TransitionTodoRequirementsWarning_CoversNonblankBlankUncheckAndArchivedRows()
    {
        using var context = new TestContext();
        var id = Guid.NewGuid();
        var nonblank = Transition(id, false) with { Requirements = "complete", RequirementsSeverity = V2Severity.Neutral, IsTodo = true };
        var table = context.RenderComponent<TransitionTablePresentation>(p => p.Add(x => x.View, new TransitionTableView([nonblank], [], [], new Dictionary<Guid, IReadOnlyList<string>>()))
            .Add(x => x.Save, (TransitionDurableBaseline _, TransitionDraft _, string _, string? _) => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Unchanged))));
        Assert.True(table.Find("[data-v2-transition-field='requirements']").ParentElement!.ClassList.Contains("warning"));

        var todo = table.Find("[data-v2-transition-field='todo']");
        await table.InvokeAsync(() => table.Instance.ControlTransitionAsync(todo.GetAttribute("data-v2-transition-client-row")!, "todo", "false"));
        table.SetParametersAndRender(p => p.Add(x => x.View, new TransitionTableView([nonblank with { IsTodo = false }], [], [], new Dictionary<Guid, IReadOnlyList<string>>())));
        Assert.False(table.Find("[data-v2-transition-field='requirements']").ParentElement!.ClassList.Contains("warning"));

        var blank = Transition(Guid.NewGuid(), false) with { Requirements = " ", RequirementsSeverity = V2Severity.Warning, IsTodo = true };
        table.SetParametersAndRender(p => p.Add(x => x.View, new TransitionTableView([blank], [], [], new Dictionary<Guid, IReadOnlyList<string>>())));
        Assert.True(table.Find("[data-v2-transition-field='requirements']").ParentElement!.ClassList.Contains("warning"));
        var blankTodo = table.Find("[data-v2-transition-field='todo']");
        await table.InvokeAsync(() => table.Instance.ControlTransitionAsync(blankTodo.GetAttribute("data-v2-transition-client-row")!, "todo", "false"));
        table.SetParametersAndRender(p => p.Add(x => x.View, new TransitionTableView([blank with { IsTodo = false }], [], [], new Dictionary<Guid, IReadOnlyList<string>>())));
        Assert.True(table.Find("[data-v2-transition-field='requirements']").ParentElement!.ClassList.Contains("warning"));

        var archived = Transition(Guid.NewGuid(), true) with { Requirements = "complete", RequirementsSeverity = V2Severity.Neutral, IsTodo = true };
        table.SetParametersAndRender(p => p.Add(x => x.ShowArchivedChildren, true).Add(x => x.View, new TransitionTableView([], [archived], [], new Dictionary<Guid, IReadOnlyList<string>>())));
        Assert.False(table.Find("table[data-v2-transition-table='archived'] [data-v2-transition-field='requirements']").ParentElement!.ClassList.Contains("warning"));
    }

    [Fact]
    public async Task ConnectionTodoRequirementsWarning_CoversNonblankBlankUncheckAndArchivedRows()
    {
        using var context = new TestContext();
        var id = Guid.NewGuid();
        var nonblank = Connection(id, false) with { Requirements = "complete", RequirementsSeverity = V2Severity.Neutral, IsTodo = true };
        var table = context.RenderComponent<ConnectionTablePresentation>(p => p.Add(x => x.View, new ConnectionTableView([nonblank], [], []))
            .Add(x => x.Save, (ConnectionDurableBaseline _, ConnectionDraft _, string _, string? _) => Task.FromResult(new V2ConnectionCommandOutcome(V2ConnectionCommandStatus.Unchanged))));
        Assert.True(table.Find("[data-v2-connection-field='requirements']").ParentElement!.ClassList.Contains("warning"));

        var todo = table.Find("[data-v2-connection-field='todo']");
        await table.InvokeAsync(() => table.Instance.ControlConnectionAsync(todo.GetAttribute("data-v2-connection-client-row")!, "todo", "false"));
        table.SetParametersAndRender(p => p.Add(x => x.View, new ConnectionTableView([nonblank with { IsTodo = false }], [], [])));
        Assert.False(table.Find("[data-v2-connection-field='requirements']").ParentElement!.ClassList.Contains("warning"));

        var blank = Connection(Guid.NewGuid(), false) with { Requirements = " ", RequirementsSeverity = V2Severity.Warning, IsTodo = true };
        table.SetParametersAndRender(p => p.Add(x => x.View, new ConnectionTableView([blank], [], [])));
        Assert.True(table.Find("[data-v2-connection-field='requirements']").ParentElement!.ClassList.Contains("warning"));
        var blankTodo = table.Find("[data-v2-connection-field='todo']");
        await table.InvokeAsync(() => table.Instance.ControlConnectionAsync(blankTodo.GetAttribute("data-v2-connection-client-row")!, "todo", "false"));
        table.SetParametersAndRender(p => p.Add(x => x.View, new ConnectionTableView([blank with { IsTodo = false }], [], [])));
        Assert.True(table.Find("[data-v2-connection-field='requirements']").ParentElement!.ClassList.Contains("warning"));

        var archived = Connection(Guid.NewGuid(), true) with { Requirements = "complete", RequirementsSeverity = V2Severity.Neutral, IsTodo = true };
        table.SetParametersAndRender(p => p.Add(x => x.ShowArchivedChildren, true).Add(x => x.View, new ConnectionTableView([], [archived], [])));
        Assert.False(table.Find("table[data-v2-connection-table='archived'] [data-v2-connection-field='requirements']").ParentElement!.ClassList.Contains("warning"));
    }

    [Fact]
    public async Task CheckTodoRequirementsWarning_CoversNonblankBlankUncheckAndArchivedRows()
    {
        using var context = new TestContext();
        var id = Guid.NewGuid();
        var nonblank = Check(id, false) with { Requirements = "complete", RequirementsSeverity = V2Severity.Neutral, IsTodo = true };
        var table = context.RenderComponent<CheckTablePresentation>(p => p.Add(x => x.View, new CheckTableView([nonblank], [], []))
            .Add(x => x.Save, (CheckDurableBaseline _, CheckDraft _) => Task.FromResult(new V2CheckCommandOutcome(V2CheckCommandStatus.Unchanged))));
        Assert.True(table.Find("[data-v2-check-field='requirements']").ParentElement!.ClassList.Contains("warning"));

        var todo = table.Find("[data-v2-check-field='todo']");
        await table.InvokeAsync(() => table.Instance.ControlCheckAsync(todo.GetAttribute("data-v2-check-client-row")!, "todo", "false"));
        table.SetParametersAndRender(p => p.Add(x => x.View, new CheckTableView([nonblank with { IsTodo = false }], [], [])));
        Assert.False(table.Find("[data-v2-check-field='requirements']").ParentElement!.ClassList.Contains("warning"));

        var blank = Check(Guid.NewGuid(), false) with { Requirements = " ", RequirementsSeverity = V2Severity.Warning, IsTodo = true };
        table.SetParametersAndRender(p => p.Add(x => x.View, new CheckTableView([blank], [], [])));
        Assert.True(table.Find("[data-v2-check-field='requirements']").ParentElement!.ClassList.Contains("warning"));
        var blankTodo = table.Find("[data-v2-check-field='todo']");
        await table.InvokeAsync(() => table.Instance.ControlCheckAsync(blankTodo.GetAttribute("data-v2-check-client-row")!, "todo", "false"));
        table.SetParametersAndRender(p => p.Add(x => x.View, new CheckTableView([blank with { IsTodo = false }], [], [])));
        Assert.True(table.Find("[data-v2-check-field='requirements']").ParentElement!.ClassList.Contains("warning"));

        var archived = Check(Guid.NewGuid(), true) with { Requirements = "complete", RequirementsSeverity = V2Severity.Neutral, IsTodo = true };
        table.SetParametersAndRender(p => p.Add(x => x.ShowArchivedChildren, true).Add(x => x.View, new CheckTableView([], [archived], [])));
        Assert.False(table.Find("table[data-v2-check-table='archived'] [data-v2-check-field='requirements']").ParentElement!.ClassList.Contains("warning"));
    }

    [Fact]
    public void RestoredSubroomAndTransitionRows_ReturnToTheirOrdinaryActivePresentation()
    {
        using var context = new TestContext();
        var id = Guid.NewGuid();
        var transition = context.RenderComponent<TransitionTablePresentation>(p => p
            .Add(x => x.ShowArchivedChildren, true)
            .Add(x => x.View, new TransitionTableView([], [Transition(id, true)], ["room"], new Dictionary<Guid, IReadOnlyList<string>> { [id] = ["a"] })));

        AssertArchivedMinimal(ArchivedPartition(transition.Markup, "data-v2-transition-table=\"archived\""));
        transition.SetParametersAndRender(p => p.Add(x => x.View, new TransitionTableView([Transition(id, false)], [], ["room"], new Dictionary<Guid, IReadOnlyList<string>> { [id] = ["a"] })));

        var activeTransition = ActivePartition(transition.Markup, "data-v2-transition-table=\"active\"");
        Assert.Contains("danger", activeTransition);
        Assert.Contains("checkbox-cell todo", activeTransition);
        Assert.Contains("transition-inverse-state\"><i", activeTransition);
        Assert.Contains("title=\"edit in-game data\"", activeTransition);
        Assert.Contains("title=\"archive\"", activeTransition);
        Assert.DoesNotContain("title=\"restore\"", activeTransition);
    }

    [Fact]
    public async Task ExplicitInverseStatusAction_IsIconOnlyAccessibleAndLeavesOtherStatesNoninteractive()
    {
        using var context = new TestContext();
        var id = Guid.NewGuid(); var calls = 0;
        var actionable = Transition(id, false) with { Alias = "out", FriendlyName = "Out", InverseState = V2InverseState.Zero, CanExplicitInverseSetup = true };
        var nonactionable = Transition(Guid.NewGuid(), false) with { InverseState = V2InverseState.One, CanExplicitInverseSetup = false };
        var table = context.RenderComponent<TransitionTablePresentation>(p => p.Add(x => x.View, new TransitionTableView([actionable, nonactionable], [], [], new Dictionary<Guid, IReadOnlyList<string>>())).Add(x => x.InverseStatusActionRequested, (TransitionDurableBaseline baseline, TransitionDraft draft, string initiator) => { calls++; return Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Proposal)); }));
        var button = table.Find($"#v2-transition-inverse-{id}");
        Assert.Equal("No inverse transition found; set up inverse", button.GetAttribute("title"));
        Assert.Equal("No inverse transition found; set up inverse", button.GetAttribute("aria-label"));
        Assert.Single(button.QuerySelectorAll("i")); Assert.Contains("fa-triangle-exclamation", button.InnerHtml);
        await button.ClickAsync(new()); Assert.Equal(1, calls);

        var inverseRoom = Guid.NewGuid(); var navigationCalls = 0;
        var neutral = Transition(Guid.NewGuid(), false) with { InverseState = V2InverseState.One, ResolvedInverseRoomId = inverseRoom };
        var navigation = context.RenderComponent<TransitionTablePresentation>(p => p.Add(x => x.View, new TransitionTableView([neutral], [], [], new Dictionary<Guid, IReadOnlyList<string>>())).Add(x => x.InverseRoomNavigationRequested, target => { navigationCalls++; Assert.Equal(inverseRoom, target); return Task.FromResult(true); }));
        var navigationIcon = navigation.Find($"#v2-transition-inverse-{neutral.EntityId}");
        Assert.Equal("One inverse transition found; open inverse room", navigationIcon.GetAttribute("aria-label"));
        Assert.Contains("fa-circle-check", navigationIcon.InnerHtml);
        await navigationIcon.ClickAsync(new()); Assert.Equal(1, navigationCalls);
        navigation.Find($"[data-v2-transition-row='{neutral.EntityId}'] [data-v2-transition-field='notes']").Input("dirty");
        Assert.Empty(navigation.FindAll($"#v2-transition-inverse-{neutral.EntityId}"));
        Assert.Contains("One inverse transition found", navigation.Markup);
    }

    [Fact]
    public void InverseColumnActions_UseCompactTableActionStylingWithoutChangingPassiveIndicators()
    {
        using var context = new TestContext();
        var setupId = Guid.NewGuid();
        var navigationId = Guid.NewGuid();
        var passiveId = Guid.NewGuid();
        var setup = Transition(setupId, false) with { InverseState = V2InverseState.Zero, CanExplicitInverseSetup = true };
        var navigation = Transition(navigationId, false) with { InverseState = V2InverseState.One, ResolvedInverseRoomId = Guid.NewGuid() };
        var passive = Transition(passiveId, false) with { InverseState = V2InverseState.Multiple };
        var table = context.RenderComponent<TransitionTablePresentation>(p => p.Add(x => x.View, new TransitionTableView([setup, navigation, passive], [], [], new Dictionary<Guid, IReadOnlyList<string>>())));

        Assert.Equal("BUTTON", table.Find($"#v2-transition-inverse-{setupId}").TagName);
        Assert.Equal("BUTTON", table.Find($"#v2-transition-inverse-{navigationId}").TagName);
        Assert.Equal("I", table.Find($"[data-v2-transition-row='{passiveId}'] .transition-inverse-state > *").TagName);

        var css = File.ReadAllText(Path.Combine(FindSolutionRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "app.css"));
        Assert.Contains(".transition-inverse-state > button { margin: 0; padding: 0; border: 0; background: transparent; cursor: pointer; }", css, StringComparison.Ordinal);
    }

    private static void AssertArchivedMinimal(string markup)
    {
        Assert.DoesNotContain("danger", markup);
        Assert.DoesNotContain("warning", markup);
        Assert.DoesNotContain("checkbox-cell todo", markup);
        Assert.DoesNotContain("reorder-handle", markup);
        Assert.DoesNotContain("draggable", markup);
        Assert.DoesNotContain("fa-grip-vertical", markup);
        Assert.Contains("title=\"restore\"", markup);
        Assert.Contains("title=\"delete permanently\"", markup);
        Assert.DoesNotContain("title=\"archive\"", markup);
        Assert.DoesNotContain("title=\"edit in-game data\"", markup);
        Assert.DoesNotContain("text-danger", markup);
    }

    private static void AssertNoGripOrDraggable(string rowMarkup, string gripAttribute)
    {
        Assert.DoesNotContain(gripAttribute, rowMarkup);
        Assert.DoesNotContain("draggable", rowMarkup);
        Assert.DoesNotContain("reorder-handle", rowMarkup);
        Assert.DoesNotContain("fa-grip-vertical", rowMarkup);
    }

    private static void RenderLifecycleState(ComponentBase component) =>
        typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null);

    private static string ArchivedPartition(string markup, string tableMarker)
    {
        var start = markup.IndexOf(tableMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Archived table marker {tableMarker} was not rendered.");
        return markup[start..];
    }

    private static string ActivePartition(string markup, string tableMarker)
    {
        var start = markup.IndexOf(tableMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Active table marker {tableMarker} was not rendered.");
        return markup[start..];
    }
    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length) count++;
        return count;
    }
    private static T PrivateField<T>(RoomEditorV2Page page, string name) where T : class =>
        Assert.IsType<T>(typeof(RoomEditorV2Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
    private static V2ModalRuntimeStage? ModalStage(RoomEditorV2Page page) =>
        ((V2ModalRuntimeState?)typeof(RoomEditorV2Page).GetField("modalState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page))?.Stage;
    private static int FocusCallCount(TestContext context, string? target = null) => context.JSInterop.Invocations.Count(x =>
        x.Identifier == "focusEditorField" && (target is null || string.Equals((string?)x.Arguments[0], target, StringComparison.Ordinal)));
    private static string FindSolutionRoot([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Silksong Rando Logic Manager.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the solution root.");
    }

    private static CheckRowView Check(Guid id, bool archived) => new(id, 0, archived, DateTime.UtcNow, "duplicate", "missing", "", "notes", true, true, null, null, null, null, null, null, null, null, null, null, true, V2Severity.Danger, V2Severity.Danger, V2Severity.Warning, V2Severity.Danger, V2Severity.Danger, V2ReferenceState.Unresolved);
    private static TransitionRowView Transition(Guid id, bool archived) => new(id, 0, archived, DateTime.UtcNow, "toolong", "duplicate", "missing", "missing room", "toolong", "", "notes", true, null, null, null, null, null, null, null, null, null, null, V2Severity.Danger, V2Severity.Danger, V2Severity.Danger, V2Severity.Danger, V2Severity.Danger, V2Severity.Warning, V2Severity.Danger, V2ReferenceState.Unresolved, V2ReferenceState.Unresolved, V2ReferenceState.Unresolved, V2InverseState.Multiple);
    private static ConnectionRowView Connection(Guid id, bool archived) => new(id, 0, archived, DateTime.UtcNow, "toolong", "duplicate", "missing", "missing", "", "notes", true, null, false, null, null, V2Severity.Danger, V2Severity.Danger, V2Severity.Danger, V2Severity.Danger, V2Severity.Warning, V2Severity.Danger, V2Severity.Danger, V2ReferenceState.Unresolved, V2ReferenceState.Unresolved, "ambiguous", true);
    private static TransitionDurableBaseline TransitionBaseline(string notes) => new(Guid.NewGuid(), DateTime.UtcNow, 0, false, "out", "Out", null, null, null, null, null, null, null, null, null, "", "old target", "old alias", "r", notes, false, null);
    private static RoomEditorV2View View(Guid id) => new(new RoomHeaderView(id, "Room", "reference", null, null, null, null, null, false, false, false, DateTime.UtcNow, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral), new([], []), new([], [], [], new Dictionary<Guid, IReadOnlyList<string>>()), new([], [], []), new([], [], []));
    private sealed class HeaderLoader(RoomEditorV2View view) : IRoomEditorV2LogicLoader { public int LoadCount { get; private set; } public Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken) { LoadCount++; return Task.FromResult<RoomEditorV2View?>(view); } }
    private sealed class MapHostLoader(RoomEditorV2View view) : IRoomEditorV2LogicLoader, IRoomEditorV2SceneSource { public int LoadCount { get; private set; } public CountingScene Scene { get; } = new(); ISceneLayoutLoader? IRoomEditorV2SceneSource.SceneLoader => Scene; public Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken) { LoadCount++; return Task.FromResult<RoomEditorV2View?>(view); } }
    private sealed class CountingScene : ISceneLayoutLoader
    {
        public int Count { get; private set; }
        public Task<SceneLayoutView?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult<SceneLayoutView?>(new(false, null, null, [], []));
        }
    }
    private sealed class ReferenceWorkflowLoader(RoomEditorV2View view) : IRoomEditorV2LogicLoader
    {
        public RoomEditorV2View View { get; set; } = view;
        public int LoadCount { get; private set; }
        public Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken) { LoadCount++; return Task.FromResult<RoomEditorV2View?>(View); }
    }
    private sealed class ReferenceWorkflowCommands(ReferenceWorkflowLoader loader) : IRoomEditorV2CommandService
    {
        public int PrepareCalls { get; private set; }
        public bool? UpdateReferences { get; private set; }
        public Task<V2RoomHeaderCommandOutcome> PrepareRoomReferenceRenameAsync(Guid roomId, RoomHeaderDurableBaseline baseline, RoomHeaderDraft draft)
        {
            PrepareCalls++;
            return Task.FromResult(new V2RoomHeaderCommandOutcome(V2RoomHeaderCommandStatus.Proposal, Proposal: new RoomReferenceRenameProposal(baseline, draft, [], [])));
        }
        public Task<V2RoomHeaderCommandOutcome> ApplyRoomReferenceRenameAsync(Guid roomId, RoomReferenceRenameProposal proposal, bool updateReferences)
        {
            UpdateReferences = updateReferences;
            loader.View = loader.View with { Header = loader.View.Header with { ReferenceId = proposal.SourceCurrent.ReferenceId, UpdatedUtc = loader.View.Header.UpdatedUtc.AddTicks(1) } };
            return Task.FromResult(new V2RoomHeaderCommandOutcome(V2RoomHeaderCommandStatus.Committed));
        }
        public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
    }
    private sealed class HeaderCommands : IRoomEditorV2CommandService
    {
        public TaskCompletionSource<V2RoomHeaderCommandOutcome> Pending { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveCalls { get; private set; } public int CreateSubroomCalls { get; private set; } public int CreateCheckCalls { get; private set; }
        public Task<V2RoomHeaderCommandOutcome> SaveRoomHeaderAsync(Guid roomId, RoomHeaderDurableBaseline baseline, RoomHeaderDraft draft) { SaveCalls++; return Pending.Task; }
        public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft) { CreateSubroomCalls++; return Task.FromResult(new V2SubroomCommandOutcome(V2SubroomCommandStatus.ExpectedFailure)); }
        public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
        public Task<V2CheckCommandOutcome> CreateCheckAsync(Guid roomId, CheckDraft draft) { CreateCheckCalls++; return Task.FromResult(new V2CheckCommandOutcome(V2CheckCommandStatus.ExpectedFailure)); }
    }
    private sealed class TransitionPageCommands(Guid target) : IRoomEditorV2CommandService
    {
        public int ApplyInverseCalls { get; private set; } public int RevertInverseCalls { get; private set; } public int RevertCreateInverseCalls { get; private set; } public int DeleteCalls { get; private set; }
        public TaskCompletionSource<V2TransitionCommandOutcome>? ApplyPending { get; set; }
        public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
        public Task<V2TransitionCommandOutcome> PrepareInverseAsync(Guid roomId, TransitionDurableBaseline baseline, TransitionDraft draft) => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Proposal, Proposal: new(baseline, draft, new(target, DateTime.UtcNow, null, null, true, true), "source", "out", "Out", "In")));
        public Task<V2TransitionCommandOutcome> CreateTransitionAsync(Guid roomId, TransitionDraft draft) => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Proposal, CreateProposal: new(draft, new(target, DateTime.UtcNow, null, null, true, true), "source", "out", "Out", "In")));
        public Task<V2TransitionCommandOutcome> ApplyInverseAsync(Guid roomId, TransitionInverseProposal proposal, bool updateInverse) { ApplyInverseCalls++; return ApplyPending?.Task ?? Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Committed)); }
        public V2TransitionCommandOutcome RevertInverse(TransitionInverseProposal proposal)
        {
            RevertInverseCalls++;
            var baseline = proposal.SourceBaseline;
            return new(V2TransitionCommandStatus.Unchanged, RetainedDraft: new(proposal.SourceCurrent.ClientDraftId, baseline.Alias, baseline.FriendlyName, baseline.InGameId, baseline.InGamePositionX, baseline.InGamePositionY, baseline.InGamePositionZ, baseline.LocalPositionX, baseline.LocalPositionY, baseline.LocalPositionZ, baseline.AnnotationSceneUnitX, baseline.AnnotationSceneUnitY, baseline.SourceSubroomReferenceText, baseline.DestinationRoomReferenceText, baseline.DestinationTransitionAliasText, baseline.Requirements, baseline.Notes, baseline.IsTodo, baseline.IsVerified));
        }
        public V2TransitionCommandOutcome RevertCreateInverse(TransitionInverseCreateProposal proposal) { RevertCreateInverseCalls++; return new(V2TransitionCommandStatus.Unchanged, RetainedDraft: proposal.SourceCurrent); }
        public Task<V2TransitionCommandOutcome> DeleteTransitionAsync(Guid roomId, Guid entityId) { DeleteCalls++; return Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Committed)); }
    }
    private sealed class ConnectionPageCommands(Guid created) : IRoomEditorV2CommandService
    {
        private int saveCalls;
        public int DeleteCalls { get; private set; }
        public V2ConnectionCommandStatus DeleteStatus { get; set; } = V2ConnectionCommandStatus.Committed;
        public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => throw new NotSupportedException();
        public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => throw new NotSupportedException();
        public Task<V2ConnectionCommandOutcome> SaveConnectionAsync(Guid roomId, ConnectionDurableBaseline baseline, ConnectionDraft draft)
            => Task.FromResult(new V2ConnectionCommandOutcome(++saveCalls == 1 ? V2ConnectionCommandStatus.Committed : V2ConnectionCommandStatus.Unchanged));
        public Task<V2ConnectionCommandOutcome> ScaffoldInverseConnectionAsync(Guid roomId, Guid entityId)
            => Task.FromResult(new V2ConnectionCommandOutcome(V2ConnectionCommandStatus.Committed, CreatedEntityId: created));
        public Task<V2ConnectionCommandOutcome> DeleteConnectionAsync(Guid roomId, Guid entityId) { DeleteCalls++; return Task.FromResult(new V2ConnectionCommandOutcome(DeleteStatus)); }
    }
}
