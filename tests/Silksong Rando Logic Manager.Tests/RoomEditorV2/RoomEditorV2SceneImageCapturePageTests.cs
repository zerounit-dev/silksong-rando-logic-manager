using Bunit;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SceneImageBuilder;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Data;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

/// <summary>
/// The capture route is deliberately tested as rendered-page work.  In particular,
/// these tests do not preselect a command outcome or call the coordinator refresh
/// overload directly: Apply reaches the production capture service and its owned
/// filesystem output.
/// </summary>
public sealed class RoomEditorV2SceneImageCapturePageTests
{
    [Fact]
    public async Task RenderedProductionCaptureModal_ProjectsActiveFrameExitConnectionAndCheckOverlays()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "silksong-v2-scene-preview-projection-" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuildSourceAsync(root); var room = await AddEligibleRoomAsync(fixture);
            await using (var db = fixture.CreateDbContext())
            {
                var frame = new Subroom { RoomId = room.Id, FriendlyName = "Frame", ReferenceId = "frame", SceneUnitX = 10, SceneUnitY = 5, SceneUnitWidth = 30, SceneUnitHeight = 20 };
                db.AddRange(frame,
                    new RoomTransition { RoomId = room.Id, Alias = "out", FriendlyName = "Exit", EnableAnnotation = true, AnnotationSceneUnitX = 20, AnnotationSceneUnitY = 30 },
                    new SubroomConnection { RoomId = room.Id, Alias = "way", FriendlyName = "Path", SourceSubroomReferenceText = "frame", DestinationSubroomReferenceText = "frame", EnableAnnotation = true, SceneUnitX = 40, SceneUnitY = 15 },
                    new CheckLocation { RoomId = room.Id, FriendlyName = "Check", EnableAnnotation = true, AnnotationSceneUnitX = 60, AnnotationSceneUnitY = 10 });
                await db.SaveChangesAsync();
            }
            var files = new SceneImageFileService(root);
            using var context = PageContext(fixture, files);
            var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));

            page.Find("button[data-scene-capture='true']").Click();
            page.WaitForAssertion(() =>
            {
                var dialog = page.Find("#v2-page-modal-dialog");
                Assert.True(dialog.ClassList.Contains("confirmation-modal"));
                Assert.True(dialog.ClassList.Contains("scene-image-capture-modal"));
                Assert.Equal("capture scene image", dialog.QuerySelector("h2")!.TextContent);
                Assert.Contains("wheel: zoom | drag: pan | Ctrl+wheel: uniform scale | Ctrl+drag: pan image | Ctrl+Shift+drag: scale X/Y", dialog.TextContent);
                Assert.Equal(new[] { "reset estimate", "cancel", "apply" }, dialog.QuerySelectorAll(".confirmation-actions button").Select(x => x.TextContent.Trim()).ToArray());
                Assert.Single(page.FindAll("svg[data-v2-scene-capture-preview='true']"));
                Assert.Single(page.FindAll("[data-v2-scene-capture-frame='true']"));
                Assert.Single(page.FindAll("[data-v2-scene-capture-marker='exit']"));
                Assert.Single(page.FindAll("[data-v2-scene-capture-marker='connection']"));
                Assert.Single(page.FindAll("[data-v2-scene-capture-marker='check']"));
            });
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HeldRenderedProductionApply_ShowsProgressBlocksAndReturnsActualExpectedFailureForChangedMapContext()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "silksong-v2-scene-held-" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuildSourceAsync(root); var room = await AddEligibleRoomAsync(fixture); var files = new SceneImageFileService(root);
            var production = new RoomEditorV2CommandService(new LogicCatalogService(fixture, files), fixture, sceneImages:
                new SceneImageCaptureService(fixture, new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService(), files));
            var held = new HeldCaptureApply(production);
            using var context = PageContext(fixture, files, held);
            var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
            page.Find("button[data-scene-capture='true']").Click();
            page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "apply").Click();
            await held.Entered.Task;
            page.WaitForAssertion(() =>
            {
                Assert.Single(page.FindAll("[data-scene-image-generation-progress='true']"));
                Assert.Equal("Committing", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage"));
            });
            Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));
            page.Find(".v2-page-modal-backdrop").Click();
            Assert.Single(page.FindAll("[data-scene-image-generation-progress='true']"));

            // This is a real existing map-link service mutation while the admitted
            // production generation route is held. The service then returns its
            // actual eligibility (ExpectedFailure), not a test-supplied outcome.
            var links = new MapLinkService(fixture); var link = Assert.Single((await links.GetAsync()).Rows);
            await links.SaveAsync([new MapLinkDraft(link.MapSceneId, null)]);
            held.Release();
            page.WaitForAssertion(() =>
            {
                Assert.Single(page.FindAll("#v2-page-modal-dialog"));
                Assert.Contains("exactly one map", page.Find("#v2-page-modal-dialog").TextContent);
                Assert.Empty(page.FindAll("[data-scene-image-generation-progress='true']"));
            });
            Assert.False(files.Exists(room.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HeldRenderedProductionRecapture_SuccessShowsProgressBlocksAndPerformsExactlyOneRefresh()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "silksong-v2-scene-success-" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuildSourceAsync(root); var room = await AddEligibleRoomAsync(fixture); var files = new SceneImageFileService(root);
            var production = new RoomEditorV2CommandService(new LogicCatalogService(fixture, files), fixture, sceneImages:
                new SceneImageCaptureService(fixture, new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService(), files));
            using (var initialContext = PageContext(fixture, files, production))
            {
                var initial = initialContext.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
                await CaptureAsync(initial, "apply");
            }
            var held = new HeldCaptureApply(production);
            using var context = PageContext(fixture, files, held);
            var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
            var priorImageUrl = page.Find("image[data-scene-layout-image='true']").GetAttribute("href");
            var events = Trace(page).OperationTrace.Events.Count;
            page.Find("button[data-scene-capture='true']").Click();
            Assert.Contains("recapture scene image", page.Find("button[data-scene-capture='true']").TextContent);
            page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "reset estimate").Click();
            page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "apply").Click();
            await held.Entered.Task;
            await AssertGenerationBlockedAsync(page);
            held.Release(); await held.Completed.Task;
            page.WaitForAssertion(() =>
            {
                Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
                Assert.Equal(events + 3, Trace(page).OperationTrace.Events.Count);
                Assert.Contains("refresh #2", page.Markup);
                Assert.Single(page.FindAll("image[data-scene-layout-image='true']"));
                var refreshedImageUrl = page.Find("image[data-scene-layout-image='true']").GetAttribute("href");
                Assert.NotEqual(priorImageUrl, refreshedImageUrl);
                Assert.Matches(@"\.webp\?v=[1-9][0-9]*$", refreshedImageUrl);
            });
            Assert.True(files.Exists(room.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldRenderedProductionRecapture_FailuresPreservePriorCaptureAndOfferCorrection(bool unexpected)
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "silksong-v2-scene-failure-" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuildSourceAsync(root); var room = await AddEligibleRoomAsync(fixture); var files = new SceneImageFileService(root);
            var production = new RoomEditorV2CommandService(new LogicCatalogService(fixture, files), fixture, sceneImages:
                new SceneImageCaptureService(fixture, new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService(), files));
            using (var initialContext = PageContext(fixture, files, production))
            {
                var initial = initialContext.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
                await CaptureAsync(initial, "apply");
            }
            var originalFile = await File.ReadAllBytesAsync(files.GetRoomImagePath(room.Id));
            var originalTransform = await ReadTransformAsync(fixture, room.Id);
            var held = new HeldCaptureApply(production);
            using var context = PageContext(fixture, files, held);
            var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
            page.Find("button[data-scene-capture='true']").Click();
            page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "apply").Click();
            await held.Entered.Task;
            await AssertGenerationBlockedAsync(page);
            if (unexpected)
                File.Delete(Path.Combine(root, "data", "scene-source", "tiles", "0-0.webp"));
            else
            {
                var link = Assert.Single((await new MapLinkService(fixture).GetAsync()).Rows);
                await new MapLinkService(fixture).SaveAsync([new MapLinkDraft(link.MapSceneId, null)]);
            }
            held.Release(); await held.Completed.Task;
            page.WaitForAssertion(() =>
            {
                Assert.Single(page.FindAll("#v2-page-modal-dialog"));
                Assert.Equal("ProposalOpen", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage"));
                Assert.Contains("recapture scene image", page.Find("#v2-page-modal-dialog").TextContent);
                Assert.Single(page.FindAll("image[data-scene-layout-image='true']"));
            });
            Assert.Equal(originalFile, await File.ReadAllBytesAsync(files.GetRoomImagePath(room.Id)));
            Assert.Equal(originalTransform, await ReadTransformAsync(fixture, room.Id));
            Assert.Equal(unexpected ? V2SceneImageCaptureCommandStatus.Unexpected : V2SceneImageCaptureCommandStatus.ExpectedFailure, held.LastOutcome!.Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HeldRenderedProductionCapture_RouteSwitchAndDisposalRejectStaleCompletionEffects()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "silksong-v2-scene-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuildSourceAsync(root); var source = await AddEligibleRoomAsync(fixture); var destination = new Room { FriendlyName = "destination", ReferenceId = "destination", SceneUnitWidth = 20, SceneUnitHeight = 10 };
            await using (var db = fixture.CreateDbContext()) { db.Rooms.Add(destination); await db.SaveChangesAsync(); }
            var files = new SceneImageFileService(root);
            var production = new RoomEditorV2CommandService(new LogicCatalogService(fixture, files), fixture, sceneImages:
                new SceneImageCaptureService(fixture, new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService(), files));
            var held = new HeldCaptureApply(production);
            using var context = PageContext(fixture, files, held);
            var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Id));
            page.Find("button[data-scene-capture='true']").Click(); page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "apply").Click();
            await held.Entered.Task; await AssertGenerationBlockedAsync(page);
            page.SetParametersAndRender(p => p.Add(x => x.RoomId, destination.Id));
            page.WaitForAssertion(() => { Assert.Contains("destination", page.Markup); Assert.Empty(page.FindAll("#v2-page-modal-dialog")); });
            var diagnostic = page.Find(".v2-refresh-diagnostic").TextContent; var trace = Trace(page).OperationTrace.Events.ToArray(); var renders = page.RenderCount; var focus = context.JSInterop.Invocations.Count(x => x.Identifier == "focusEditorField");
            held.Release(); await held.Completed.Task;
            page.WaitForAssertion(() =>
            {
                Assert.Equal(diagnostic, page.Find(".v2-refresh-diagnostic").TextContent); Assert.Equal(trace, Trace(page).OperationTrace.Events); Assert.Equal(renders, page.RenderCount);
                Assert.Equal(focus, context.JSInterop.Invocations.Count(x => x.Identifier == "focusEditorField")); Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
            });
            Assert.True(files.Exists(source.Id));

            var disposalHeld = new HeldCaptureApply(production);
            using var disposalContext = PageContext(fixture, files, disposalHeld);
            var disposedPage = disposalContext.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, source.Id));
            disposedPage.Find("button[data-scene-capture='true']").Click(); disposedPage.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "apply").Click();
            await disposalHeld.Entered.Task; await AssertGenerationBlockedAsync(disposedPage);
            await disposedPage.InvokeAsync(() => disposedPage.Instance.DisposeAsync()); disposalHeld.Release(); await disposalHeld.Completed.Task;
            Assert.Null(typeof(RoomEditorV2Page).GetField("modalState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(disposedPage.Instance));
            Assert.DoesNotContain(disposalContext.JSInterop.Invocations, x => x.Identifier == "focusEditorField");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RenderedCaptureAndRecapture_UseProductionApplyPersistOneImageAndKeepCancelLocal()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "silksong-v2-scene-page-" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuildSourceAsync(root);
            var room = await AddEligibleRoomAsync(fixture);
            var files = new SceneImageFileService(root);
            using var context = PageContext(fixture, files);
            context.JSInterop.Setup<V2SceneImageCapturePreviewValues>("readV2SceneImageCapturePreview", _ => true)
                .SetResult(new V2SceneImageCapturePreviewValues("88", "44", "-6", "11"));
            var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));

            // Availability, capture copy, retained estimate reset, and every
            // no-write dismissal are asserted against the actual rendered action.
            var capture = page.Find("button[data-scene-capture='true']");
            Assert.False(capture.HasAttribute("disabled"));
            Assert.Contains("capture scene image", capture.TextContent);
            capture.Click();
            page.WaitForAssertion(() => Assert.Single(page.FindAll("#v2-page-modal-dialog")));
            Assert.Equal("dialog", page.Find("#v2-page-modal-dialog").GetAttribute("role"));
            Assert.Equal(4, page.FindAll("#v2-page-modal-dialog input").Count);
            Assert.Contains(context.JSInterop.Invocations, call => call.Identifier == "focusV2ModalDialog");

            var firstInput = page.Find("input[aria-label='Scene image scale X']");
            var estimate = firstInput.GetAttribute("value");
            firstInput.Input("77");
            page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "reset estimate").Click();
            Assert.Equal(estimate, page.Find("input[aria-label='Scene image scale X']").GetAttribute("value"));
            page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "cancel").Click();
            page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
            Assert.False(files.Exists(room.Id));

            capture = page.Find("button[data-scene-capture='true']"); capture.Click();
            await page.InvokeAsync(() => page.Find("#v2-page-modal-dialog").TriggerEventAsync("onkeydown", new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" }));
            page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
            Assert.False(files.Exists(room.Id));
            page.Find("button[data-scene-capture='true']").Click(); page.Find(".v2-page-modal-backdrop").Click();
            page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
            Assert.False(files.Exists(room.Id));

            var events = Trace(page).OperationTrace.Events.Count;
            page.Find("button[data-scene-capture='true']").Click();
            page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "apply").Click();
            page.WaitForAssertion(() =>
            {
                Assert.Empty(page.FindAll("#v2-page-modal-dialog"));
                Assert.Equal(events + 3, Trace(page).OperationTrace.Events.Count);
                Assert.Single(page.FindAll("image[data-scene-layout-image='true']"));
                Assert.Contains("recapture scene image", page.Find("button[data-scene-capture='true']").TextContent);
            });
            Assert.True(files.Exists(room.Id));
            var original = await File.ReadAllBytesAsync(files.GetRoomImagePath(room.Id));
            await using (var verify = fixture.CreateDbContext())
            {
                var saved = await verify.Rooms.SingleAsync(x => x.Id == room.Id);
                Assert.False(saved.IsSceneImageStale);
                Assert.Equal(88, saved.SceneImageScaleXPercent);
                Assert.Equal(44, saved.SceneImageScaleYPercent);
                Assert.Equal(-6, saved.SceneImagePanXPercent);
                Assert.Equal(11, saved.SceneImagePanYPercent);
            }

            // A failed real recapture retains both the preceding transform and file.
            File.Delete(Path.Combine(root, "data", "scene-source", "tiles", "0-0.webp"));
            page.Find("button[data-scene-capture='true']").Click();
            Assert.Contains("recapture scene image", page.Find("#v2-page-modal-dialog").TextContent);
            page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "apply").Click();
            page.WaitForAssertion(() => Assert.Contains("unable", page.Find("#v2-page-modal-dialog").TextContent, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(original, await File.ReadAllBytesAsync(files.GetRoomImagePath(room.Id)));
            Assert.Single(page.FindAll("#v2-page-modal-dialog"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RenderedResetEstimate_UsesFreshSqliteEstimateInsteadOfRetainedTransformAndDoesNotWrite()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "silksong-v2-scene-reset-" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuildSourceAsync(root); var room = await AddEligibleRoomAsync(fixture); var files = new SceneImageFileService(root);
            await using (var db = fixture.CreateDbContext())
            {
                var persisted = await db.Rooms.SingleAsync(x => x.Id == room.Id);
                persisted.SceneImageScaleXPercent = 88; persisted.SceneImageScaleYPercent = 44; persisted.SceneImagePanXPercent = -6; persisted.SceneImagePanYPercent = 11;
                await db.SaveChangesAsync();
            }
            var before = await ReadTransformAsync(fixture, room.Id);
            using var context = PageContext(fixture, files);
            var page = context.RenderComponent<RoomEditorV2Page>(p => p.Add(x => x.RoomId, room.Id));
            page.Find("button[data-scene-capture='true']").Click();
            Assert.Equal("88", page.Find("input[aria-label='Scene image scale X']").GetAttribute("value"));

            await using (var db = fixture.CreateDbContext())
            {
                var chunk = await db.MapChunks.SingleAsync(); chunk.MapUnitMaxX = 50; await db.SaveChangesAsync();
            }
            page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == "reset estimate").Click();
            page.WaitForAssertion(() =>
            {
                Assert.NotEqual("88", page.Find("input[aria-label='Scene image scale X']").GetAttribute("value"));
                Assert.Contains(context.JSInterop.Invocations, x => x.Identifier == "reconcileV2SceneImageCapturePreview");
            });
            Assert.Equal(before, await ReadTransformAsync(fixture, room.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RealMapServicesThenFreshCaptureContext_ChangeReasonAndEstimateWithoutV2MapRoute()
    {
        await using var fixture = await MigratedSqliteFixture.CreateAsync();
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 100, SceneUnitHeight = 50 };
        await using (var db = fixture.CreateDbContext())
        {
            db.Rooms.Add(room);
            var map = new Map { InGameId = "map", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 100, MapUnitMaxY = 100 };
            var zone = new MapZone { Map = map, InGameId = "zone" };
            var scene = new MapScene { MapZone = zone, InGameId = "room" };
            db.AddRange(map, zone, scene, new MapChunk { MapScene = scene, CacheIndex = 0, MapUnitMinX = 10, MapUnitMinY = 10, MapUnitMaxX = 30, MapUnitMaxY = 30 });
            db.MapOverlays.Add(new MapOverlay { Map = map, FriendlyName = "area", ImageAssetKey = "area-map-hd", ScaleXPercent = 100, ScaleYPercent = 100 });
            await db.SaveChangesAsync();
        }
        var root = Path.Combine(Path.GetTempPath(), "silksong-v2-map-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            await BuildSourceAsync(root);
            var loader = new SceneLayoutLoader(fixture, new SceneImageFileService(root), new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService());
            var absent = await loader.LoadAsync(room.Id, CancellationToken.None);
            Assert.Equal("Link this room to exactly one map before capturing a scene image.", absent!.Capture!.AvailabilityReason);

            // Existing map-link service mutation, followed by a fresh capture load.
            var mapLinks = new MapLinkService(fixture);
            var row = Assert.Single((await mapLinks.GetAsync()).Rows);
            await mapLinks.SaveAsync([new MapLinkDraft(row.MapSceneId, room.ReferenceId)]);
            var linked = await loader.LoadAsync(room.Id, CancellationToken.None);
            Assert.True(linked!.Capture!.IsAvailable);
            var first = linked.Capture.Estimate;

            // Existing manifest reconciliation mutates the chunk/map bounds; the
            // authored link survives and the next independent capture read sees
            // the changed composed owner estimate.
            await new MapManifestService(fixture).ReconcileAsync(new MapManifest("map",
                [new ImportedMapZone("zone", new MapUnitBounds(0, 0, 100, 100))],
                [new ImportedMapChunk("zone", "room", 0, null, new MapUnitBounds(10, 10, 50, 30), 0)], 0));
            var chunkChanged = await loader.LoadAsync(room.Id, CancellationToken.None);
            Assert.True(chunkChanged!.Capture!.IsAvailable);
            Assert.NotEqual(first!.ScaleXPercent, chunkChanged.Capture.Estimate!.ScaleXPercent);

            // Existing overlay-placement service mutation changes the next fresh
            // estimate. No V2 page/coordinator map route, cache, or projection is
            // involved in this Phase-4 proof.
            await using (var db = fixture.CreateDbContext())
            {
                var overlay = await db.MapOverlays.SingleAsync();
                overlay.ScaleXPercent = 200;
                await new MapOverlayService(fixture).SavePlacementAsync(new(overlay.MapId, overlay.ScaleXPercent, overlay.ScaleYPercent, overlay.LeftOffsetPercent, overlay.BottomOffsetPercent));
            }
            var changed = await loader.LoadAsync(room.Id, CancellationToken.None);
            Assert.True(changed!.Capture!.IsAvailable);
            Assert.NotEqual(chunkChanged.Capture.Estimate!.ScaleXPercent, changed.Capture.Estimate!.ScaleXPercent);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static RoomEditorV2RefreshCoordinator Trace(IRenderedComponent<RoomEditorV2Page> page) =>
        (RoomEditorV2RefreshCoordinator)typeof(RoomEditorV2Page).GetField("refresh", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page.Instance)!;

    private static async Task CaptureAsync(IRenderedComponent<RoomEditorV2Page> page, string label)
    {
        page.Find("button[data-scene-capture='true']").Click();
        page.FindAll("#v2-page-modal-dialog button").Single(x => x.TextContent.Trim() == label).Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#v2-page-modal-dialog")));
        await Task.CompletedTask;
    }

    private static async Task AssertGenerationBlockedAsync(IRenderedComponent<RoomEditorV2Page> page)
    {
        page.WaitForAssertion(() =>
        {
            Assert.Single(page.FindAll("[data-scene-image-generation-progress='true']"));
            Assert.Equal("Committing", page.Find("#v2-page-modal-dialog").GetAttribute("data-v2-modal-stage"));
        });
        Assert.False(await page.InvokeAsync(() => page.Instance.RequestApplicationRoomNavigationAsync(Guid.NewGuid())));
        page.Find(".v2-page-modal-backdrop").Click();
        Assert.Single(page.FindAll("[data-scene-image-generation-progress='true']"));
    }

    private static async Task<(double? X, double? Y, double? PanX, double? PanY, bool Stale, DateTime UpdatedUtc)> ReadTransformAsync(MigratedSqliteFixture fixture, Guid roomId)
    {
        await using var db = fixture.CreateDbContext(); var room = await db.Rooms.SingleAsync(x => x.Id == roomId);
        return (room.SceneImageScaleXPercent, room.SceneImageScaleYPercent, room.SceneImagePanXPercent, room.SceneImagePanYPercent, room.IsSceneImageStale, room.UpdatedUtc);
    }

    private static TestContext PageContext(MigratedSqliteFixture fixture, SceneImageFileService files, IRoomEditorV2CommandService? commands = null)
    {
        var context = new TestContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<bool>("loadRoomMapContextVisibility").SetResult(true);
        context.Services.AddSingleton<IDbContextFactory<LogicDbContext>>(fixture);
        context.Services.AddSingleton<IAreaMapLoader>(new TestAreaMapLoader());
        context.Services.AddSingleton<MapRenderProjectionService>();
        context.Services.AddSingleton<MapOverlayAssetCatalog>();
        context.Services.AddSingleton<MapOverlayPlacementService>();
        context.Services.AddSingleton<DiagnosticState>();
        context.Services.AddSingleton<MapLinkService>();
        var capture = new SceneImageCaptureService(fixture, new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService(), files);
        context.Services.AddSingleton<IRoomEditorV2LogicLoader>(new RoomEditorV2LogicLoader(fixture, new SceneLayoutLoader(fixture, files, new MapRenderProjectionService(), new MapOverlayAssetCatalog(), new MapOverlayPlacementService())));
        context.Services.AddSingleton<IRoomEditorV2CommandService>(commands ?? new RoomEditorV2CommandService(new LogicCatalogService(fixture, files), fixture, sceneImages: capture));
        return context;
    }

    private static async Task BuildSourceAsync(string root)
    {
        var source = Path.Combine(root, "data", "scene-source"); Directory.CreateDirectory(source);
        using var image = new MagickImage(MagickColors.Red, 2048, 1024); image.Write(Path.Combine(source, "room-map-hd.png"));
        await SceneImageBuilderService.BuildAsync(source);
    }

    private static async Task<Room> AddEligibleRoomAsync(MigratedSqliteFixture fixture)
    {
        var room = new Room { FriendlyName = "Room", ReferenceId = "room", SceneUnitWidth = 100, SceneUnitHeight = 50 };
        await using var db = fixture.CreateDbContext();
        var map = new Map { InGameId = "map", MapUnitMinX = 0, MapUnitMinY = 0, MapUnitMaxX = 100, MapUnitMaxY = 100 };
        var zone = new MapZone { Map = map, InGameId = "zone" }; var scene = new MapScene { MapZone = zone, InGameId = "room", RoomReferenceText = "room", ResolvedRoomId = room.Id };
        db.AddRange(room, map, zone, scene, new MapChunk { MapScene = scene, CacheIndex = 0, MapUnitMinX = 10, MapUnitMinY = 10, MapUnitMaxX = 30, MapUnitMaxY = 30 });
        db.MapOverlays.Add(new MapOverlay { Map = map, FriendlyName = "area", ImageAssetKey = "area-map-hd", ScaleXPercent = 100, ScaleYPercent = 100 });
        await db.SaveChangesAsync(); return room;
    }

    private sealed class HeldCaptureApply(IRoomEditorV2CommandService inner) : IRoomEditorV2CommandService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public V2SceneImageCaptureCommandOutcome? LastOutcome { get; private set; }
        public void Release() => release.TrySetResult();
        public async Task<V2SceneImageCaptureCommandOutcome> ApplySceneImageCaptureAsync(Guid roomId, SceneImageCaptureDraft draft)
        {
            try { Entered.TrySetResult(); await release.Task; return LastOutcome = await inner.ApplySceneImageCaptureAsync(roomId, draft); }
            finally { Completed.TrySetResult(); }
        }
        public Task<V2SceneImageCaptureCommandOutcome> ResetSceneImageCaptureEstimateAsync(Guid roomId) => inner.ResetSceneImageCaptureEstimateAsync(roomId);
        public Task<V2SubroomCommandOutcome> SaveSubroomAsync(Guid roomId, SubroomDurableBaseline baseline, SubroomDraft draft) => inner.SaveSubroomAsync(roomId, baseline, draft);
        public Task<V2SubroomCommandOutcome> CreateSubroomAsync(Guid roomId, SubroomDraft draft) => inner.CreateSubroomAsync(roomId, draft);
        public Task<V2SubroomCommandOutcome> ReorderSubroomAsync(Guid roomId, Guid entityId, int targetIndex) => inner.ReorderSubroomAsync(roomId, entityId, targetIndex);
        public Task<V2SubroomCommandOutcome> SetSubroomArchiveAsync(Guid roomId, Guid entityId, bool archived) => inner.SetSubroomArchiveAsync(roomId, entityId, archived);
        public Task<V2SubroomCommandOutcome> DeleteSubroomAsync(Guid roomId, Guid entityId) => inner.DeleteSubroomAsync(roomId, entityId);
    }
}
