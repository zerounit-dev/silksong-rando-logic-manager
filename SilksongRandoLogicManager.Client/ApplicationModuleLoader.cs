using Microsoft.JSInterop;

namespace SilksongRandoLogicManager.Client;

internal static class ApplicationModuleLoader
{
    internal static async Task<IJSObjectReference> ImportAsync(IJSRuntime js, ClientRuntime runtime, string name)
    {
        runtime.RequireActive();
        IJSObjectReference module;
        try { module = await ApplicationModules.ImportAsync(js, name); }
        catch (JSException) { runtime.RequiredCodeFailed(); throw; }
        if (runtime.Retired) { await module.DisposeAsync(); runtime.RequireActive(); }
        return module;
    }
}
