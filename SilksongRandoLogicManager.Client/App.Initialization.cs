using Microsoft.JSInterop;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    private InitialCatalogueProgress initialProgress = new(InitialLoadingPhase.Neutral);
    private enum StartupFailure { Script, CacheDeadline, CacheRead, Unavailable, ServerError, Unknown }
    private StartupFailure? startupFailure;
    private string StartupFailureCopy => startupFailure switch
    {
        StartupFailure.Script => "Startup script unavailable. Try refreshing.",
        StartupFailure.CacheDeadline => "Cache worker unresponsive. Close all logic manager tabs and try again.",
        StartupFailure.CacheRead => "Couldn’t read the cache. Try refreshing.",
        StartupFailure.Unavailable => "Server unavailable. Try again shortly.",
        StartupFailure.ServerError => "Server error. Try again shortly.",
        _ => "Unknown startup error. Try refreshing."
    };

    // This synchronous release also fences promise rejection during ready/update
    // transitions, before their next render replaces the loading host.
    private string? ReleaseBootstrapLoading()
    {
        try { if (JS is IJSInProcessRuntime native) return native.Invoke<string?>("logicInitialLoading.release"); }
        catch (JSException error) { Console.Error.WriteLine(error); }
        return null;
    }

    [JSInvokable]
    public Task BootstrapFailed(string category)
    {
        if (disposed || Runtime.Retired || ready || initialFailed || category is not ("SF1" or "SF6"))
            return Task.CompletedTask;
        ReleaseBootstrapLoading();
        initialLoadTimingRetired = true;
        if (Runtime.RetryRequired) Runtime.InstallationFailed();
        else LatchStartupFailure(BootstrapCategory(category));
        return InvokeAsync(StateHasChanged);
    }

    private static StartupFailure BootstrapCategory(string category) => category == "SF1" ? StartupFailure.Script : StartupFailure.Unknown;

    private void LatchStartupFailure(StartupFailure category)
    {
        startupFailure ??= category;
        initialFailed = true;
    }

    private async Task FailInitialLoadingAsync(Exception exception, bool requiredStartupWork = true)
    {
        if (disposed || Runtime.Retired) { Console.Error.WriteLine(exception); return; }
        if (ready) { await FailAsync(exception, false); return; }
        var accessDenied = exception is SessionAccessException;
        if (Runtime.RetryRequired && !accessDenied) Runtime.InstallationFailed();
        var bootstrap = ReleaseBootstrapLoading();
        if (accessDenied)
        {
            initialFailed = true;
            startupFailure = null;
        }
        else if (!Runtime.Retired && !initialFailed)
        {
            if (bootstrap is not null) initialLoadTimingRetired = true;
            LatchStartupFailure(bootstrap is not null ? BootstrapCategory(bootstrap) : ClassifyStartupFailure(exception, requiredStartupWork));
        }
        await FailAsync(exception, false, initialLoading: !accessDenied);
    }

    private static StartupFailure ClassifyStartupFailure(Exception exception, bool requiredStartupWork)
    {
        if (StartupHttpEvidence.ForFailure(exception) is { } http)
        {
            if (http.NoResponse || http.Status == 503) return StartupFailure.Unavailable;
            if (http.Status is >= 500 and <= 599) return StartupFailure.ServerError;
        }
        Exception? cause = exception;
        for (var depth = 0; depth < 4 && cause is not null; depth++, cause = cause.InnerException)
        {
            if (cause is OperationCanceledException or ClientRetiredException) break;
            if (cause is not LocalCacheFailure local) continue;
            return local.Diagnostic switch
            {
                { Origin: "Tab", Stage: "timeout" } when requiredStartupWork => StartupFailure.CacheDeadline,
                { Origin: "Owner", Stage: "request", Operation: "state" or "query" } => StartupFailure.CacheRead,
                _ => StartupFailure.Unknown
            };
        }
        return StartupFailure.Unknown;
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
