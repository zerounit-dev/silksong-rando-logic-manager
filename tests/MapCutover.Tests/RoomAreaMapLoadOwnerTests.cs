using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace MapCutover.Tests;

public sealed class RoomAreaMapLoadOwnerTests
{
    [Fact]
    public async Task HeldInitialLoadForA_CannotAssignAfterNavigationToB()
    {
        var loader = new HeldLoader(); await using var owner = new RoomAreaMapLoadOwner(loader);
        var current = Guid.NewGuid(); var roomA = current; var roomB = Guid.NewGuid(); long generation = 1;
        bool Owns(Guid room, long expected) => room == current && expected == generation;
        var aLoad = owner.LoadAsync(roomA, null, 1, Owns); var a = loader.Requests.Single();
        current = roomB; generation = 2;
        var bLoad = owner.LoadAsync(roomB, null, 2, Owns); var b = loader.Requests[1];
        Assert.True(a.Token.IsCancellationRequested);
        a.Completion.SetResult(View(roomA)); Assert.False(await aLoad);
        b.Completion.SetResult(View(roomB)); Assert.True(await bLoad);
        Assert.Equal(roomB, owner.View!.Context.CurrentRoomId);
    }

    [Fact]
    public async Task AppliedAThenNavigationToB_PreservesGlobalGeometryForDestinationContextLoad()
    {
        var loader = new HeldLoader(); await using var owner = new RoomAreaMapLoadOwner(loader);
        var roomA = Guid.NewGuid(); var roomB = Guid.NewGuid(); var current = roomA; long generation = 1;
        bool Owns(Guid room, long expected) => room == current && expected == generation;
        var aLoad = owner.LoadAsync(roomA, null, generation, Owns); loader.Requests[0].Completion.SetResult(View(roomA)); Assert.True(await aLoad);
        var geometry = owner.RetainedGeometry; owner.Clear(); current = roomB; generation++;
        var bLoad = owner.LoadAsync(roomB, owner.RetainedGeometry, generation, Owns);
        Assert.Same(geometry, loader.Requests[1].RetainedGeometry);
        loader.Requests[1].Completion.SetResult(View(roomB) with { Geometry = geometry! }); Assert.True(await bLoad);
        Assert.Same(geometry, owner.View!.Geometry);
    }

    [Theory]
    [InlineData("room-map-link")]
    [InlineData("room-reference-resolution")]
    [InlineData("room-lifecycle-restore")]
    public async Task HeldPostCommandLoad_CannotAssignOrTriggerDestinationSceneRefresh(string operation)
    {
        var loader = new HeldLoader(); await using var owner = new RoomAreaMapLoadOwner(loader);
        var roomA = Guid.NewGuid(); var roomB = Guid.NewGuid(); var current = roomA; long generation = 4; var sceneRefreshes = 0;
        bool Owns(Guid room, long expected) => room == current && expected == generation;
        var load = owner.LoadAsync(roomA, null, generation, Owns); var request = loader.Requests.Single();
        current = roomB; generation++; owner.Clear();
        request.Completion.SetResult(View(roomA));
        if (await load && operation == "room-map-link") sceneRefreshes++;
        Assert.Null(owner.View); Assert.Equal(0, sceneRefreshes); Assert.True(request.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task HeldLifecycleArchiveLoadIsCancelledAndCleared()
    {
        var loader = new HeldLoader(); await using var owner = new RoomAreaMapLoadOwner(loader);
        var room = Guid.NewGuid();
        var load = owner.LoadAsync(room, null, 1, (_, _) => true); var request = loader.Requests.Single();
        owner.Clear(); request.Completion.SetResult(View(room));
        Assert.False(await load); Assert.Null(owner.View); Assert.True(request.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task HeldLoadCannotAssignAfterOwnerDisposal()
    {
        var loader = new HeldLoader(); var owner = new RoomAreaMapLoadOwner(loader); var room = Guid.NewGuid();
        var load = owner.LoadAsync(room, null, 1, (_, _) => true); var request = loader.Requests.Single();
        await owner.DisposeAsync(); request.Completion.SetResult(View(room));
        Assert.False(await load); Assert.Null(owner.View); Assert.True(request.Token.IsCancellationRequested);
    }

    private static AreaMapSurfaceView View(Guid room)
    {
        var map = Guid.NewGuid();
        return new(new(map, new(0, 0, 1, 1), room.ToString("N"), [], 0),
            new(new Dictionary<Guid, AreaMapOwnerDecoration>(), new Dictionary<Guid, AreaMapOwnerDecoration>(), null),
            new($"room-{room:N}", $"key-{room:N}", new(0, 0, 1, 1), room, true));
    }

    private sealed class HeldLoader : IAreaMapLoader
    {
        public List<Request> Requests { get; } = [];
        public Task<LandingAreaMapView> LoadLandingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AreaMapSurfaceView?> LoadRoomAsync(Guid roomId, GlobalAreaMapGeometryView? retainedGeometry, CancellationToken cancellationToken = default)
        {
            var request = new Request(roomId, retainedGeometry, cancellationToken, new(TaskCreationOptions.RunContinuationsAsynchronously)); Requests.Add(request); return request.Completion.Task;
        }
    }
    private sealed record Request(Guid RoomId, GlobalAreaMapGeometryView? RetainedGeometry, CancellationToken Token, TaskCompletionSource<AreaMapSurfaceView?> Completion);
}
