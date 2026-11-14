using Microsoft.JSInterop;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    public bool CanAuthor => CanInteract && Access.CanAuthor;
    public bool CanInteract => !disposed && !Runtime.Retired && !Runtime.Installing;
    private async Task LogoutAsync()
    {
        if (disposed || Locked || errorOpen || !CanInteract || Access.Session?.Bypass != false || module is not IJSInProcessObjectReference) return;
        if (roomWorkspaceOwner is null) await ExecuteLogoutAsync();
        else await AdmitRoomDepartureAsync("/account/logout", null, DepartureMode.Logout, lifetime.Token,
            () => CanInteract && Access.Session?.Bypass == false);
    }
    private Task<bool> ExecuteLogoutAsync()
    {
        if (!CanInteract || Access.Session is not { Bypass: false } session || module is not IJSInProcessObjectReference native)
            return Task.FromResult(false);
        // Exact-action/whole-queue certification immediately precedes native
        // departure. There is no mounted response protocol or preservation seal.
        native.InvokeVoid("submitLogout", session.AntiforgeryToken, session.WorkspaceKey!);
        return Task.FromResult(true);
    }
    private void AccessChanged()
    {
        if (disposed) return;
        StateHasChanged();
    }
}
