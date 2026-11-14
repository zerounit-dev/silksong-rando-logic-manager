using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    private IJSObjectReference? updateTransport;
    private ElementReference updateHeading;
    private bool updateFocusPending;
    private bool runtimeRetirementHandled;
    private string UpdateTitle => Runtime.Discarding ? "Reloading application" : Runtime.InstallationFailure switch
    {
        ClientRuntime.UpdateFailure.RequiredCode => "Application code unavailable",
        ClientRuntime.UpdateFailure.Preparation => "Application not ready",
        ClientRuntime.UpdateFailure.ReloadStart => "Reload could not start",
        _ => "Reload required"
    };
    private string UpdateExplanation => Runtime.InstallationFailure switch
    {
        ClientRuntime.UpdateFailure.RequiredCode => "Required application code could not be loaded. Reload to try loading the application again.",
        ClientRuntime.UpdateFailure.Preparation => "The application could not finish preparing a usable local installation. Try again to reload and prepare it again.",
        ClientRuntime.UpdateFailure.IntentRead => "This tab could not check whether an application reload was requested. Try again to reload the application.",
        ClientRuntime.UpdateFailure.ReloadStart => "This tab could not start the application reload. Try again.",
        _ => Runtime.ServerVersion is { } server && server != ApplicationBuild.Version
            ? "This tab is still running a different version of the app from the server. Reload to try loading a matching version."
            : "This tab cannot continue with its current application code. Reload to try loading the application again."
    };
    private string UpdateAction => Runtime.Discarding ? "Reloading…"
        : Runtime.WorkAtRetirement ? "Discard local work and reload"
        : Runtime.InstallationFailure is not null ? "Try again" : "Reload application";
    private readonly Dictionary<object, Func<bool>> updateRiskOwners = new(ReferenceEqualityComparer.Instance);
    public void RegisterClientUpdateRisk(object owner, Func<bool> observe) => updateRiskOwners[owner] = observe;
    public void ReleaseClientUpdateRisk(object owner) => updateRiskOwners.Remove(owner);
    private bool CaptureClientUpdateRisk()
    {
        var risk = disposed || busy || editor is not null || Catalogue.MutationActive
            || cacheReplacing || sceneOperationOwner is not null
            || sceneInverse is not null || sceneInverseBusy || sceneExitQueue.Count != 0;
        if (dragOwner is not null)
            risk |= dragOwner is not IJSInProcessObjectReference nativeDrag || nativeDrag.Invoke<bool>("hasClientUpdateWork");
        foreach (var observe in updateRiskOwners.Values.ToArray()) risk |= observe();
        return risk;
    }
    private Action RequiredCodeFailed => Runtime.RequiredCodeFailed;
    private void RuntimeChanged()
    {
        if (disposed) return;
        // This callback is synchronous: admission is already closed before cleanup.
        updateFocusPending = true;
        if (Runtime.Retired && !runtimeRetirementHandled)
        {
            runtimeRetirementHandled = true;
            ReleaseBootstrapLoading();
            RetireRoomEntryNavigation();
            ready = false; initialCacheCheckPending = false; mountPending = false;
            if (Runtime.AutomaticUpdateInProgress)
                initialProgress = new(InitialLoadingPhase.Neutral);
            editorSequence++; errorOpen = false; errorInstance = null;
            creationNavigationOwner = new();
            updateFocusPending = true;
            Catalogue.RetireRuntime();
            try { Browser.RetireRuntime(); } catch (Exception error) { Console.Error.WriteLine(error); }
            try { if (updateTransport is IJSInProcessObjectReference control) control.InvokeVoid("retire"); }
            catch (JSException error) { Console.Error.WriteLine(error); }
            try { if (module is IJSInProcessObjectReference native) native.InvokeVoid("retireRuntime"); }
            catch (JSException error) { Console.Error.WriteLine(error); }
            // Removing Router and dialogs disposes existing feature/native owners.
            // Outstanding admitted outcomes keep their original receipt semantics.
            _ = RetireNativeOwnersAsync();
        }
        StateHasChanged();
    }
    [JSInvokable]
    public void ApplicationVersionRejected(string? serverVersion) => Runtime.Retire(serverVersion);
    private async Task RetireNativeOwnersAsync()
    {
        try
        {
            await ReleaseOwnersAsync();
        }
        catch (Exception error) { Console.Error.WriteLine(error); }
    }
    private Task UpdateClientAsync() => UpdateClientCoreAsync(false);
    private async Task UpdateClientCoreAsync(bool automatic)
    {
        if (!Runtime.Retired || Runtime.Discarding) return;
        // The pre-retirement certificate or the single explicit action authorizes discard.
        await Runtime.ReloadForUpdateAsync(() =>
        { if (module is IJSInProcessObjectReference native) native.InvokeVoid("discardForClientUpdate"); }, automatic);
    }
}
