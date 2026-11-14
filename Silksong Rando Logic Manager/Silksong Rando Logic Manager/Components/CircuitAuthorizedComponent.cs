using Microsoft.AspNetCore.Components;
using Silksong_Rando_Logic_Manager.Authentication;

namespace Silksong_Rando_Logic_Manager.Components;

/// <summary>
/// The retained application's Blazor event boundary (not a transport boundary).
/// Native JSInvokable entry points and service executions require their own guards.
/// No render-mode, component lifetime, or browser event listener is changed here.
/// </summary>
public abstract class CircuitAuthorizedComponent : ComponentBase, IHandleEvent
{
    [Inject] private CircuitCallbackAccess EventCallbackAccess { get; set; } = default!;
    [Inject] private LiveAccess EventLiveAccess { get; set; } = default!;
    [Inject] private Services.DiagnosticState Diagnostics { get; set; } = default!;
    [Inject] private ILogger<CircuitAuthorizedComponent> Logger { get; set; } = default!;
    protected Task<bool> AdmitCircuitCallbackAsync(string policy = AccessPolicies.Normal) => EventCallbackAccess.AdmitAsync(policy);
    protected async Task RequireCircuitCallbackAsync() { await EventLiveAccess.RequireAsync(); }

    async Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? argument)
    {
        try
        {
            // Retain ComponentBase's immediate and asynchronous completion render
            // semantics, including ShouldRender and cancellation.
            var work = callback.InvokeAsync(argument);
            var awaitCompletion = work.Status != TaskStatus.RanToCompletion && work.Status != TaskStatus.Canceled;
            StateHasChanged();
            if (!awaitCompletion) return;
            try { await work; }
            catch when (work.IsCanceled) { return; }
            StateHasChanged();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Component callback failed in {Component}.", GetType().Name);
            Diagnostics.Show($"{GetType().Name}: callback failed.\n{exception}");
        }
    }
}
