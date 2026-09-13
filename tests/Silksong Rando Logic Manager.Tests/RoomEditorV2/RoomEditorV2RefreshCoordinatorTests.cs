using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace Silksong_Rando_Logic_Manager.Tests.RoomEditorV2;

public sealed class RoomEditorV2RefreshCoordinatorTests
{
    [Fact]
    public async Task RefreshAsync_AppliesOnlyOneCompleteFreshView()
    {
        var room = Guid.NewGuid();
        var loader = new ControlledLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader);

        var pending = coordinator.RefreshAsync(room);
        loader.Complete(room, View(room, "fresh"));
        Assert.False(await pending);
        Assert.Equal("fresh", coordinator.View!.Header.FriendlyName);
    }

    [Fact]
    public async Task RefreshAsync_FaultLeavesExistingCompleteViewUntouched()
    {
        var room = Guid.NewGuid();
        var loader = new ControlledLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        var first = coordinator.RefreshAsync(room); loader.Complete(room, View(room, "old")); await first;
        var failed = coordinator.RefreshAsync(room); loader.Fail(room, new InvalidOperationException("load fault"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        Assert.Equal("old", coordinator.View!.Header.FriendlyName);
    }

    [Fact]
    public async Task RefreshAsync_RouteSwitchRejectsOldResultAndMissingRouteClears()
    {
        var oldRoom = Guid.NewGuid(); var newRoom = Guid.NewGuid();
        var loader = new ControlledLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        var old = coordinator.RefreshAsync(oldRoom);
        var next = coordinator.RefreshAsync(newRoom);
        Assert.Null(coordinator.View); // clear before a route's missing redirect/load completes
        loader.Complete(oldRoom, View(oldRoom, "old"));
        loader.Complete(newRoom, null);
        Assert.False(await old);
        Assert.True(await next);
        Assert.Null(coordinator.View);
    }

    [Fact]
    public async Task RefreshAsync_AppliesArchivedRoom()
    {
        var room = Guid.NewGuid(); var loader = new ControlledLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        var pending = coordinator.RefreshAsync(room);
        loader.Complete(room, View(room, "archived", archived: true));
        await pending;
        Assert.True(coordinator.View!.Header.IsArchived);
    }

    [Fact]
    public async Task SameRoomNonSceneRefresh_RetainsTheCompleteExistingScene_ButASceneLoadMayClearIt()
    {
        var room = Guid.NewGuid();
        var scene = new SceneLayoutView(true, 30, 40,
            [new(Guid.NewGuid(), "frame", null, 1, 2, 3, 4)],
            [new(Guid.NewGuid(), "exit", "marker", null, 5, 6)],
            new(true, false, true, 71), new(true, "ready", new(1, 1, 0, 0)));
        var loader = new CountingLoader(View(room, "initial"));
        var sceneLoader = new SequenceSceneLoader(scene, null);
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader, sceneLoader);

        await coordinator.RefreshAsync(room);
        await coordinator.CommitTransitionCommandAsync(room, "transition-notes",
            () => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Committed)));

        Assert.Same(scene, coordinator.View!.Scene);
        Assert.Equal((1, 0), (sceneLoader.Count, coordinator.OperationTrace.MapLoaderInvocations));

        await coordinator.RefreshAsync(room, loadScene: true);
        Assert.Null(coordinator.View!.Scene);
        Assert.Equal(2, sceneLoader.Count);
    }

    [Fact]
    public void Mapper_OnlyRetainsAnIntentionallySkippedSceneForTheSameRoom()
    {
        var room = Guid.NewGuid(); var other = Guid.NewGuid();
        var scene = new SceneLayoutView(true, 1, 1, [], [], new(true, false, true, 9));
        var current = View(room, "old") with { Scene = scene };

        Assert.Same(scene, RoomEditorV2Mapper.Reconcile(current, View(room, "fresh"), retainCurrentScene: true).Scene);
        Assert.Null(RoomEditorV2Mapper.Reconcile(current, View(other, "other"), retainCurrentScene: true).Scene);
        Assert.Null(RoomEditorV2Mapper.Reconcile(current, View(room, "fresh")).Scene);
    }

    [Fact]
    public void Mapper_CanvasOnlySameRoomRefreshRetainsCaptureContextWhileReplacingFreshCanvasAndImage()
    {
        var room = Guid.NewGuid();
        var capture = new SceneImageCaptureContextView(false, "Link this room to exactly one map before capturing a scene image.", null, true);
        var current = View(room, "old") with { Scene = new(true, 10, 10, [], [], new(true, true, false, 1), capture) };
        var canvasOnly = View(room, "fresh") with { Scene = new(true, 20, 30, [], [new(Guid.NewGuid(), "exit", "e", null, 4, 5)], new(true, false, true, 2), null) };
        var fullCapture = canvasOnly with { Scene = canvasOnly.Scene! with { Capture = new(true, "Scene image capture is ready.", new(1, 1, 0, 0)) } };

        var retained = RoomEditorV2Mapper.Reconcile(current, canvasOnly, retainCurrentCaptureContext: true);

        Assert.Equal((20d, 30d, 2L), (retained.Scene!.SceneUnitWidth, retained.Scene.SceneUnitHeight, retained.Scene.Image!.ImageVersion));
        Assert.Equal(capture, retained.Scene.Capture);
        Assert.Equal(fullCapture.Scene!.Capture, RoomEditorV2Mapper.Reconcile(current, fullCapture, retainCurrentCaptureContext: true).Scene!.Capture);
    }

    [Fact]
    public async Task RefreshDiagnostic_AdvancesOnlyForSuccessfullyAppliedCompleteViews()
    {
        var firstRoom = Guid.NewGuid(); var secondRoom = Guid.NewGuid();
        var loader = new ControlledLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader);

        var first = coordinator.RefreshAsync(firstRoom);
        loader.Complete(firstRoom, View(firstRoom, "first"));
        await first;
        var initial = Assert.IsType<V2AppliedRefreshDiagnostic>(coordinator.LastAppliedRefresh);
        Assert.Equal(1, initial.Sequence);
        Assert.True(initial.ServerElapsed >= TimeSpan.Zero);

        var stale = coordinator.RefreshAsync(firstRoom);
        var missing = coordinator.RefreshAsync(secondRoom);
        loader.Complete(firstRoom, View(firstRoom, "stale"));
        loader.Complete(secondRoom, null);
        await stale;
        Assert.True(await missing);
        Assert.Equal(initial, coordinator.LastAppliedRefresh);

        var faulted = coordinator.RefreshAsync(secondRoom);
        loader.Fail(secondRoom, new InvalidOperationException("fault"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => faulted);
        Assert.Equal(initial, coordinator.LastAppliedRefresh);
    }

    [Fact]
    public async Task RefreshDiagnostic_DoesNotAdvanceForCancelledLoad()
    {
        var oldRoom = Guid.NewGuid(); var newRoom = Guid.NewGuid();
        var loader = new CancellationAwareLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader);

        var cancelled = coordinator.RefreshAsync(oldRoom);
        var applied = coordinator.RefreshAsync(newRoom);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        loader.Complete(newRoom, View(newRoom, "fresh"));
        await applied;

        Assert.Equal(1, coordinator.LastAppliedRefresh?.Sequence);
        Assert.Equal("fresh", coordinator.View!.Header.FriendlyName);
    }

    [Fact]
    public async Task EveryCommittedSubroomOperation_CommitsThenPerformsOneCompleteRefreshWithoutMapOrScene()
    {
        var room = Guid.NewGuid();
        var loader = new CountingLoader(View(room, "initial"));
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        foreach (var command in new[] { "save", "create", "reorder-active", "reorder-archived", "archive", "restore", "permanent-delete" })
        {
            var result = await coordinator.CommitSubroomCommandAsync(room, command,
                () => Task.FromResult(new V2SubroomCommandOutcome(V2SubroomCommandStatus.Committed)));
            Assert.Equal(V2SubroomCommandStatus.Committed, result.Status);
        }

        Assert.Equal(7, loader.LoadCount);
        Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations);
        Assert.Equal(0, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(new[]
        {
            "save:committed", "save:complete-room-refresh", "create:committed", "create:complete-room-refresh",
            "reorder-active:committed", "reorder-active:complete-room-refresh", "reorder-archived:committed", "reorder-archived:complete-room-refresh",
            "archive:committed", "archive:complete-room-refresh", "restore:committed", "restore:complete-room-refresh",
            "permanent-delete:committed", "permanent-delete:complete-room-refresh"
        }, coordinator.OperationTrace.Events);
    }

    [Fact]
    public async Task EveryCommittedCheckOperation_CommitsThenPerformsOneCompleteRefreshWithoutMapOrScene()
    {
        var room = Guid.NewGuid(); var loader = new CountingLoader(View(room, "initial")); using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        foreach (var command in new[] { "text-save", "control-save", "create", "reorder-active", "reorder-archived", "archive", "restore", "permanent-delete" })
            Assert.Equal(V2CheckCommandStatus.Committed, (await coordinator.CommitCheckCommandAsync(room, command, () => Task.FromResult(new V2CheckCommandOutcome(V2CheckCommandStatus.Committed)))).Status);
        Assert.Equal(8, loader.LoadCount); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(new[] { "text-save:committed", "text-save:complete-room-refresh", "control-save:committed", "control-save:complete-room-refresh", "create:committed", "create:complete-room-refresh", "reorder-active:committed", "reorder-active:complete-room-refresh", "reorder-archived:committed", "reorder-archived:complete-room-refresh", "archive:committed", "archive:complete-room-refresh", "restore:committed", "restore:complete-room-refresh", "permanent-delete:committed", "permanent-delete:complete-room-refresh" }, coordinator.OperationTrace.Events);
    }

    [Fact]
    public async Task EveryCommittedTransitionOperation_CommitsThenPerformsOneCompleteRefreshWithoutMapOrScene_AndNoncommitsDoNotRefresh()
    {
        var room = Guid.NewGuid(); var loader = new CountingLoader(View(room, "initial")); using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        foreach (var command in new[] { "save", "create", "reorder", "archive", "restore", "permanent-delete", "inverse-update", "inverse-do-not-update" })
            Assert.Equal(V2TransitionCommandStatus.Committed, (await coordinator.CommitTransitionCommandAsync(room, command, () => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Committed)))).Status);
        foreach (var status in new[] { V2TransitionCommandStatus.Unchanged, V2TransitionCommandStatus.Proposal, V2TransitionCommandStatus.Conflict, V2TransitionCommandStatus.Missing, V2TransitionCommandStatus.ExpectedFailure })
            Assert.Equal(status, (await coordinator.CommitTransitionCommandAsync(room, "noncommit", () => Task.FromResult(new V2TransitionCommandOutcome(status)))).Status);
        Assert.Equal(8, loader.LoadCount); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(16, coordinator.OperationTrace.Events.Count);
    }

    [Fact]
    public async Task TransitionMetadata_CommitsThenPerformsExactlyOneCompleteRefreshWithoutMapOrScene()
    {
        var room = Guid.NewGuid(); var loader = new CountingLoader(View(room, "initial")); using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        var result = await coordinator.CommitTransitionCommandAsync(room, "transition-metadata", () => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Committed)));
        Assert.Equal(V2TransitionCommandStatus.Committed, result.Status);
        Assert.Equal(1, loader.LoadCount); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(new[] { "transition-metadata:committed", "transition-metadata:complete-room-refresh" }, coordinator.OperationTrace.Events);
    }

    [Fact]
    public async Task CheckMetadata_CommitsThenPerformsExactlyOneCompleteRefreshWithoutMapOrScene()
    {
        var room = Guid.NewGuid(); var loader = new CountingLoader(View(room, "initial")); using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        var result = await coordinator.CommitCheckCommandAsync(room, "check-metadata", () => Task.FromResult(new V2CheckCommandOutcome(V2CheckCommandStatus.Committed)));
        Assert.Equal(V2CheckCommandStatus.Committed, result.Status);
        Assert.Equal(1, loader.LoadCount); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(new[] { "check-metadata:committed", "check-metadata:complete-room-refresh" }, coordinator.OperationTrace.Events);
    }

    [Fact]
    public async Task TransitionAndCheckAnnotationMatrixRoutes_CommitOneCompleteRefreshAndOneSceneLoadWithoutMap()
    {
        var room = Guid.NewGuid(); var loader = new CountingLoader(View(room, "initial")); var scene = new CountingSceneLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader, scene);
        foreach (var route in new[] { "transition-annotation-placement", "transition-annotation-drag", "transition-annotation-remove", "transition-annotation-show" })
            Assert.Equal(V2TransitionCommandStatus.Committed, (await coordinator.CommitTransitionCommandAsync(room, route, () => Task.FromResult(new V2TransitionCommandOutcome(V2TransitionCommandStatus.Committed)), true)).Status);
        foreach (var route in new[] { "check-annotation-placement", "check-annotation-drag", "check-annotation-remove", "check-annotation-show" })
            Assert.Equal(V2CheckCommandStatus.Committed, (await coordinator.CommitCheckCommandAsync(room, route, () => Task.FromResult(new V2CheckCommandOutcome(V2CheckCommandStatus.Committed)), true)).Status);
        Assert.Equal(8, loader.LoadCount); Assert.Equal(8, scene.Count); Assert.Equal(8, coordinator.OperationTrace.SceneLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations);
        Assert.Equal(24, coordinator.OperationTrace.Events.Count);
    }

    [Fact]
    public async Task SubroomAnnotationShowHideAndClear_CommitOneCompleteRefreshAndOneSceneLoadWithoutMap_AndNoncommitsDoNothing()
    {
        var room = Guid.NewGuid(); var loader = new CountingLoader(View(room, "initial")); var scene = new CountingSceneLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader, scene);
        foreach (var route in new[] { "subroom-annotation-show", "subroom-annotation-hide", "subroom-annotation-clear" })
            Assert.Equal(V2SubroomCommandStatus.Committed, (await coordinator.CommitSubroomCommandAsync(room, route, () => Task.FromResult(new V2SubroomCommandOutcome(V2SubroomCommandStatus.Committed)), SceneRefreshImpact.SubroomGeometry)).Status);
        foreach (var status in new[] { V2SubroomCommandStatus.Unchanged, V2SubroomCommandStatus.Conflict, V2SubroomCommandStatus.Missing, V2SubroomCommandStatus.ExpectedFailure })
            Assert.Equal(status, (await coordinator.CommitSubroomCommandAsync(room, "subroom-annotation-noncommit", () => Task.FromResult(new V2SubroomCommandOutcome(status)), SceneRefreshImpact.SubroomGeometry)).Status);
        Assert.Equal(3, loader.LoadCount); Assert.Equal(3, scene.Count); Assert.Equal(3, coordinator.OperationTrace.SceneLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations);
        Assert.Equal(new[]
        {
            "subroom-annotation-show:committed", "scene-load:scene-load", "subroom-annotation-show:complete-room-refresh",
            "subroom-annotation-hide:committed", "scene-load:scene-load", "subroom-annotation-hide:complete-room-refresh",
            "subroom-annotation-clear:committed", "scene-load:scene-load", "subroom-annotation-clear:complete-room-refresh"
        }, coordinator.OperationTrace.Events);
    }

    [Fact]
    public async Task EveryCommittedConnectionOperation_CommitsThenPerformsOneCompleteRefreshWithoutMapOrScene_AndNoncommitsDoNotRefresh()
    {
        var room = Guid.NewGuid(); var loader = new CountingLoader(View(room, "initial")); using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        foreach (var command in new[] { "save", "create", "reorder", "archive", "restore", "permanent-delete", "scaffold" })
            Assert.Equal(V2ConnectionCommandStatus.Committed, (await coordinator.CommitConnectionCommandAsync(room, command, () => Task.FromResult(new V2ConnectionCommandOutcome(V2ConnectionCommandStatus.Committed)))).Status);
        foreach (var status in new[] { V2ConnectionCommandStatus.Unchanged, V2ConnectionCommandStatus.Conflict, V2ConnectionCommandStatus.Missing, V2ConnectionCommandStatus.ExpectedFailure })
            Assert.Equal(status, (await coordinator.CommitConnectionCommandAsync(room, "noncommit", () => Task.FromResult(new V2ConnectionCommandOutcome(status)))).Status);
        Assert.Equal(7, loader.LoadCount); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(14, coordinator.OperationTrace.Events.Count);
    }

    [Fact]
    public async Task EveryCommittedHeaderSave_PerformsOneCompleteRefreshWithoutMapOrScene()
    {
        var room = Guid.NewGuid(); var loader = new CountingLoader(View(room, "initial")); using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        foreach (var command in new[] { "friendly-name", "in-game-id", "contributors", "comments" })
            Assert.Equal(V2RoomHeaderCommandStatus.Committed, (await coordinator.CommitRoomHeaderCommandAsync(room, command, () => Task.FromResult(new V2RoomHeaderCommandOutcome(V2RoomHeaderCommandStatus.Committed)))).Status);
        Assert.Equal(4, loader.LoadCount); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(new[] { "friendly-name:committed", "friendly-name:complete-room-refresh", "in-game-id:committed", "in-game-id:complete-room-refresh", "contributors:committed", "contributors:complete-room-refresh", "comments:committed", "comments:complete-room-refresh" }, coordinator.OperationTrace.Events);
    }

    [Fact]
    public async Task NonCommittedHeaderOutcomes_DoNotRefreshOrRecordACommittedCommand()
    {
        var room = Guid.NewGuid(); var loader = new CountingLoader(View(room, "initial")); using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        foreach (var status in new[] { V2RoomHeaderCommandStatus.Unchanged, V2RoomHeaderCommandStatus.Conflict, V2RoomHeaderCommandStatus.Missing })
            Assert.Equal(status, (await coordinator.CommitRoomHeaderCommandAsync(room, "header-save", () => Task.FromResult(new V2RoomHeaderCommandOutcome(status)))).Status);
        Assert.Equal(0, loader.LoadCount);
        Assert.Empty(coordinator.OperationTrace.Events);
    }

    [Fact]
    public async Task EveryCommittedRoomLifecycleOperation_CommitsThenPerformsOneCompleteRefreshWithoutMapOrScene()
    {
        var room = Guid.NewGuid(); var loader = new CountingLoader(View(room, "initial")); using var coordinator = new RoomEditorV2RefreshCoordinator(loader);
        foreach (var command in new[] { "room-archive", "room-restore", "room-permanent-delete" })
            Assert.Equal(V2RoomLifecycleCommandStatus.Committed, (await coordinator.CommitRoomLifecycleCommandAsync(room, command, () => Task.FromResult(new V2RoomLifecycleCommandOutcome(V2RoomLifecycleCommandStatus.Committed)))).Status);
        Assert.Equal(3, loader.LoadCount); Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations); Assert.Equal(0, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(new[] { "room-archive:committed", "room-archive:complete-room-refresh", "room-restore:committed", "room-restore:complete-room-refresh", "room-permanent-delete:committed", "room-permanent-delete:complete-room-refresh" }, coordinator.OperationTrace.Events);
    }

    [Fact]
    public async Task RoomSceneDimensions_OnlyCommittedApplyOrClearPerformsOneLogicalAndSceneRefresh()
    {
        var room = Guid.NewGuid();
        var loader = new CountingLoader(View(room, "initial"));
        var scene = new CountingSceneLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader, scene);

        foreach (var command in new[] { "valid-apply", "blank-clear" })
        {
            var result = await coordinator.CommitRoomSceneDimensionsCommandAsync(room, "room-scene-dimensions",
                () => Task.FromResult(new V2RoomSceneDimensionsCommandOutcome(V2RoomSceneDimensionsCommandStatus.Committed)));
            Assert.Equal(V2RoomSceneDimensionsCommandStatus.Committed, result.Status);
        }
        foreach (var status in new[] { V2RoomSceneDimensionsCommandStatus.ExpectedFailure, V2RoomSceneDimensionsCommandStatus.Unchanged, V2RoomSceneDimensionsCommandStatus.Conflict, V2RoomSceneDimensionsCommandStatus.Missing })
            Assert.Equal(status, (await coordinator.CommitRoomSceneDimensionsCommandAsync(room, "room-scene-dimensions",
                () => Task.FromResult(new V2RoomSceneDimensionsCommandOutcome(status)))).Status);

        Assert.Equal(2, loader.LoadCount);
        Assert.Equal(2, scene.Count);
        Assert.Equal(2, coordinator.OperationTrace.SceneLoaderInvocations);
        Assert.Equal(0, coordinator.OperationTrace.MapLoaderInvocations);
        Assert.Equal(new[]
        {
            "room-scene-dimensions:committed", "scene-load:scene-load", "room-scene-dimensions:complete-room-refresh",
            "room-scene-dimensions:committed", "scene-load:scene-load", "room-scene-dimensions:complete-room-refresh"
        }, coordinator.OperationTrace.Events);
    }

    [Fact]
    public async Task SceneImageGeneration_HeldCommandAppliesOneRefreshOnlyForSuccess_AndNeverAppliesStaleFailureRouteOrDisposalResults()
    {
        var source = Guid.NewGuid(); var destination = Guid.NewGuid();
        var loader = new CountingLoader(View(source, "source")); var scene = new CaptureCountingSceneLoader();
        using var coordinator = new RoomEditorV2RefreshCoordinator(loader, scene);
        await coordinator.RefreshAsync(source);

        foreach (var status in new[] { V2SceneImageCaptureCommandStatus.Committed, V2SceneImageCaptureCommandStatus.ExpectedFailure, V2SceneImageCaptureCommandStatus.Unexpected })
        {
            var hold = new TaskCompletionSource<V2SceneImageCaptureCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            var eventsBefore = coordinator.OperationTrace.Events.Count;
            var sceneBefore = scene.CaptureRequests.Count;
            var pending = coordinator.CommitSceneImageCommandAsync(source, "scene-image-capture", () => hold.Task);
            Assert.Equal(eventsBefore, coordinator.OperationTrace.Events.Count); // generation progress has not completed a durable route
            hold.SetResult(new(status));
            Assert.Equal(status, (await pending).Status);
            Assert.Equal(eventsBefore + (status == V2SceneImageCaptureCommandStatus.Committed ? 3 : 0), coordinator.OperationTrace.Events.Count);
            if (status == V2SceneImageCaptureCommandStatus.Committed)
                Assert.Equal(sceneBefore + 1, scene.CaptureRequests.Count); // exactly one Apply refresh
        }

        var stale = new TaskCompletionSource<V2SceneImageCaptureCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var routePending = coordinator.CommitSceneImageCommandAsync(source, "scene-image-capture", () => stale.Task);
        await coordinator.RefreshAsync(destination, false, false);
        var traceBeforeRelease = coordinator.OperationTrace.Events.ToArray(); var appliedBeforeRelease = coordinator.LastAppliedRefresh;
        stale.SetResult(new(V2SceneImageCaptureCommandStatus.Committed));
        await routePending;
        Assert.Equal(traceBeforeRelease, coordinator.OperationTrace.Events);
        Assert.Equal(appliedBeforeRelease, coordinator.LastAppliedRefresh);
        Assert.Equal(destination, coordinator.CurrentRoomId);

        var disposal = new TaskCompletionSource<V2SceneImageCaptureCommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposedCoordinator = new RoomEditorV2RefreshCoordinator(new CountingLoader(View(source, "source")), new CaptureCountingSceneLoader());
        await disposedCoordinator.RefreshAsync(source);
        var disposalPending = disposedCoordinator.CommitSceneImageCommandAsync(source, "scene-image-capture", () => disposal.Task);
        disposedCoordinator.Dispose(); disposal.SetResult(new(V2SceneImageCaptureCommandStatus.Committed));
        await disposalPending;
        Assert.Single(disposedCoordinator.OperationTrace.Events, x => x == "scene-load:scene-load");
    }

    private static RoomEditorV2View View(Guid id, string name, bool archived = false) => new(
        new RoomHeaderView(id, name, name, null, null, null, null, null, false, false, archived, DateTime.UtcNow, V2Severity.Neutral, V2Severity.Neutral, V2Severity.Neutral),
        new SubroomTableView([], []), new TransitionTableView([], [], [], new Dictionary<Guid, IReadOnlyList<string>>()), new ConnectionTableView([], [], []), new CheckTableView([], [], []));

    private sealed class ControlledLoader : IRoomEditorV2LogicLoader
    {
        private readonly Dictionary<Guid, TaskCompletionSource<RoomEditorV2View?>> pending = [];
        public Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource<RoomEditorV2View?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[roomId] = source;
            return source.Task;
        }
        public void Complete(Guid roomId, RoomEditorV2View? view) => pending[roomId].TrySetResult(view);
        public void Fail(Guid roomId, Exception error) => pending[roomId].TrySetException(error);
    }
    private sealed class CountingLoader(RoomEditorV2View view) : IRoomEditorV2LogicLoader
    {
        public int LoadCount { get; private set; }
        public Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken) { LoadCount++; return Task.FromResult<RoomEditorV2View?>(view); }
    }
    private sealed class CountingSceneLoader : ISceneLayoutLoader
    {
        public int Count { get; private set; }
        public Task<SceneLayoutView?> LoadAsync(Guid roomId, CancellationToken cancellationToken) { Count++; return Task.FromResult<SceneLayoutView?>(new(false, null, null, [], [])); }
    }
    private sealed class CaptureCountingSceneLoader : ISceneLayoutLoader
    {
        public List<bool> CaptureRequests { get; } = [];
        public Task<SceneLayoutView?> LoadAsync(Guid roomId, CancellationToken cancellationToken) => LoadAsync(roomId, true, cancellationToken);
        public Task<SceneLayoutView?> LoadAsync(Guid roomId, bool includeCaptureContext, CancellationToken cancellationToken) { CaptureRequests.Add(includeCaptureContext); return Task.FromResult<SceneLayoutView?>(new(false, null, null, [], [])); }
    }
    private sealed class SequenceSceneLoader(params SceneLayoutView?[] scenes) : ISceneLayoutLoader
    {
        private int index;
        public int Count { get; private set; }
        public Task<SceneLayoutView?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(scenes[Math.Min(index++, scenes.Length - 1)]);
        }
    }
    private sealed class CancellationAwareLoader : IRoomEditorV2LogicLoader
    {
        private readonly Dictionary<Guid, TaskCompletionSource<RoomEditorV2View?>> pending = [];
        public Task<RoomEditorV2View?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource<RoomEditorV2View?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[roomId] = source;
            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            return source.Task;
        }
        public void Complete(Guid roomId, RoomEditorV2View view) => pending[roomId].TrySetResult(view);
    }
}
