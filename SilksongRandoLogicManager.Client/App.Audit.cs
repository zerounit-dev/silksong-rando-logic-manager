using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Components.RoomAudit;
using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    private bool sidebarMounting;
    private bool auditErrorFocusPending;
    public bool AuditRoute => AuditRoom(Navigation.ToBaseRelativePath(Navigation.Uri)) is not null;
    internal static Guid? AuditRoom(string path)
    {
        var parts = path.Split('?', '#')[0].Trim('/').Split('/');
        return parts is ["rooms", var id, "audit"] && Guid.TryParse(id, out var room) ? room : null;
    }
    public AuditHost CreateAuditHost() => new BrowserAuditHost(this);
    public async Task ReportAuditErrorAsync(Exception exception)
    {
        await FailAsync(exception, false);
        if (!disposed) StateHasChanged();
    }
    private async Task ReconcileSidebarOwnerAsync()
    {
        if (AuditRoute)
        {
            if (dragOwner is null) return;
            var previous = dragOwner; dragOwner = null;
            try { try { await previous.InvokeVoidAsync("dispose"); } finally { await previous.DisposeAsync(); } }
            catch (Exception exception) { if (!disposed) { await FailAsync(exception, false); StateHasChanged(); } }
            return;
        }
        if (dragOwner is not null || sidebarMounting || !CanInteract || callback is null) return;
        sidebarMounting = true;
        try
        {
        dragModule ??= await ApplicationModuleLoader.ImportAsync(JS, Runtime, "sidebar-drag.js");
            if (!CanInteract || AuditRoute) return;
            var mounted = await dragModule.InvokeAsync<IJSObjectReference>("mount", sidebarRoot, callback, ownerId);
            if (!CanInteract || AuditRoute)
            { try { await mounted.InvokeVoidAsync("dispose"); } finally { await mounted.DisposeAsync(); } }
            else dragOwner = mounted;
        }
        catch (Exception exception) { if (!disposed) { await FailAsync(exception, false); StateHasChanged(); } }
        finally { sidebarMounting = false; }
    }
    private sealed class BrowserAuditHost(App app) : AuditHost
    {
        public override bool ReloadAvailable => true;
        public override bool PerformanceLogging => true;
        // The retained source must not become inert just because its destination
        // is being read. Exclude ONLY that owned lease, not other shell locks.
        public override bool Blocked => !app.CanInteract || app.errorOpen || app.SidebarLocked
            || app.requirementPageLock is not null || app.cacheOpen || app.roomSwitch is not null
            || app.roomLoadOwner is not null && !ReferenceEquals(app.roomLoadOwner, app.executingRoomEntry);
        public override ElementReference ExternalModal => app.errorDialog;
        public override string RoomUrl(Guid id) => $"/rooms/{id:D}";
        public override string AuditUrl(Guid id) => $"/rooms/{id:D}/audit";
        public override string BackLabel => "back to room";
        public override Task<RoomAuditSnapshot?> LoadAsync(Guid roomId, Func<bool> owns, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return app.Catalogue.LoadAuditAsync(roomId, owns, token);
        }
        public override Task<AuditRoomCatalogue> LoadCatalogueAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(app.Catalogue.CaptureAuditCatalogue()); }
        public override Task<T> ExecuteAsync<T>(Func<T> work, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = work();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
