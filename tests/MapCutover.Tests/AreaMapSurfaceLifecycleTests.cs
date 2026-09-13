using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Components;
using Silksong_Rando_Logic_Manager.Services;
using Xunit;

namespace MapCutover.Tests;

public sealed class AreaMapSurfaceLifecycleTests
{
    [Fact]
    public async Task HeldCreateReturnedAfterSvgReplacementIsDisposedAndNeverRetained()
    {
        using var context = new TestContext(); var js = new HeldJsRuntime();
        context.Services.AddSingleton<IJSRuntime>(js);
        var rendered = context.RenderComponent<AreaMapSurface>(p => p.Add(x => x.View, View("A")));
        rendered.WaitForAssertion(() => Assert.Single(js.Creates));
        rendered.SetParametersAndRender(p => p.Add(x => x.View, View("B")));
        var stale = new HeldOwner(); js.Creates[0].SetResult(stale);
        rendered.WaitForAssertion(() => Assert.Equal(2, stale.DisposeCalls));
        rendered.WaitForAssertion(() => Assert.Equal(2, js.Creates.Count));
        var current = new HeldOwner(); js.Creates[1].SetResult(current);
        rendered.WaitForAssertion(() => Assert.Equal(0, current.DisposeCalls));
        await rendered.Instance.DisposeAsync();
        await WaitUntil(() => current.DisposeCalls == 2);
    }

    [Fact]
    public async Task HeldCreateReturnedAfterComponentDisposalIsImmediatelyDisposed()
    {
        using var context = new TestContext(); var js = new HeldJsRuntime();
        context.Services.AddSingleton<IJSRuntime>(js);
        var rendered = context.RenderComponent<AreaMapSurface>(p => p.Add(x => x.View, View("A")));
        rendered.WaitForAssertion(() => Assert.Single(js.Creates));
        await rendered.Instance.DisposeAsync();
        var stale = new HeldOwner(); js.Creates[0].SetResult(stale);
        await WaitUntil(() => stale.DisposeCalls == 2);
    }

    [Fact]
    public async Task HeldReconcileCannotKeepReplacedOwnerAlive()
    {
        using var context = new TestContext(); var js = new HeldJsRuntime();
        context.Services.AddSingleton<IJSRuntime>(js);
        var rendered = context.RenderComponent<AreaMapSurface>(p => p.Add(x => x.View, View("A")));
        rendered.WaitForAssertion(() => Assert.Single(js.Creates));
        var first = new HeldOwner { HoldReconcile = true }; js.Creates[0].SetResult(first);
        rendered.WaitForAssertion(() => Assert.Equal(0, first.DisposeCalls));
        rendered.SetParametersAndRender(p => p.Add(x => x.View, View("A", "second-key")));
        await WaitUntil(() => first.ReconcileStarted);
        rendered.SetParametersAndRender(p => p.Add(x => x.View, View("B")));
        first.ReleaseReconcile();
        await WaitUntil(() => first.DisposeCalls == 2);
        rendered.WaitForAssertion(() => Assert.Equal(2, js.Creates.Count));
    }

    private static AreaMapSurfaceView View(string svg, string? key = null)
    {
        var map = Guid.NewGuid();
        return new(new(map, new(0, 0, 1, 1), "geometry", [], 0),
            new(new Dictionary<Guid, AreaMapOwnerDecoration>(), new Dictionary<Guid, AreaMapOwnerDecoration>(), null),
            new(svg, key ?? svg, new(0, 0, 1, 1), null, false));
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class HeldJsRuntime : IJSRuntime
    {
        public List<TaskCompletionSource<IJSObjectReference?>> Creates { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("mapNavigation.create", identifier);
            var completion = new TaskCompletionSource<IJSObjectReference?>(TaskCreationOptions.RunContinuationsAsynchronously); Creates.Add(completion);
            return new(Convert<TValue>(completion.Task));
        }
        private static async Task<T> Convert<T>(Task<IJSObjectReference?> task) => (T)(object)(await task)!;
    }
    private sealed class HeldOwner : IJSObjectReference
    {
        private readonly TaskCompletionSource<object?> reconcile = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldReconcile { get; init; }
        public bool ReconcileStarted { get; private set; }
        public int DisposeCalls { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "reconcile") { ReconcileStarted = true; return HoldReconcile ? new(Wait<TValue>()) : ValueTask.FromResult(default(TValue)!); }
            if (identifier == "dispose") { DisposeCalls++; return ValueTask.FromResult(default(TValue)!); }
            throw new InvalidOperationException(identifier);
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
        public void ReleaseReconcile() => reconcile.TrySetResult(null);
        private async Task<T> Wait<T>() { await reconcile.Task; return default!; }
    }
}
