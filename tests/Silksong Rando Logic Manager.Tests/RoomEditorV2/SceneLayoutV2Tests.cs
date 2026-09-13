using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

public sealed class SceneLayoutV2Tests
{
    [Fact]
    public async Task ActualPageNudgeRoutes_CommitEachSceneItemWithOneRefreshOneSceneNoMapAndRestoreSelection()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 100, SceneUnitHeight = 100 };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "a", ReferenceId = "a", SceneUnitX = 1, SceneUnitY = 2, SceneUnitWidth = 3, SceneUnitHeight = 4 };
        var b = new Subroom { RoomId = room.Id, FriendlyName = "b", ReferenceId = "b", SortOrder = 1 };
        var exit = new RoomTransition { RoomId = room.Id, Alias = "e", FriendlyName = "exit", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 10, AnnotationSceneUnitY = 11 };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "check", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 12, AnnotationSceneUnitY = 13 };
        var connection = new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "connection", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", Requirements = "r", EnableAnnotation = true, SceneUnitX = 14, SceneUnitY = 15 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, a, b, exit, check, connection); await db.SaveChangesAsync(); await new LogicReferenceResolver(db).ResolveAsync(); }
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("loadRoomMapContextVisibility", _ => true).SetResult(true);
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader());
        context.Services.AddSingleton<MapRenderProjectionService>();
        context.Services.AddSingleton<MapOverlayAssetCatalog>();
        context.Services.AddSingleton<MapOverlayPlacementService>();
        context.Services.AddSingleton<DiagnosticState>();
        context.Services.AddSingleton<MapLinkService>();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new RoomEditorV2LogicLoader(fixture, new SceneLayoutLoader(fixture)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        var coordinator = (RoomEditorV2RefreshCoordinator)typeof(RoomEditorV2Page).GetField("refresh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page.Instance)!;
        var pane = page.FindComponent<SceneContextPanePresentation>();

        foreach (var (kind, id, x, y) in new[] { ("exit", exit.Id, 20d, 21d), ("check", check.Id, 22d, 23d), ("connection", connection.Id, 24d, 25d), ("subroom", a.Id, 26d, 27d) })
        {
            var refreshes = coordinator.OperationTrace.Events.Count;
            var scenes = coordinator.OperationTrace.SceneLoaderInvocations;
            var ownerGeneration = (long)typeof(SceneContextPanePresentation).GetField("ownerGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pane.Instance)!;
            await pane.InvokeAsync(() => pane.Instance.SelectSceneItemAsync(room.Id.ToString(), ownerGeneration, kind, id.ToString()));
            Assert.True(await pane.InvokeAsync(() => pane.Instance.CommitSceneNudgeAsync(kind, id.ToString(), x, y)));
            page.WaitForAssertion(() => Assert.Equal(refreshes + 3, coordinator.OperationTrace.Events.Count));
            Assert.Equal(scenes + 1, coordinator.OperationTrace.SceneLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations);
            Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "selectV2SceneLayoutItem");
        }

        var before = coordinator.OperationTrace.Events.Count;
        Assert.False(await pane.InvokeAsync(() => pane.Instance.CommitSceneNudgeAsync("exit", Guid.NewGuid().ToString(), 1, 1)));
        Assert.Equal(before, coordinator.OperationTrace.Events.Count);
    }
    [Fact]
    public async Task ScalarLoader_ProjectsOnlyUsableActiveGeometryAndOmitsUnpositionedAnnotations()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync(); var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 100, SceneUnitHeight = 50 };
        var archived = new RoomTransition { RoomId = room.Id, Alias = "old", FriendlyName = "Old", InGamePositionX = 4, InGamePositionY = 4, Requirements = "r", SortOrder = 1, IsArchived = true };
        var exit = new RoomTransition { RoomId = room.Id, Alias = "out", FriendlyName = "Exit", EnableAnnotation = true, InGamePositionX = 10, InGamePositionY = 12, AnnotationSceneUnitX = 20, AnnotationSceneUnitY = 30, Requirements = "r", SortOrder = 0 };
        var unpositionedExit = new RoomTransition { RoomId = room.Id, Alias = "none", FriendlyName = "Unpositioned exit", EnableAnnotation = true, InGamePositionX = 70, InGamePositionY = 71, Requirements = "r", SortOrder = 1 };
        var check = new CheckLocation { RoomId = room.Id, FriendlyName = "Check", EnableAnnotation = true, InGamePositionX = 2, InGamePositionY = 3, AnnotationSceneUnitX = 2, AnnotationSceneUnitY = 3, SortOrder = 0 };
        var unpositionedCheck = new CheckLocation { RoomId = room.Id, FriendlyName = "Unpositioned check", EnableAnnotation = true, InGamePositionX = 72, InGamePositionY = 73, AnnotationSceneUnitX = 1, SortOrder = 1 };
        var frame = new Subroom { RoomId = room.Id, FriendlyName = "Frame", ReferenceId = "f", SceneUnitX = 1, SceneUnitY = 2, SceneUnitWidth = 3, SceneUnitHeight = 4, SortOrder = 0 };
        var badFrame = new Subroom { RoomId = room.Id, FriendlyName = "Bad", ReferenceId = "b", SceneUnitX = 1, SceneUnitWidth = 3, SceneUnitHeight = 4, SortOrder = 1 };
        var connection = new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "Path", SourceSubroomReferenceText = "f", DestinationSubroomReferenceText = "b", EnableAnnotation = true, SceneUnitX = 7, SceneUnitY = 8, Requirements = "r", SortOrder = 0 };
        var unpositionedConnection = new SubroomConnection { RoomId = room.Id, Alias = "u", FriendlyName = "Unpositioned connection", SourceSubroomReferenceText = "b", DestinationSubroomReferenceText = "f", EnableAnnotation = true, Requirements = "r", SortOrder = 1 };
        var malformed = new SubroomConnection { RoomId = room.Id, Alias = "bad", FriendlyName = "Bad", SourceSubroomReferenceText = "f", DestinationSubroomReferenceText = "b", EnableAnnotation = true, SceneUnitX = 1, Requirements = "r", SortOrder = 1 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, archived, exit, unpositionedExit, check, unpositionedCheck, frame, badFrame, connection, unpositionedConnection, malformed); await db.SaveChangesAsync(); }
        var view = await new SceneLayoutLoader(fixture).LoadAsync(room.Id, CancellationToken.None);
        Assert.True(view!.HasValidBounds); Assert.Single(view.Frames); Assert.Equal("Frame", view.Frames[0].Label);
        Assert.Contains(view.Markers, x => x.Kind == "exit" && x.X == 20 && x.Y == 30 && x.Title == "Exit");
        Assert.Contains(view.Markers, x => x.Kind == "check" && x.X == 2 && x.Y == 3);
        Assert.Contains(view.Markers, x => x.Kind == "connection" && x.X == 7 && x.Y == 8);
        Assert.DoesNotContain(view.Markers, x => x.EntityId == unpositionedExit.Id || x.EntityId == unpositionedCheck.Id || x.EntityId == unpositionedConnection.Id);
        Assert.DoesNotContain(view.Markers, x => x.Label is "old" or "bad");
    }

    [Fact]
    public async Task ScalarLoader_CaptureContextReaderDrivesActualUnavailableReasonAndAbsentStaleDisplayBehavior()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 10, SceneUnitHeight = 10 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, new Subroom { RoomId = room.Id, FriendlyName = "Sub", ReferenceId = "sub" }, new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "Exit", Requirements = "r" }, new CheckLocation { RoomId = room.Id, FriendlyName = "Check", EnableAnnotation = true }, new SubroomConnection { RoomId = room.Id, Alias = "c", FriendlyName = "Path", Requirements = "r" }); await db.SaveChangesAsync(); }
        // This is behavior coverage, not merely a sixth-reader count: the scalar
        // capture context is what supplies the exact visible unavailable state.
        var capture = new ReaderCapture();
        var view = await new SceneLayoutLoader(new InspectingFactory(fixture.DatabasePath, capture)).LoadAsync(room.Id, CancellationToken.None);

        Assert.Equal(6, capture.Commands.Count);
        Assert.Equal(0, capture.EntityMaterializations);
        Assert.All(capture.Commands.Where(sql => !sql.Contains("Maps", StringComparison.Ordinal)), sql => Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capture.Commands, x => x.Contains("Rooms", StringComparison.Ordinal));
        Assert.Contains(capture.Commands, x => x.Contains("Subrooms", StringComparison.Ordinal));
        Assert.Contains(capture.Commands, x => x.Contains("RoomTransitions", StringComparison.Ordinal));
        Assert.Contains(capture.Commands, x => x.Contains("CheckLocations", StringComparison.Ordinal));
        Assert.Contains(capture.Commands, x => x.Contains("SubroomConnections", StringComparison.Ordinal));
        Assert.Contains(capture.Commands, x => x.Contains("Maps", StringComparison.Ordinal));
        Assert.NotNull(view);
        Assert.False(view!.Capture!.IsAvailable);
        Assert.Equal("Link this room to exactly one map before capturing a scene image.", view.Capture.AvailabilityReason);
        Assert.True(view.Capture.CanOpenMapLinks);
        Assert.False(view.Image!.IsAvailable);

        await using (var update = fixture.CreateDbContext())
        {
            var persisted = await update.Rooms.SingleAsync(x => x.Id == room.Id);
            persisted.SceneImageScaleXPercent = 100;
            persisted.SceneImageScaleYPercent = 100;
            persisted.SceneImagePanXPercent = 0;
            persisted.SceneImagePanYPercent = 0;
            persisted.IsSceneImageStale = true;
            await update.SaveChangesAsync();
        }
        var stale = await new SceneLayoutLoader(fixture).LoadAsync(room.Id, CancellationToken.None);
        Assert.True(stale!.Image!.HasTransform);
        Assert.True(stale.Image.IsStale);
        Assert.False(stale.Image.IsAvailable);
    }

    [Fact]
    public async Task ScalarLoader_CanvasOnlyRefreshDoesNotQueryMapsOrProjectCaptureContext()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 10, SceneUnitHeight = 10 };
        await using (var db = fixture.CreateDbContext()) { db.Add(room); await db.SaveChangesAsync(); }
        var capture = new ReaderCapture();

        var view = await new SceneLayoutLoader(new InspectingFactory(fixture.DatabasePath, capture)).LoadAsync(room.Id, false, CancellationToken.None);

        Assert.Equal(5, capture.Commands.Count);
        Assert.DoesNotContain(capture.Commands, sql => sql.Contains("Maps", StringComparison.Ordinal));
        Assert.Null(view!.Capture);
        Assert.True(view.HasValidBounds);
    }

    [Fact]
    public async Task MigrationCurrentSqliteCoordinator_CanvasOnlyRefreshRetainsUnavailableCaptureContextWithoutMapQuery_AndFullCaptureRefreshReplacesIt()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 10, SceneUnitHeight = 10 };
        var exit = new RoomTransition { RoomId = room.Id, Alias = "a", FriendlyName = "Before", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 1, AnnotationSceneUnitY = 2 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, exit); await db.SaveChangesAsync(); }
        var capture = new ReaderCapture(); var factory = new InspectingFactory(fixture.DatabasePath, capture);
        var loader = new RoomEditorV2LogicLoader(factory, new SceneLayoutLoader(factory));
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader);

        await coordinator.RefreshAsync(room.Id);
        var unavailable = Assert.IsType<SceneImageCaptureContextView>(coordinator.View!.Scene!.Capture);
        Assert.Equal("Link this room to exactly one map before capturing a scene image.", unavailable.AvailabilityReason);

        await using (var update = fixture.CreateDbContext())
        {
            var persisted = await update.RoomTransitions.SingleAsync(x => x.Id == exit.Id);
            persisted.FriendlyName = "Fresh canvas"; persisted.AnnotationSceneUnitX = 8;
            await update.SaveChangesAsync();
        }
        capture.Commands.Clear();
        await coordinator.RefreshAsync(room.Id, loadScene: true, loadCaptureContext: false);

        Assert.Equal(unavailable, coordinator.View!.Scene!.Capture);
        Assert.Contains(coordinator.View.Scene.Markers, marker => marker.Label == "a" && marker.X == 8);
        Assert.DoesNotContain(capture.Commands, sql => sql.Contains("Maps", StringComparison.Ordinal));

        await using (var update = fixture.CreateDbContext())
        {
            var persisted = await update.Rooms.SingleAsync(x => x.Id == room.Id);
            persisted.SceneUnitWidth = null; persisted.SceneUnitHeight = null;
            await update.SaveChangesAsync();
        }
        await coordinator.RefreshAsync(room.Id, loadScene: true, loadCaptureContext: true);

        var replaced = Assert.IsType<SceneImageCaptureContextView>(coordinator.View!.Scene!.Capture);
        Assert.NotSame(unavailable, replaced);
        Assert.Equal("Set valid scene dimensions before capturing a scene image.", replaced.AvailabilityReason);
    }

    [Fact]
    public void Canvas_RendersTypedYUpFramesAndMarkersWithoutSupersededPresentation()
    {
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var view = new SceneLayoutView(true, 100, 50, [new(Guid.NewGuid(), "Frame", "Frame", 1, 2, 3, 4)], [new(Guid.NewGuid(), "exit", "out", "Exit", 20, 30)]);
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, view));
        Assert.Single(cut.FindAll("svg[data-scene-layout-canvas='true']")); Assert.Single(cut.FindAll("[data-scene-layout-frame='true']")); Assert.Single(cut.FindAll("[data-scene-layout-marker='exit']"));
        Assert.Equal("-34", cut.Find("[data-scene-layout-marker='exit'] rect").GetAttribute("y"));
        Assert.True(cut.Markup.IndexOf("data-scene-layout-frame", StringComparison.Ordinal) < cut.Markup.IndexOf("data-scene-layout-marker", StringComparison.Ordinal));
        Assert.DoesNotContain("scene-layout-unplaced", cut.Markup); Assert.DoesNotContain("unplaced", cut.Markup, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("scene layout unavailable", cut.Markup);
    }

    [Fact]
    public async Task Pane_SelectedIdentityRetainsThroughPlacementCancellationWithoutFabricatedMarkerOrPersistenceIntent()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var id = Guid.NewGuid(); var cancelled = 0; var selection = new List<V2SceneSelectionCallback>();
        var request = new SceneAnnotationPlacementRequest("exit", id, "row-action");
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p
            .Add(x => x.View, new SceneLayoutView(true, 100, 50, [], []))
            .Add(x => x.SelectedItem, new V2SceneSelectedItem(V2SceneSelectedItemKind.Transition, id))
            .Add(x => x.Placement, request)
            .Add(x => x.SelectionChanged, value => { selection.Add(value); return Task.CompletedTask; })
            .Add(x => x.PlacementCancelled, (Guid _, long _, SceneAnnotationPlacementRequest value) => { cancelled++; return Task.FromResult(true); }));

        await cut.InvokeAsync(() => cut.Instance.CancelPlacementAsync());
        Assert.Equal(1, cancelled);
        Assert.Empty(selection); // cancel clears only the arm; no select/re-arm/dismiss persistence intent exists.
        Assert.Empty(cut.FindAll($"[data-scene-id='{id}']"));
        Assert.DoesNotContain("ghost", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pane_ReplacedOrDisposedOwnerRejectsEverySceneCallbackBeforeTransientOrCommandDelegate()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var room = Guid.NewGuid(); var item = Guid.NewGuid(); var request = new SceneAnnotationPlacementRequest("exit", item, "owner-proof");
        var selection = 0; var placement = 0; var cancellation = 0; var drag = 0; var nudge = 0; var frame = 0;
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.RoomId, room)
            .Add(x => x.View, new SceneLayoutView(true, 10, 10, [], []))
            .Add(x => x.Placement, request)
            .Add(x => x.SelectionChanged, _ => { selection++; return Task.CompletedTask; })
            .Add(x => x.PlacementCommitted, (Guid _, long _, SceneAnnotationPlacementRequest _, double _, double _) => { placement++; return Task.FromResult(true); })
            .Add(x => x.PlacementCancelled, (Guid _, long _, SceneAnnotationPlacementRequest _) => { cancellation++; return Task.FromResult(true); })
            .Add(x => x.MarkerDragCommitted, (Guid _, long _, string _, Guid _, double _, double _) => { drag++; return Task.FromResult(true); })
            .Add(x => x.SceneNudgeCommitted, (Guid _, long _, string _, Guid _, double _, double _) => { nudge++; return Task.FromResult(true); })
            .Add(x => x.FrameGeometryCommitted, (Guid _, long _, Guid _, double _, double _, double _, double _) => { frame++; return Task.FromResult(true); }));
        var generation = (long)typeof(SceneContextPanePresentation).GetField("ownerGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cut.Instance)!;

        // Model a stale same-room pane after replacement: all callback classes
        // reject before selection/arm mutation or any durable-command delegate.
        await cut.InvokeAsync(() => cut.Instance.SelectSceneItemAsync(room.ToString(), generation + 1, "exit", item.ToString()));
        await cut.InvokeAsync(() => cut.Instance.ClearSceneSelectionAsync(room.ToString(), generation + 1));
        await cut.InvokeAsync(() => cut.Instance.CancelPlacementAsync(room.ToString(), generation + 1));
        await cut.InvokeAsync(() => cut.Instance.CommitPlacementAsync(room.ToString(), generation + 1, "exit", item.ToString(), 1, 2));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.CommitMarkerDragAsync(room.ToString(), generation + 1, "exit", item.ToString(), 1, 2)));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.CommitSceneNudgeAsync(room.ToString(), generation + 1, "exit", item.ToString(), 1, 2)));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.CommitSubroomGeometryAsync(room.ToString(), generation + 1, item.ToString(), 1, 2, 3, 4)));
        Assert.Equal((0, 0, 0, 0, 0, 0), (selection, placement, cancellation, drag, nudge, frame));
        Assert.Equal(request, typeof(SceneContextPanePresentation).GetField("placement", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cut.Instance));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync());
        await cut.InvokeAsync(() => cut.Instance.SelectSceneItemAsync(room.ToString(), generation, "exit", item.ToString()));
        await cut.InvokeAsync(() => cut.Instance.ClearSceneSelectionAsync(room.ToString(), generation));
        await cut.InvokeAsync(() => cut.Instance.CancelPlacementAsync(room.ToString(), generation));
        await cut.InvokeAsync(() => cut.Instance.CommitPlacementAsync(room.ToString(), generation, "exit", item.ToString(), 1, 2));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.CommitMarkerDragAsync(room.ToString(), generation, "exit", item.ToString(), 1, 2)));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.CommitSceneNudgeAsync(room.ToString(), generation, "exit", item.ToString(), 1, 2)));
        Assert.False(await cut.InvokeAsync(() => cut.Instance.CommitSubroomGeometryAsync(room.ToString(), generation, item.ToString(), 1, 2, 3, 4)));
        Assert.Equal((0, 0, 0, 0, 0, 0), (selection, placement, cancellation, drag, nudge, frame));
    }

    [Fact]
    public async Task Page_RejectsDelayedOlderSameRoomOwnerAndItsSelectionCallbacksAfterReplacement()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 20, SceneUnitHeight = 20 };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "e", FriendlyName = "exit", Requirements = "r" };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition); await db.SaveChangesAsync(); }

        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("loadRoomMapContextVisibility", _ => true).SetResult(true);
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader()); context.Services.AddSingleton<MapRenderProjectionService>();
        context.Services.AddSingleton<MapOverlayAssetCatalog>(); context.Services.AddSingleton<MapOverlayPlacementService>();
        context.Services.AddSingleton<DiagnosticState>(); context.Services.AddSingleton<MapLinkService>();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new RoomEditorV2LogicLoader(fixture, new SceneLayoutLoader(fixture)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        var pane = page.FindComponent<SceneContextPanePresentation>();
        var generationField = typeof(RoomEditorV2Page).GetField("sceneOwnerGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var selectedField = typeof(RoomEditorV2Page).GetField("selectedSceneItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var placementField = typeof(RoomEditorV2Page).GetField("placementRequest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var oldGeneration = (long)generationField.GetValue(page.Instance)! + 1;
        var replacementGeneration = oldGeneration + 1;

        await pane.Instance.OwnerMounted!(room.Id, replacementGeneration);
        page.Find($"#v2-transition-place-{transition.Id}").Click();
        page.WaitForAssertion(() => Assert.Equal("true", page.Find("svg[data-scene-layout-canvas='true']").GetAttribute("data-scene-placement-armed")));
        var retainedSelection = Assert.IsType<V2SceneSelectedItem>(selectedField.GetValue(page.Instance));
        var retainedPlacement = Assert.IsType<SceneAnnotationPlacementRequest>(placementField.GetValue(page.Instance));
        var before = await ReadTransitionAsync(fixture, transition.Id);

        await pane.Instance.OwnerMounted!(room.Id, oldGeneration);
        await pane.Instance.ClearSceneSelectionAsync(room.Id.ToString(), oldGeneration);
        await pane.Instance.SelectSceneItemAsync(room.Id.ToString(), oldGeneration, "exit", Guid.NewGuid().ToString());

        Assert.Equal(replacementGeneration, (long)generationField.GetValue(page.Instance)!);
        Assert.Equal(retainedSelection, selectedField.GetValue(page.Instance));
        Assert.Equal(retainedPlacement, placementField.GetValue(page.Instance));
        Assert.Equal(before, await ReadTransitionAsync(fixture, transition.Id));
    }

    [Theory]
    [InlineData("marker-drag", false)]
    [InlineData("nudge", false)]
    [InlineData("retry", false)]
    [InlineData("marker-drag", true)]
    [InlineData("nudge", true)]
    [InlineData("retry", true)]
    public async Task MigrationCurrentSqlite_HeldSceneGateRejectsReplacedOrDisposedMarkerCallbacksBeforeCommandAdmission(string callbackKind, bool dispose)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 20, SceneUnitHeight = 20 };
        var transition = new RoomTransition { RoomId = room.Id, Alias = "e", FriendlyName = "exit", Requirements = "r", EnableAnnotation = true, AnnotationSceneUnitX = 2, AnnotationSceneUnitY = 3 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, transition); await db.SaveChangesAsync(); }

        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("loadRoomMapContextVisibility", _ => true).SetResult(true);
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader()); context.Services.AddSingleton<MapRenderProjectionService>();
        context.Services.AddSingleton<MapOverlayAssetCatalog>(); context.Services.AddSingleton<MapOverlayPlacementService>();
        context.Services.AddSingleton<DiagnosticState>(); context.Services.AddSingleton<MapLinkService>();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new RoomEditorV2LogicLoader(fixture, new SceneLayoutLoader(fixture)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        var page = context.RenderComponent<RoomEditorV2Page>(parameters => parameters.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        var pane = page.FindComponent<SceneContextPanePresentation>();
        var generation = (long)typeof(SceneContextPanePresentation).GetField("ownerGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pane.Instance)!;
        await pane.InvokeAsync(() => pane.Instance.SelectSceneItemAsync(room.Id.ToString(), generation, "exit", transition.Id.ToString()));
        var selectedField = typeof(RoomEditorV2Page).GetField("selectedSceneItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var placementField = typeof(RoomEditorV2Page).GetField("placementRequest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var armed = new SceneAnnotationPlacementRequest("exit", transition.Id, "held-gate");
        placementField.SetValue(page.Instance, armed);
        var trace = (RoomEditorV2RefreshCoordinator)typeof(RoomEditorV2Page).GetField("refresh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page.Instance)!;
        var eventsBefore = trace.OperationTrace.Events.ToArray();
        var durableBefore = await ReadTransitionAsync(fixture, transition.Id);
        var gate = (SemaphoreSlim)typeof(RoomEditorV2Page).GetField("commandGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page.Instance)!;

        await gate.WaitAsync();
        Task<bool> pending = callbackKind == "nudge"
            ? pane.Instance.CommitSceneNudgeAsync(room.Id.ToString(), generation, "exit", transition.Id.ToString(), 8, 9)
            // Retry uses the same typed marker callback with the exact captured drop.
            : pane.Instance.CommitMarkerDragAsync(room.Id.ToString(), generation, "exit", transition.Id.ToString(), 8, 9);

        if (dispose) await page.Instance.DisposeAsync();
        else await pane.Instance.OwnerMounted!(room.Id, generation + 1);
        var expectedSelected = selectedField.GetValue(page.Instance);
        var expectedPlacement = placementField.GetValue(page.Instance);
        gate.Release();

        Assert.False(await pending);
        Assert.Equal(eventsBefore, trace.OperationTrace.Events);
        Assert.Equal(durableBefore, await ReadTransitionAsync(fixture, transition.Id));
        Assert.Equal(expectedSelected, selectedField.GetValue(page.Instance));
        Assert.Equal(expectedPlacement, placementField.GetValue(page.Instance));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationCurrentSqlite_HeldSceneGateRejectsSubroomGeometryAfterSelectionClearsOrChanges(bool changeSelection)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "room", ReferenceId = "room", SceneUnitWidth = 20, SceneUnitHeight = 20 };
        var frame = new Subroom { RoomId = room.Id, FriendlyName = "frame", ReferenceId = "frame", SceneUnitX = 2, SceneUnitY = 3, SceneUnitWidth = 4, SceneUnitHeight = 5 };
        var replacement = new Subroom { RoomId = room.Id, FriendlyName = "replacement", ReferenceId = "replacement", SceneUnitX = 10, SceneUnitY = 11, SceneUnitWidth = 2, SceneUnitHeight = 3, SortOrder = 1 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, frame, replacement); await db.SaveChangesAsync(); }

        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("loadRoomMapContextVisibility", _ => true).SetResult(true);
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader()); context.Services.AddSingleton<MapRenderProjectionService>();
        context.Services.AddSingleton<MapOverlayAssetCatalog>(); context.Services.AddSingleton<MapOverlayPlacementService>();
        context.Services.AddSingleton<DiagnosticState>(); context.Services.AddSingleton<MapLinkService>();
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new RoomEditorV2LogicLoader(fixture, new SceneLayoutLoader(fixture)));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(new RoomEditorV2CommandService(new LogicCatalogService(fixture), fixture));
        var page = context.RenderComponent<RoomEditorV2Page>(parameters => parameters.Add(x => x.RoomId, room.Id));
        page.WaitForAssertion(() => Assert.NotNull(page.FindComponent<SceneContextPanePresentation>()));
        var pane = page.FindComponent<SceneContextPanePresentation>();
        var generation = (long)typeof(SceneContextPanePresentation).GetField("ownerGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(pane.Instance)!;
        var selectedField = typeof(RoomEditorV2Page).GetField("selectedSceneItem", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var placementField = typeof(RoomEditorV2Page).GetField("placementRequest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await pane.InvokeAsync(() => pane.Instance.SelectSceneItemAsync(room.Id.ToString(), generation, "subroom", frame.Id.ToString()));
        placementField.SetValue(page.Instance, new SceneAnnotationPlacementRequest("subroom", frame.Id, "held-geometry"));
        var trace = (RoomEditorV2RefreshCoordinator)typeof(RoomEditorV2Page).GetField("refresh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page.Instance)!;
        var eventsBefore = trace.OperationTrace.Events.ToArray();
        var durableBefore = await ReadSubroomAsync(fixture, frame.Id);
        var gate = (SemaphoreSlim)typeof(RoomEditorV2Page).GetField("commandGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page.Instance)!;

        await gate.WaitAsync();
        var pending = pane.Instance.CommitSubroomGeometryAsync(room.Id.ToString(), generation, frame.Id.ToString(), 8, 9, 4, 5);
        if (changeSelection)
            await pane.InvokeAsync(() => pane.Instance.SelectSceneItemAsync(room.Id.ToString(), generation, "subroom", replacement.Id.ToString()));
        else
            await pane.InvokeAsync(() => pane.Instance.ClearSceneSelectionAsync(room.Id.ToString(), generation));
        var expectedSelected = selectedField.GetValue(page.Instance);
        var expectedPlacement = placementField.GetValue(page.Instance);
        gate.Release();

        Assert.False(await pending);
        Assert.Equal(eventsBefore, trace.OperationTrace.Events);
        Assert.Equal(durableBefore, await ReadSubroomAsync(fixture, frame.Id));
        Assert.Equal(expectedSelected, selectedField.GetValue(page.Instance));
        Assert.Equal(expectedPlacement, placementField.GetValue(page.Instance));
    }

    [Fact]
    public void Pane_NonrenderableReplacementClearsPriorSvgSelectionWithoutClearingTypedIdentity()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var view = new SceneLayoutView(true, 100, 50, [], [new(a, "exit", "a", "A", 1, 2)]);
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.RoomId, Guid.NewGuid())
            .Add(x => x.View, view).Add(x => x.SelectedItem, new V2SceneSelectedItem(V2SceneSelectedItemKind.Transition, a)));
        var before = context.JSInterop.Invocations.Count;

        cut.SetParametersAndRender(p => p.Add(x => x.RoomId, cut.Instance.RoomId)
            .Add(x => x.View, view).Add(x => x.SelectedItem, new V2SceneSelectedItem(V2SceneSelectedItemKind.Transition, b)));

        Assert.Contains(context.JSInterop.Invocations.Skip(before), call => call.Identifier == "clearV2SceneLayoutSelection");
        Assert.DoesNotContain($"[data-scene-id='{b}']", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Canvas_RendersCurrentMarkerPrimitivesLabelsAndStatefulAnnotationToggle()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var view = new SceneLayoutView(true, 100, 50, [],
        [new(Guid.NewGuid(), "exit", "out", "Exit label", 20, 30), new(Guid.NewGuid(), "connection", "path", "Path label", 40, 20), new(Guid.NewGuid(), "check", "", "Check label", 60, 10)]);
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, view));

        foreach (var kind in new[] { "exit", "connection" })
        {
            var marker = cut.Find($"[data-scene-layout-marker='{kind}']");
            Assert.NotNull(marker.QuerySelector("rect"));
            var label = marker.QuerySelector("svg\\:text");
            Assert.NotNull(marker.QuerySelector("rect")); Assert.Equal(".8", marker.QuerySelector("rect")!.GetAttribute("rx"));
            Assert.NotNull(label); Assert.Equal("middle", label!.GetAttribute("text-anchor")); Assert.Equal("central", label.GetAttribute("dominant-baseline"));
            Assert.Equal(kind == "exit" ? "-30" : "-20", label.GetAttribute("y")); Assert.False(label.HasAttribute("transform"));
        }
        var check = cut.Find("[data-scene-layout-marker='check']");
        Assert.NotNull(check.QuerySelector("circle")); Assert.Null(check.QuerySelector("svg\\:text"));
        var toggle = cut.Find("button[data-scene-annotation-toggle='true']");
        Assert.Equal("true", toggle.GetAttribute("aria-pressed")); Assert.Equal("Hide annotations", toggle.GetAttribute("aria-label")); Assert.Equal("Hide annotations", toggle.GetAttribute("title"));
        Assert.True(toggle.QuerySelector("i")!.ClassList.Contains("fa-layer-group"));
        var css = ReadAppCss();
        Assert.Contains("[data-scene-layout-marker].selected > :not(text):not(title) { stroke: #0dcaf0; filter: drop-shadow(0 0 2px #0dcaf0); cursor: move; }", css);
    }

    [Fact]
    public async Task Canvas_ProjectsUprightLabelsAndOwnsExactSceneActionGroups()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var dimensions = 0;
        var view = new SceneLayoutView(true, 100, 50,
            [new(Guid.NewGuid(), "Frame", "Frame", 10, 20, 30, 10)],
            [new(Guid.NewGuid(), "exit", "out", "Exit", 20, 30), new(Guid.NewGuid(), "connection", "path", "Path", 40, 20)],
            new SceneImageDisplayView(true, false, true));
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, view)
            .Add(x => x.SceneDimensions, EventCallback.Factory.Create(this, () => dimensions++)));

        var frameLabel = cut.Find("[data-scene-layout-frame='true'] text");
        Assert.Equal("25", frameLabel.GetAttribute("x")); Assert.Equal("-25", frameLabel.GetAttribute("y")); Assert.Equal("middle", frameLabel.GetAttribute("text-anchor")); Assert.Equal("central", frameLabel.GetAttribute("dominant-baseline")); Assert.False(frameLabel.HasAttribute("transform"));
        Assert.Equal(new[] { "zone", "dimensions", "capture" }, cut.Find("[data-scene-actions-left='true']").Children.Select(ActionName).ToArray());
        Assert.Equal(new[] { "annotations", "image", "reset" }, cut.Find("[data-scene-actions-right='true']").Children.Select(ActionName).ToArray());
        var actionBox = cut.Find("[data-scene-annotation-action-box='true']");
        Assert.Equal(new[] { "rearm", "reset", "clear", "visibility", "dismiss" }, actionBox.Children.Select(x => x.GetAttribute("data-scene-action")).ToArray());
        Assert.All(actionBox.QuerySelectorAll("button"), button => { Assert.True(button.HasAttribute("disabled")); Assert.Contains("fa-fw", button.QuerySelector("i")!.ClassName); });
        var status = cut.Find("[data-scene-status-text='true']"); Assert.Equal(status.TextContent, status.GetAttribute("title"));
        var actionCss = ReadAppCss(); Assert.Contains(".scene-annotation-action-box { display: flex; flex: 0 0 auto; gap: .18rem; white-space: nowrap; }", actionCss); Assert.Contains(".scene-status-text { min-width: 0; flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }", actionCss);
        Assert.Empty(cut.FindAll("[data-scene-undo='true']")); Assert.Single(cut.FindAll("#room-scene-dimensions"));
        await cut.Find("#room-scene-dimensions").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()); Assert.Equal(1, dimensions);
    }

    private static string ActionName(AngleSharp.Dom.IElement element) =>
        element.Id == "room-scene-dimensions" ? "dimensions" :
        element.HasAttribute("data-scene-zone-toggle") ? "zone" :
        element.HasAttribute("data-scene-capture") ? "capture" :
        element.HasAttribute("data-scene-annotation-toggle") ? "annotations" :
        element.HasAttribute("data-scene-image-toggle") ? "image" :
        element.HasAttribute("data-scene-reset-view") ? "reset" : throw new InvalidOperationException("Unexpected scene action.");

    private static async Task<(DateTime UpdatedUtc, bool EnableAnnotation, double? X, double? Y)> ReadTransitionAsync(MigratedSqliteFixture fixture, Guid id)
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.RoomTransitions.SingleAsync(x => x.Id == id);
        return (row.UpdatedUtc, row.EnableAnnotation, row.AnnotationSceneUnitX, row.AnnotationSceneUnitY);
    }

    private static async Task<(DateTime UpdatedUtc, double? X, double? Y, double? Width, double? Height)> ReadSubroomAsync(MigratedSqliteFixture fixture, Guid id)
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.Subrooms.SingleAsync(x => x.Id == id);
        return (row.UpdatedUtc, row.SceneUnitX, row.SceneUnitY, row.SceneUnitWidth, row.SceneUnitHeight);
    }

    [Fact]
    public void Canvas_ProjectsAvailableImageAsLowestNoninteractiveOwnerControlledLayer()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var view = new SceneLayoutView(true, 100, 50,
            [new(Guid.NewGuid(), "Frame", "Frame", 1, 2, 3, 4)],
            [new(Guid.NewGuid(), "exit", "out", "Exit", 20, 30)],
            new SceneImageDisplayView(true, false, true));
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, view));

        var image = cut.Find("image[data-scene-layout-image='true']");
        Assert.Equal("none", image.GetAttribute("preserveAspectRatio"));
        Assert.True(cut.Markup.IndexOf("data-scene-layout-image", StringComparison.Ordinal) < cut.Markup.IndexOf("scene-layout-room-bounds", StringComparison.Ordinal));
        var toggle = cut.Find("button[data-scene-image-toggle='true']");
        Assert.Equal("true", toggle.GetAttribute("aria-pressed"));
        Assert.Equal("Hide scene image", toggle.GetAttribute("aria-label"));
        Assert.Equal("Hide scene image", toggle.GetAttribute("title"));
        Assert.DoesNotContain("@onclick=\"ToggleImage\"", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextSplitPane_SceneOnlyStateRemovesLegacyPaneAndDividerWithFullWidthScene()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = context.RenderComponent<RoomContextSplitPanePresentation>(p => p
            .Add(x => x.View, new RoomContextPresentationView(false))
            .Add(x => x.RoomId, Guid.NewGuid())
            .Add(x => x.Scene, new SceneLayoutView(true, 10, 10, [], [])));
        var host = cut.Find(".room-map-context");
        Assert.Contains("v2-scene-zone-preference-pending", host.ClassName);
        Assert.False(host.ClassList.Contains("with-zone-map"));
        Assert.True(host.ClassList.Contains("scene-layout-only"));
        Assert.Empty(cut.FindAll(".room-context-zone-pane"));
        Assert.Empty(cut.FindAll(".room-context-divider"));
        var css = ReadAppCss();
        Assert.Contains(".room-map-context.scene-layout-only > .room-context-zone-pane", css);
        Assert.Contains(".room-map-context.scene-layout-only > .room-context-divider { display: none; }", css);
        Assert.Contains(".room-map-context.scene-layout-only > .room-context-scene-pane { flex-basis: 100%; }", css);
        Assert.Contains(".room-map-context.v2-scene-zone-preference-pending { visibility: hidden; }", css);
    }

    [Fact]
    public async Task CaptureDialog_RendersCaptureAndRecaptureDraftsWithRetainedEstimateResetAndApplyTransferControls()
    {
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var changes = new List<V2SceneImageCaptureDialogView>(); var cancels = 0; var applies = 0;
        var initial = new V2SceneImageCaptureDialogView("Scene image capture is ready.", "25", "50", "-5", "10", true, "Apply capture", "25", "50", "-5", "10");
        context.JSInterop.Setup<V2SceneImageCapturePreviewValues>("readV2SceneImageCapturePreview", _ => true)
            .SetResult(new V2SceneImageCapturePreviewValues("88", "44", "-6", "11"));
        var preview = new V2SceneImageCapturePreviewView(100, 50, [], []);
        var cut = context.RenderComponent<V2SceneImageCaptureDialogContent>(p => p.Add(x => x.View, initial with { Preview = preview })
            .Add(x => x.DraftChanged, EventCallback.Factory.Create<V2SceneImageCaptureDialogView>(this, value => changes.Add(value)))
            .Add(x => x.Reset, EventCallback.Factory.Create(this, () => changes.Add(initial with { ScaleXPercent = "40", ScaleYPercent = "20", PanXPercent = "1", PanYPercent = "2" })))
            .Add(x => x.Cancel, EventCallback.Factory.Create(this, () => cancels++))
            .Add(x => x.Apply, EventCallback.Factory.Create(this, () => applies++)));
        Assert.Equal("capture scene image", cut.Find("h2").TextContent);
        Assert.Equal(new[] { "Scene image scale X", "Scene image scale Y", "Scene image pan X", "Scene image pan Y" }, cut.FindAll("input").Select(x => x.GetAttribute("aria-label")));
        cut.Find("input[aria-label='Scene image scale X']").Input("77");
        cut.FindAll("button").Single(x => x.TextContent.Trim() == "reset estimate").Click();
        cut.FindAll("button").Single(x => x.TextContent.Trim() == "cancel").Click();
        await cut.FindAll("button").Single(x => x.TextContent.Trim() == "apply").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.Equal(3, changes.Count);
        Assert.Equal("77", changes[0].ScaleXPercent); Assert.Equal("40", changes[1].ScaleXPercent);
        Assert.Equal(new V2SceneImageCapturePreviewValues("88", "44", "-6", "11"), new V2SceneImageCapturePreviewValues(changes[2].ScaleXPercent, changes[2].ScaleYPercent, changes[2].PanXPercent, changes[2].PanYPercent));
        Assert.Equal(1, cancels); Assert.Equal(1, applies);

        cut.SetParametersAndRender(p => p.Add(x => x.View, initial with { ApplyLabel = "Apply recapture" }).Add(x => x.Disabled, true));
        Assert.Equal("recapture scene image", cut.Find("h2").TextContent);
        Assert.Empty(cut.FindAll("button"));
        Assert.Equal("generating scene image...", cut.Find("[data-scene-image-generation-progress='true']").TextContent);
        Assert.All(cut.FindAll("input"), element => Assert.True(element.HasAttribute("disabled")));
    }

    [Fact]
    public void CaptureDialog_RendersTypedInteractivePreviewBelowSourceWithBoundsFramesAndMarkers()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var preview = new V2SceneImageCapturePreviewView(100, 50,
            [new(Guid.NewGuid(), "Frame", "Frame", 10, 20, 30, 10)],
            [new(Guid.NewGuid(), "exit", "out", "Exit", 20, 30), new(Guid.NewGuid(), "connection", "path", "Path", 40, 20), new(Guid.NewGuid(), "check", "", "Check", 60, 10)]);
        var view = new V2SceneImageCaptureDialogView("ready", "25", "50", "-5", "10", true, "Apply capture", "25", "50", "-5", "10", preview);
        var cut = context.RenderComponent<V2SceneImageCaptureDialogContent>(p => p.Add(x => x.View, view));

        var svg = cut.Find("svg[data-v2-scene-capture-preview='true']");
        Assert.Equal("Scene image capture preview", svg.GetAttribute("aria-label"));
        Assert.Single(cut.FindAll("image[data-v2-scene-capture-source='true']"));
        Assert.Single(cut.FindAll("[data-v2-scene-capture-bounds='true']"));
        Assert.Single(cut.FindAll("[data-v2-scene-capture-frame='true']"));
        Assert.Equal(3, cut.FindAll("[data-v2-scene-capture-marker]").Count);
        foreach (var kind in new[] { "exit", "connection" })
        {
            var marker = cut.Find($"[data-v2-scene-capture-marker='{kind}']");
            Assert.Equal(".8", marker.QuerySelector("rect")!.GetAttribute("rx"));
            Assert.Equal("central", marker.QuerySelector("svg\\:text")!.GetAttribute("dominant-baseline"));
        }
        Assert.Equal("central", cut.Find("[data-v2-scene-capture-frame='true'] svg\\:text").GetAttribute("dominant-baseline"));
        Assert.True(cut.Markup.IndexOf("data-v2-scene-capture-source", StringComparison.Ordinal) < cut.Markup.IndexOf("data-v2-scene-capture-bounds", StringComparison.Ordinal));
        Assert.Contains("Ctrl+Shift+drag: scale X/Y", cut.Markup);
        Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "initializeV2SceneImageCapturePreview");
        Assert.Equal("number", cut.Find("input").GetAttribute("type"));
        Assert.Equal("any", cut.Find("input").GetAttribute("step"));
        Assert.Contains("scene-image-capture-modal", ReadAppCss());
    }

    [Fact]
    public void Canvas_AlwaysRendersOneCanvasAndUsesUnifiedUnavailableStatus()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var valid = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, new SceneLayoutView(true, 10, 20, [], [])));
        var invalid = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, new SceneLayoutView(false, null, null, [], [])));
        var missing = context.RenderComponent<SceneContextPanePresentation>();

        Assert.Single(valid.FindAll("svg[data-scene-layout-canvas='true']"));
        Assert.Single(invalid.FindAll("svg[data-scene-layout-canvas='true']"));
        Assert.Single(missing.FindAll("svg[data-scene-layout-canvas='true']"));
        Assert.Equal("scene dimensions unavailable", invalid.Find("[data-scene-status-text='true']").TextContent);
        Assert.Equal("scene dimensions unavailable", missing.Find("[data-scene-status-text='true']").TextContent);
    }

    [Fact]
    public void Canvas_UnavailableDimensionsStatusOverridesActionDisablementReason_AndRetainsMarkerRetryControl()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var actionBox = new V2SceneActionBoxView(false, false, false, false, false,
            "Scene annotation actions are unavailable because scene dimensions are unavailable.",
            "reset annotation to game position", "clear annotation geometry", "show annotation", "dismiss scene interaction", false,
            "Scene annotation actions are unavailable because scene dimensions are unavailable.");
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p
            .Add(x => x.View, new SceneLayoutView(false, null, null, [], []))
            .Add(x => x.ActionBox, actionBox));

        Assert.Equal("scene dimensions unavailable", cut.Find("[data-scene-status-text='true']").TextContent);
        var retry = cut.Find("button[data-scene-marker-retry='true']");
        Assert.True(retry.HasAttribute("hidden"));
        Assert.Equal("retry marker save", retry.TextContent.Trim());
        Assert.Empty(cut.Find("[data-scene-annotation-action-box='true']").QuerySelectorAll("[data-scene-marker-retry='true']"));
    }

    [Fact]
    public void Canvas_ConsolidatesEverySceneWarningIntoItsSingleStatusRegionAndVersionsRefreshedImage()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var unavailable = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View,
            new SceneLayoutView(true, 10, 20, [], [], new SceneImageDisplayView(true, false, false, 123), new SceneImageCaptureContextView(false, "Link this room to exactly one map before capturing a scene image.", null, true))));
        Assert.Single(unavailable.FindAll(".scene-layout-status"));
        Assert.Contains("Link this room", unavailable.Find("[data-scene-status-text='true']").TextContent);
        Assert.Empty(unavailable.FindAll("[data-scene-status='true'] [data-scene-capture-map-links='true']"));
        Assert.Empty(unavailable.FindAll(".scene-image-capture-status, .scene-image-stale"));
        Assert.Empty(unavailable.Find(".scene-layout-actions").QuerySelectorAll("span, p"));

        var stale = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View,
            new SceneLayoutView(true, 10, 20, [], [], new SceneImageDisplayView(true, true, false, 123))));
        Assert.Equal("needs recapture", stale.Find("[data-scene-status-text='true']").TextContent);
        Assert.Empty(stale.FindAll(".scene-image-capture-status, .scene-image-stale"));

        var available = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.RoomId, Guid.Parse("11111111-1111-1111-1111-111111111111")).Add(x => x.View,
            new SceneLayoutView(true, 10, 20, [], [], new SceneImageDisplayView(true, false, true, 456))));
        Assert.EndsWith(".webp?v=456", available.Find("[data-scene-layout-image='true']").GetAttribute("href"));
    }

    [Fact]
    public void Canvas_UsesLocalViewportContractForVisibleAnnotationsAndResetWithoutServerGestureCallbacks()
    {
        using var context = new TestContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var view = new SceneLayoutView(true, 100, 50, [new(Guid.NewGuid(), "Frame", "Frame", 1, 2, 3, 4)], [new(Guid.NewGuid(), "exit", "out", "Exit", 20, 30)]);
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, view));

        var canvas = cut.Find("svg[data-scene-layout-canvas='true']");
        Assert.NotNull(canvas.GetAttribute("data-scene-initial-viewbox"));
        Assert.Single(cut.FindAll("[data-scene-annotations='true'] [data-scene-layout-frame='true']"));
        Assert.Single(cut.FindAll("button[data-scene-annotation-toggle='true']"));
        Assert.Single(cut.FindAll("button[data-scene-reset-view='true']"));
        Assert.DoesNotContain("@onclick", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("invokeMethodAsync", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canvas_RoomNavigationChangesThePaneLocalViewportKeyForFreshShownResetState()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var view = new SceneLayoutView(true, 10, 10, [], [new(Guid.NewGuid(), "check", "c", "check", 2, 3)]);
        var firstRoom = Guid.NewGuid(); var secondRoom = Guid.NewGuid();
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, view).Add(x => x.RoomId, firstRoom));
        var firstKey = cut.Find("svg[data-scene-layout-canvas='true']").GetAttribute("data-scene-viewport-key");
        cut.SetParametersAndRender(p => p.Add(x => x.View, view).Add(x => x.RoomId, secondRoom));
        var secondKey = cut.Find("svg[data-scene-layout-canvas='true']").GetAttribute("data-scene-viewport-key");

        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal(2, context.JSInterop.Invocations.Count(x => x.Identifier == "initializeV2SceneViewport"));
    }

    [Fact]
    public async Task Canvas_StaticPrerenderOrUninitializedDisposal_DoesNotCallViewportCleanup()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        // Static prerender has no OnAfterRenderAsync lifecycle; this is that same
        // uninitialized component state with a recording JS runtime.
        var cut = context.RenderComponent<SceneContextPanePresentation>();

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync());

        Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "initializeV2SceneViewport"));
        Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "disposeV2SceneViewport"));
    }

    [Fact]
    public async Task Canvas_InteractiveInitializedDisposal_CleansViewportExactlyOnce()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var view = new SceneLayoutView(false, null, null, [], []);
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, view));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync());

        Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "initializeV2SceneViewport"));
        Assert.Equal(1, context.JSInterop.Invocations.Count(x => x.Identifier == "disposeV2SceneViewport"));
    }

    [Fact]
    public void Canvas_RendersBrowserOnlySelectionMetadataWithoutHaloOrInspector()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var frameId = Guid.NewGuid(); var markerId = Guid.NewGuid();
        var view = new SceneLayoutView(true, 10, 10, [new(frameId, "Frame", "Frame", 1.25, 2.5, 3.75, 4.125)], [new(markerId, "exit", "a", "Exit", 6.25, 7.5)]);
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, view));

        Assert.Equal("subroom", cut.Find($"[data-scene-id='{frameId}']").GetAttribute("data-scene-selection-kind"));
        Assert.Equal("exit", cut.Find($"[data-scene-id='{markerId}']").GetAttribute("data-scene-selection-kind"));
        Assert.Null(cut.Find($"[data-scene-id='{frameId}']").GetAttribute("data-scene-selection-geometry"));
        Assert.Empty(cut.FindAll("[data-scene-selection-inspector='true']"));
        Assert.Empty(cut.FindAll(".scene-layout-marker-halo"));
        Assert.DoesNotContain("@onclick", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Canvas_HasNoV2UndoMarkupOrJsInvokableCallback()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View,
            new SceneLayoutView(true, 10, 10, [], [new(Guid.NewGuid(), "exit", "a", "Exit", 2, 3)])));
        Assert.Empty(cut.FindAll("[data-scene-undo='true']"));
        Assert.Null(typeof(SceneContextPanePresentation).GetMethod("CommitSceneUndoAsync"));
        Assert.DoesNotContain("undoV2SceneLayout", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "editor.js")), StringComparison.Ordinal);
    }

    [Fact]
    public void Canvas_RendersExplicitSelectedFrameInteriorFourEdgesAndFourCorners()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var frameId = Guid.NewGuid();
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View,
            new SceneLayoutView(true, 20, 20, [new(frameId, "Frame", "Frame", 2, 3, 8, 6)], [])));

        var frame = cut.Find($"[data-scene-id='{frameId}']");
        Assert.Equal("move", frame.QuerySelector("[data-scene-frame-target='move']")!.GetAttribute("data-scene-frame-target"));
        Assert.Equal(4, frame.QuerySelectorAll(".scene-layout-subroom-edge[data-scene-frame-target]").Length);
        Assert.Equal(4, frame.QuerySelectorAll(".scene-layout-subroom-corner[data-scene-frame-target]").Length);
        Assert.Equal(new[] { "b", "l", "r", "t" }, frame.QuerySelectorAll(".scene-layout-subroom-edge").Select(x => x.GetAttribute("data-scene-frame-target")).Order());
        Assert.Equal(new[] { "bl", "br", "tl", "tr" }, frame.QuerySelectorAll(".scene-layout-subroom-corner").Select(x => x.GetAttribute("data-scene-frame-target")).Order());
    }

    [Fact]
    public void Canvas_SnapFeedbackSelectorsRelateRenderedFrameAndRoomEdges()
    {
        var css = ReadAppCss();
        Assert.Contains(".scene-layout-snap-guide", css);
        Assert.Contains(".scene-layout-snap-target-edge", css);

        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View,
            new SceneLayoutView(true, 20, 20, [new(Guid.NewGuid(), "Frame", "Frame", 2, 3, 8, 6)], [])));
        var canvas = cut.Find("svg[data-scene-layout-canvas='true']");
        Assert.Equal(new[] { "b", "l", "r", "t" }, canvas.QuerySelectorAll(".scene-layout-room-bounds-edge[data-scene-snap-room-edge]").Select(x => x.GetAttribute("data-scene-snap-room-edge")).Order());
        Assert.All(canvas.QuerySelectorAll("[data-scene-layout-frame='true'] .scene-layout-subroom-edge"), edge => Assert.NotNull(edge.GetAttribute("data-scene-frame-target")));
    }

    [Fact]
    public void SelectedFrameTargets_ResolveToMoveAndMatchingResizeCursorRulesTheRenderedMarkupActuallyMatches()
    {
        var css = ReadAppCss();
        Assert.Contains(SelectedInteriorMoveCursorRule, css);
        Assert.Contains(".edge-n, .edge-s { cursor: ns-resize; }", css);
        Assert.Contains(".edge-e, .edge-w { cursor: ew-resize; }", css);
        Assert.Contains(".corner-nw, .corner-se { cursor: nwse-resize; }", css);
        Assert.Contains(".corner-ne, .corner-sw { cursor: nesw-resize; }", css);

        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var frameId = Guid.NewGuid();
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View,
            new SceneLayoutView(true, 20, 20, [new(frameId, "Frame", "Frame", 2, 3, 8, 6)], [])));
        var canvas = cut.Find("svg[data-scene-layout-canvas='true']");
        var frame = cut.Find($"[data-scene-id='{frameId}']");
        // The browser interaction owner applies `selected`; nothing else changes.
        frame.ClassList.Add("selected");

        var interior = canvas.QuerySelector(SelectedInteriorMoveCursorSelector);
        Assert.NotNull(interior);
        Assert.Equal("move", interior.GetAttribute("data-scene-frame-target"));
        Assert.Same(frame.QuerySelector("[data-scene-frame-target='move']"), interior);
        foreach (var (selector, target) in new[] { (".edge-n", "t"), (".edge-s", "b"), (".edge-e", "r"), (".edge-w", "l"), (".corner-nw", "tl"), (".corner-ne", "tr"), (".corner-sw", "bl"), (".corner-se", "br") })
            Assert.Equal(target, frame.QuerySelector(selector)!.GetAttribute("data-scene-frame-target"));
    }

    [Fact]
    public void UnselectedFrameInterior_KeepsTheSelectFirstPointerCursorBecauseTheMoveRuleIsSelectedScoped()
    {
        var css = ReadAppCss();
        Assert.Contains(".scene-layout-subroom { cursor: pointer; }", css);
        Assert.Contains(".scene-layout-subroom-body { fill: rgb(180 185 190 / 42%); stroke: #f8f9fa; stroke-width: 2px; vector-effect: non-scaling-stroke; rx: 2px; }", css);
        Assert.Contains(SelectedInteriorMoveCursorSelector,
            css.Split('\n').Where(x => x.Contains("cursor: move", StringComparison.Ordinal)).Select(x => x[..x.IndexOf('{', StringComparison.Ordinal)].Trim()));

        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var frameId = Guid.NewGuid();
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View,
            new SceneLayoutView(true, 20, 20, [new(frameId, "Frame", "Frame", 2, 3, 8, 6)], [])));
        var canvas = cut.Find("svg[data-scene-layout-canvas='true']");

        Assert.DoesNotContain("selected", cut.Find($"[data-scene-id='{frameId}']").GetAttribute("class"));
        Assert.Null(canvas.QuerySelector(SelectedInteriorMoveCursorSelector));
        Assert.NotNull(canvas.QuerySelector(".scene-layout-subroom [data-scene-frame-target='move']"));
    }

    [Fact]
    public async Task Canvas_RowArmedPlacement_RendersCrosshairInstruction_AndRoutesOnlyClickOrCancelIntent()
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var id = Guid.NewGuid(); var request = new SceneAnnotationPlacementRequest("check", id, "v2-check-place-test");
        var committed = new List<(double X, double Y)>(); var cancelled = new List<SceneAnnotationPlacementRequest>();
        var view = new SceneLayoutView(true, 10, 10, [], []);
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p.Add(x => x.View, view).Add(x => x.Placement, request)
            .Add(x => x.PlacementCommitted, (Guid _, long _, SceneAnnotationPlacementRequest r, double x, double y) => { committed.Add((x, y)); return Task.FromResult(r == request); })
            .Add(x => x.PlacementCancelled, (Guid _, long _, SceneAnnotationPlacementRequest r) => { cancelled.Add(r); return Task.FromResult(true); }));

        Assert.Single(cut.FindAll("[data-scene-status='true']"));
        Assert.Equal("true", cut.Find("svg[data-scene-layout-canvas='true']").GetAttribute("data-scene-placement-armed"));
        Assert.Empty(committed); Assert.Empty(cancelled);
        await cut.InvokeAsync(() => cut.Instance.CancelPlacementAsync());
        Assert.Single(cancelled); Assert.Empty(committed);
        cut.SetParametersAndRender(p => p.Add(x => x.View, view).Add(x => x.Placement, request)
            .Add(x => x.PlacementCommitted, (Guid _, long _, SceneAnnotationPlacementRequest r, double x, double y) => { committed.Add((x, y)); return Task.FromResult(true); }));
        await cut.InvokeAsync(() => cut.Instance.CommitPlacementAsync("check", id.ToString(), 2.5, 3.75));
        Assert.Equal((2.5d, 3.75d), Assert.Single(committed));
    }

    [Theory]
    [InlineData("check", "place check annotation; click the scene, or press Escape to cancel")]
    [InlineData("subroom", "draw subroom rectangle; drag to the opposite corner, or press Escape to cancel")]
    public void Canvas_ArmedPlacement_RendersExactUnifiedStatusInstruction(string kind, string expected)
    {
        using var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = context.RenderComponent<SceneContextPanePresentation>(p => p
            .Add(x => x.View, new SceneLayoutView(true, 10, 10, [], []))
            .Add(x => x.Placement, new SceneAnnotationPlacementRequest(kind, Guid.NewGuid(), "status-proof")));

        Assert.Equal(expected, cut.Find("[data-scene-status-text='true']").TextContent);
    }

    [Fact]
    public async Task Coordinator_NavigationAndRelevantRefreshLoadSceneOnce_OrdinaryRefreshDoesNot()
    {
        var room = Guid.NewGuid(); var scene = new CountingSceneLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(new FixedLoader(room), scene);
        await coordinator.RefreshAsync(room); Assert.Equal(1, scene.Count);
        await coordinator.CommitTransitionCommandAsync(room, "ordinary", () => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Committed))); Assert.Equal(1, scene.Count);
        await coordinator.CommitTransitionCommandAsync(room, "metadata", () => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Committed)), true); Assert.Equal(2, scene.Count);
        Assert.Equal(2, coordinator.OperationTrace.SceneLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations);
    }

    [Fact]
    public async Task Coordinator_CommittedTransitionAndCheckPlacement_RefreshOnceLoadsSceneOnceAndNeverLoadsMap()
    {
        var room = Guid.NewGuid(); var scene = new CountingSceneLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(new FixedLoader(room), scene);
        await coordinator.RefreshAsync(room);
        await coordinator.CommitTransitionCommandAsync(room, "transition-annotation-placement", () => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Committed)), true);
        await coordinator.CommitCheckCommandAsync(room, "check-annotation-placement", () => Task.FromResult(new V2CheckCommandOutcome(V2CheckCommandStatus.Committed)), true);
        Assert.Equal(3, scene.Count); Assert.Equal(3, coordinator.OperationTrace.SceneLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations);
        Assert.Equal(2, coordinator.OperationTrace.Events.Count(x => x.EndsWith(":complete-room-refresh", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Coordinator_CommittedSubroomGeometry_RefreshesOnceLoadsSceneOnceAndNeverLoadsMap()
    {
        var room = Guid.NewGuid(); var scene = new CountingSceneLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(new FixedLoader(room), scene);
        await coordinator.RefreshAsync(room);
        var result = await coordinator.CommitSubroomCommandAsync(room, "subroom-geometry",
            () => Task.FromResult(new V2SubroomCommandOutcome(V2SubroomCommandStatus.Committed)), SceneRefreshImpact.SubroomGeometry);

        Assert.Equal(V2SubroomCommandStatus.Committed, result.Status);
        Assert.Equal(2, scene.Count); Assert.Equal(2, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations);
        Assert.Single(coordinator.OperationTrace.Events, x => x == "subroom-geometry:complete-room-refresh");
    }

    [Fact]
    public async Task Coordinator_RefreshMatrixLoadsCaptureContextOnlyForDimensionsAndApply()
    {
        var room = Guid.NewGuid(); var scene = new CaptureTrackingSceneLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(new FixedLoader(room), scene);
        await coordinator.RefreshAsync(room); // Initial mount supplies capture availability.
        await coordinator.CommitSubroomCommandAsync(room, "subroom-geometry", () => Task.FromResult(new V2SubroomCommandOutcome(V2SubroomCommandStatus.Committed)), true);
        await coordinator.CommitRoomSceneDimensionsCommandAsync(room, "room-scene-dimensions", () => Task.FromResult(new V2RoomSceneDimensionsCommandOutcome(V2RoomSceneDimensionsCommandStatus.Committed)));
        await coordinator.CommitSceneImageCommandAsync(room, "scene-image-capture", () => Task.FromResult(new V2SceneImageCaptureCommandOutcome(V2SceneImageCaptureCommandStatus.Committed)));

        Assert.Equal([true, false, true, true], scene.CaptureRequests);
        Assert.Equal(4, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations);
    }

    [Fact]
    public async Task Coordinator_CaptureContextMatrix_UsesMapBoundInputOnlyForDimensionsMapChangesAndApply_NotCanvasOrUnrelatedLogic()
    {
        var room = Guid.NewGuid(); var scene = new CaptureTrackingSceneLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(new FixedLoader(room), scene);

        await coordinator.RefreshAsync(room);                 // mount
        await coordinator.RefreshAsync(room, true, true);     // map-link
        await coordinator.RefreshAsync(room, true, true);     // chunk
        await coordinator.RefreshAsync(room, true, true);     // overlay
        await coordinator.CommitRoomSceneDimensionsCommandAsync(room, "room-scene-dimensions", () => Task.FromResult(new V2RoomSceneDimensionsCommandOutcome(V2RoomSceneDimensionsCommandStatus.Committed)));
        await coordinator.CommitSceneImageCommandAsync(room, "scene-image-capture", () => Task.FromResult(new V2SceneImageCaptureCommandOutcome(V2SceneImageCaptureCommandStatus.Committed)));
        await coordinator.CommitTransitionCommandAsync(room, "unrelated-transition-notes", () => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Committed)));
        await coordinator.CommitSubroomCommandAsync(room, "canvas-frame", () => Task.FromResult(new V2SubroomCommandOutcome(V2SubroomCommandStatus.Committed)), true);

        Assert.Equal([true, true, true, true, true, true, false], scene.CaptureRequests);
        Assert.Equal(7, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations);
        Assert.DoesNotContain(coordinator.OperationTrace.Events, x => x.Contains("map", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompleteSceneRefresh_UsesOneSqliteSnapshotWhenAnInterveningWriteCommits()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "before", ReferenceId = "room", SceneUnitWidth = 10, SceneUnitHeight = 10 };
        await using (var db = fixture.CreateDbContext()) { db.Add(room); await db.SaveChangesAsync(); }
        await using (var writer = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            await writer.OpenAsync(); await using var pragma = writer.CreateCommand(); pragma.CommandText = "PRAGMA journal_mode=WAL;"; await pragma.ExecuteNonQueryAsync();
        }

        var capture = new InterveningWriteCapture(fixture.DatabasePath);
        var factory = new InspectingFactory(fixture.DatabasePath, capture);
        var loader = new RoomEditorV2LogicLoader(factory, new SceneLayoutLoader(factory));
        var view = await ((IRoomEditorV2SceneBatchLoader)loader).LoadAsync(room.Id, true, CancellationToken.None);

        Assert.Equal("before", view!.Header.FriendlyName);
        Assert.Equal(10, view.Scene!.SceneUnitWidth);
        await capture.Writer;
        Assert.True(capture.WriteCommitted);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("after", (await verify.Rooms.SingleAsync(x => x.Id == room.Id)).FriendlyName);
    }

    [Fact]
    public async Task ScalarLoader_RendersOnlyValidOneOrExactReverseTwoRowPathwaysAndEnabledChecks()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 10, SceneUnitHeight = 10 };
        var a = new Subroom { RoomId = room.Id, FriendlyName = "A", ReferenceId = "a" };
        var b = new Subroom { RoomId = room.Id, FriendlyName = "B", ReferenceId = "b" };
        var validForward = new SubroomConnection { RoomId = room.Id, Alias = "v", FriendlyName = "Valid", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", EnableAnnotation = true, SceneUnitX = 1, SceneUnitY = 2, Requirements = "r" };
        var validReverse = new SubroomConnection { RoomId = room.Id, Alias = "v", FriendlyName = "Valid", SourceSubroomReferenceText = "b", DestinationSubroomReferenceText = "a", EnableAnnotation = true, SceneUnitX = 1, SceneUnitY = 2, Requirements = "r" };
        var malformed = new SubroomConnection { RoomId = room.Id, Alias = "x", FriendlyName = "Bad", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", EnableAnnotation = true, SceneUnitX = 3, SceneUnitY = 4, Requirements = "r" };
        var duplicateDirection = new SubroomConnection { RoomId = room.Id, Alias = "x", FriendlyName = "Bad", SourceSubroomReferenceText = "a", DestinationSubroomReferenceText = "b", EnableAnnotation = true, SceneUnitX = 3, SceneUnitY = 4, Requirements = "r" };
        var disabled = new CheckLocation { RoomId = room.Id, FriendlyName = "Disabled", EnableAnnotation = false, InGamePositionX = 5, InGamePositionY = 5 };
        await using (var db = fixture.CreateDbContext()) { db.AddRange(room, a, b, validForward, validReverse, malformed, duplicateDirection, disabled); await db.SaveChangesAsync(); }

        var view = await new SceneLayoutLoader(fixture).LoadAsync(room.Id, CancellationToken.None);
        Assert.Single(view!.Markers, x => x.Kind == "connection");
        Assert.Equal("v", view.Markers.Single(x => x.Kind == "connection").Label);
        Assert.DoesNotContain(view.Markers, x => x.Label is "x" or "Disabled");
    }

    private const string SelectedInteriorMoveCursorSelector = ".scene-layout-subroom.selected .scene-layout-subroom-body";
    private const string SelectedInteriorMoveCursorRule = SelectedInteriorMoveCursorSelector + " { stroke: #0dcaf0; filter: drop-shadow(0 0 2px #0dcaf0); cursor: move; }";

    private static string ReadAppCss()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Silksong Rando Logic Manager.slnx")))
                return File.ReadAllText(Path.Combine(current.FullName, "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "app.css"));
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Silksong Rando Logic Manager.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class CountingSceneLoader : ISceneLayoutLoader { public int Count { get; private set; } public Task<SceneLayoutView?> LoadAsync(Guid id, CancellationToken token) { Count++; return Task.FromResult<SceneLayoutView?>(new(false, null, null, [], [])); } }
    private sealed class CaptureTrackingSceneLoader : ISceneLayoutLoader
    {
        public List<bool> CaptureRequests { get; } = [];
        public Task<SceneLayoutView?> LoadAsync(Guid id, CancellationToken token) => LoadAsync(id, true, token);
        public Task<SceneLayoutView?> LoadAsync(Guid id, bool capture, CancellationToken token) { CaptureRequests.Add(capture); return Task.FromResult<SceneLayoutView?>(new(false, null, null, [], [])); }
    }
    private sealed class FixedLoader(Guid id) : IRoomEditorV2LogicLoader { public Task<RoomEditorV2View?> LoadAsync(Guid _, CancellationToken token) => Task.FromResult<RoomEditorV2View?>(new(new(id, "r", "r", null, null, null, null, null, false, false, false, DateTime.UtcNow, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral), new([], []), new([], [], [], new Dictionary<Guid, IReadOnlyList<string>>()), new([], [], []), new([], [], []))); }
    private sealed class InspectingFactory(string path, DbCommandInterceptor capture) : IDbContextFactory<LogicDbContext>
    {
        public LogicDbContext CreateDbContext() => new(new DbContextOptionsBuilder<LogicDbContext>().UseSqlite($"Data Source={path}").AddInterceptors(capture).Options);
        public Task<LogicDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class ReaderCapture : DbCommandInterceptor, IMaterializationInterceptor
    {
        public List<string> Commands { get; } = []; public int EntityMaterializations { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity) { EntityMaterializations++; return entity; }
    }

    private sealed class InterveningWriteCapture(string path) : DbCommandInterceptor
    {
        private int readerCount;
        public bool WriteCommitted { get; private set; }
        public Task Writer { get; private set; } = Task.CompletedTask;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // The logic batch has its documented twelve reads.  This is the first
            // scene reader, after the header has already supplied the logical view.
            if (++readerCount == 13)
            {
                // The production read transaction deliberately keeps this writer
                // outside its snapshot until the complete mapped view is ready.
                // Run it concurrently rather than deadlocking EF's reader callback.
                Writer = Task.Run(() =>
                {
                    using var writer = new SqliteConnection($"Data Source={path};Default Timeout=5"); writer.Open();
                    using var update = writer.CreateCommand();
                    update.CommandText = "UPDATE Rooms SET FriendlyName = 'after', SceneUnitWidth = 99, SceneUnitHeight = 99";
                    WriteCommitted = update.ExecuteNonQuery() == 1;
                });
            }
            return ValueTask.FromResult(result);
        }
    }
}
