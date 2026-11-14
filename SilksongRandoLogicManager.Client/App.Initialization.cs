using Microsoft.JSInterop;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    private InitialCatalogueProgress initialProgress = new(InitialLoadingPhase.Neutral);

    // This synchronous release also fences promise rejection during ready/update
    // transitions, before their next render replaces the loading host.
    private void ReleaseBootstrapLoading()
    {
        try { if (JS is IJSInProcessRuntime native) native.InvokeVoid("logicInitialLoading.release"); }
        catch (JSException error) { Console.Error.WriteLine(error); }
    }

    [JSInvokable]
    public Task BootstrapFailed()
    {
        if (disposed || Runtime.Retired || ready || initialFailed) return Task.CompletedTask;
        initialFailed = true;
        initialLoadTimingRetired = true;
        ReleaseBootstrapLoading();
        return InvokeAsync(StateHasChanged);
    }

    private async Task FailInitialLoadingAsync(Exception exception)
    {
        if (Runtime.RetryRequired && !Runtime.Retired && exception is not SessionAccessException) Runtime.InstallationFailed();
        initialFailed = true;
        ReleaseBootstrapLoading();
        await FailAsync(exception, false, initialLoading: exception is not SessionAccessException);
    }

    private async Task ReportInitialProgressAsync(InitialCatalogueProgress value)
    {
        if (disposed || Runtime.Retired || ready || initialFailed) return;
        initialProgress = value;
        await InvokeAsync(StateHasChanged);
        if (module is not null) await module.InvokeVoidAsync("paintInitialProgress");
        lifetime.Token.ThrowIfCancellationRequested();
        Runtime.RequireActive();
    }
}
