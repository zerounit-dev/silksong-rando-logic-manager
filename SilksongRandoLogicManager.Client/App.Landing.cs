using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    private object? landingOwner;
    private LandingAreaMapView? initialLanding;
    internal static bool IsHomeRoute(string path) => path.Split('?', '#')[0].Trim('/').Length == 0;
    public bool CanUseLanding(object owner) => CanInteract && !errorOpen && !RoomEntryPreparing && !busy && editor is null && editorLoading is null
        && requirementPageLock is null && sceneOperationOwner is null && (landingOwner is null || ReferenceEquals(landingOwner, owner));
    public bool LockLanding(object owner)
    {
        if (!CanUseLanding(owner)) return false;
        landingOwner = owner; StateHasChanged(); return true;
    }
    public async Task ReleaseLandingAsync(object owner)
    {
        if (!ReferenceEquals(landingOwner, owner)) return;
        landingOwner = null;
        if (!disposed) { StateHasChanged(); await DrainRefreshAsync(); }
    }
    public Task DrainLandingAsync() => DrainRefreshAsync();
    public LandingAreaMapView? TakeInitialLanding()
    { var value = initialLanding; initialLanding = null; return value; }
    private async Task LoadInitialLandingAsync()
    {
        if (!IsHomeRoute(Navigation.ToBaseRelativePath(Navigation.Uri))) return;
        await ReportInitialProgressAsync(new(InitialLoadingPhase.Map));
        initialLanding = await Catalogue.LoadLandingAsync(() => !disposed && IsHomeRoute(Navigation.ToBaseRelativePath(Navigation.Uri)), lifetime.Token,
            PreparationContext.Startup);
    }
    public async Task ReportLandingErrorAsync(Exception exception, string message)
    {
        Console.Error.WriteLine(exception);
        await FailAsync(new ReadFailure(message, null, exception), false);
        if (!disposed) StateHasChanged();
    }
    public Task RememberLandingInitiatorAsync() => module!.InvokeVoidAsync("rememberInitiator").AsTask();
    public ValueTask<bool> ShowLandingDialogAsync(ElementReference dialog) => module!.InvokeAsync<bool>("showDialog", dialog, errorDialog);
    public Task FreezeLandingDialogAsync(ElementReference dialog, bool value) => module!.InvokeVoidAsync("freezeDialogs", value, dialog).AsTask();
    public async Task CloseLandingDialogAsync(ElementReference dialog)
    {
        if (module is null || disposed) return;
        await module.InvokeVoidAsync("closeDialogs", dialog);
        restoreInitiatorPending = true;
        StateHasChanged();
    }
}
