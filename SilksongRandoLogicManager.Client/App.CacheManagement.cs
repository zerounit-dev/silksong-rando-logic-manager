using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SilksongRandoLogicManager.Client;

public partial class App
{
    private ElementReference cacheDialog;
    private bool cacheOpen, cacheReading, cacheReplacing, cacheDialogPending;
    private object? cacheInspectionOwner;
    private CacheInspection? cacheInspection;
    private CacheSynchronizationObservation? cacheSynchronization;
    private DateTime? cacheSnapshotUtc;
    private string? cacheFeedback;
    private sealed record CacheReloadAction(object NavigationOwner, object? Visit, string Url);
    private CacheReloadAction? cacheReloadAction;

    private bool OwnsCacheReload(CacheReloadAction action) => CanInteract
        && ReferenceEquals(cacheReloadAction, action) && ReferenceEquals(action.NavigationOwner, creationNavigationOwner)
        && ReferenceEquals(action.Visit, roomWorkspaceOwner) && action.Url == Navigation.Uri;

    private bool CacheReplacementSafe(CacheReloadAction action)
    {
        if (!OwnsCacheReload(action) || errorOpen || SidebarFeatureLocked || requirementPageLock is not null || RoomDepartureBlocked) return false;
        if (action.Visit is null) return true;
        // A missing probe/observer is not a certificate. This does not alter ordinary navigation.
        return roomActionState?.Invoke() is { Active: true, Failed: false, Unfinished: false }
            && roomBlocksUnattendedReplacement is not null && !roomBlocksUnattendedReplacement();
    }

    private async Task OpenCacheManagementAsync()
    {
        if (!ready || Locked || errorOpen || module is not IJSInProcessObjectReference native) return;
        var owner = new object();
        cacheInspectionOwner = owner;
        cacheOpen = cacheReading = cacheDialogPending = true;
        cacheInspection = null; cacheSnapshotUtc = null; cacheSynchronization = Browser.LastSuccessfulSynchronization; cacheFeedback = null;
        try
        {
            native.InvokeVoid("rememberInitiator");
            StateHasChanged();
            var inspected = await Browser.InspectAsync(lifetime.Token);
            if (disposed || !cacheOpen || !ReferenceEquals(cacheInspectionOwner, owner)) return;
            cacheInspection = inspected;
            cacheSnapshotUtc = DateTime.UtcNow;
            cacheSynchronization = Browser.LastSuccessfulSynchronization;
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (!disposed && cacheOpen && ReferenceEquals(cacheInspectionOwner, owner))
                await FailAsync(new ReadFailure("Cache information could not be read. No server check or cache replacement was requested.", null, error), false);
        }
        finally
        {
            if (ReferenceEquals(cacheInspectionOwner, owner)) cacheReading = false;
            if (!disposed) StateHasChanged();
        }
    }

    private async Task RenderCacheManagementAsync()
    {
        if (cacheDialogPending && cacheOpen && !errorOpen && !disposed)
        {
            var shown = await module!.InvokeAsync<bool>("showDialog", cacheDialog, errorDialog);
            if (!disposed && shown && cacheOpen) cacheDialogPending = false;
        }
        if (!disposed) await module!.InvokeVoidAsync("freezeDialogs", cacheReplacing, cacheDialog);
    }

    private Task CloseCacheManagement() => ModalActionAsync(() =>
    {
        if (!cacheOpen || cacheReplacing || errorOpen || disposed) return Task.CompletedTask;
        ((IJSInProcessObjectReference)module!).InvokeVoid("closeDialogs", cacheDialog);
        cacheOpen = cacheReading = cacheDialogPending = false; cacheInspectionOwner = null;
        restoreInitiatorPending = true;
        StateHasChanged();
        return Task.CompletedTask;
    });

    private Task ClearCacheAndReloadAsync() => ModalActionAsync(ClearCacheAndReloadCoreAsync);
    private async Task ClearCacheAndReloadCoreAsync()
    {
        if (!cacheOpen || cacheReplacing || errorOpen || disposed) return;
        // Release inspection exclusion BEFORE the existing confirmation/dynamic wait.
        ((IJSInProcessObjectReference)module!).InvokeVoid("closeDialogs", cacheDialog);
        cacheOpen = cacheReading = cacheDialogPending = false; cacheInspectionOwner = null;
        var existing = cacheReloadAction;
        var action = existing ?? new CacheReloadAction(creationNavigationOwner, roomWorkspaceOwner, Navigation.Uri);
        cacheReloadAction = action;
        StateHasChanged();
        try
        {
            if (action.Visit is null) await ExecuteCacheReplacementAsync();
            else await AdmitRoomDepartureAsync(action.Url, null, DepartureMode.CacheReload, lifetime.Token,
                valid: () => OwnsCacheReload(action));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (OwnsCacheReload(action)) await FailAsync(new ReadFailure("The cache recovery request could not be processed. Local work has been retained; try again after recovery.", null, error), false);
        }
        finally
        {
            // A duplicate invocation only focuses the existing request; it cannot retire it.
            if (existing is null && ReferenceEquals(cacheReloadAction, action) && !cacheReplacing)
            { cacheReloadAction = null; restoreInitiatorPending = true; }
            if (!disposed) StateHasChanged();
        }
    }

    private async Task<bool> ExecuteCacheReplacementAsync()
    {
        if (cacheReloadAction is not { } action || cacheReplacing) return false;
        if (!CacheReplacementSafe(action))
        {
            departureFeedback = "Cache recovery was not started. Save or discard unfinished local work, then try Clear cache and reload again.";
            StateHasChanged(); return false;
        }
        var installed = false;
        var reloadDispatched = false;
        cacheOpen = cacheReplacing = true; cacheDialogPending = false; cacheFeedback = null;
        StateHasChanged();
        try
        {
            var native = (IJSInProcessObjectReference)module!;
            // Native blocking/busy ownership exists before the first replacement await.
            if (!native.Invoke<bool>("showDialog", cacheDialog, errorDialog))
            { cacheDialogPending = true; return false; }
            native.InvokeVoid("freezeDialogs", true, cacheDialog);
            var result = await Browser.ReplaceForReloadAsync(() => CacheReplacementSafe(action), lifetime.Token);
            installed = result == CacheReplacementOutcome.Installed;
            if (disposed) return false;
            if (!installed)
            {
                cacheFeedback = result switch
                {
                    CacheReplacementOutcome.SharedStateChanged => "The shared cache changed while the fresh copy was downloading. The changed cache was kept. Try Clear cache and reload again to make a new attempt.",
                    CacheReplacementOutcome.PublicationsPending => "Cache recovery was canceled because results from current work are still being added to the cache. Wait for that work to finish, then try again.",
                    _ => "Cache recovery was canceled because the page or local work changed. No replacement was requested. Finish local work, then try again."
                };
                return false;
            }
            cacheInspection = null; cacheSnapshotUtc = null;
            if (!CacheReplacementSafe(action) || !Browser.CanAdoptLocal)
            {
                cacheFeedback = "The shared cache was replaced successfully, but this page was not reloaded because the page or local work changed. Existing local work has been retained.";
                return false;
            }
            // Fresh exact URL/visit check above and synchronous dispatch below: no await gap.
            // reload(), unlike assign(the same URL), also reloads a URL containing a fragment.
            ((IJSInProcessRuntime)JS).InvokeVoid("location.reload");
            reloadDispatched = true;
            return true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (!disposed)
            {
                var message = installed
                    ? "The shared cache was replaced successfully, but this page could not be reloaded. Local work has been retained; reload was not retried."
                    : error is CacheReplacementFailure { InstallationDispatched: true }
                        ? "Cache replacement failed or its result could not be confirmed. The new copy may have been installed, or the cache may be unusable. Local work was retained; no rollback or automatic retry was attempted."
                        : "A fresh cache copy could not be downloaded or accepted for replacement. This attempt did not start replacing the cache. Local work has been retained.";
                cacheFeedback = message;
                await FailAsync(new ReadFailure(message, null, error), false);
            }
            return false;
        }
        finally
        {
            if (!reloadDispatched)
            {
                cacheReplacing = false;
                if (!disposed && module is IJSInProcessObjectReference native)
                {
                    try { native.InvokeVoid("freezeDialogs", false, cacheDialog); }
                    catch (Exception cleanup) { Console.Error.WriteLine(cleanup); } // Preserve originating outcome; render also releases busy fields.
                }
            }
            if (!disposed) StateHasChanged();
        }
    }

    private void RetireCacheManagement()
    { cacheInspectionOwner = null; cacheReloadAction = null; }

    private static string CacheUtc(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
    private static string CacheShortUtc(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
    private static bool CacheInteger(string? value, out System.Numerics.BigInteger number) =>
        System.Numerics.BigInteger.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out number) && number.Sign >= 0;
    private static string CacheNumber(string? value) => CacheInteger(value, out var number)
        ? number.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "Unavailable";
    private static string CacheSize(string? value)
    {
        if (!CacheInteger(value, out var bytes)) return "Unavailable";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB"];
        var unit = 0;
        var divisor = System.Numerics.BigInteger.One;
        while (unit < units.Length - 1 && bytes >= divisor * 1024) { divisor *= 1024; unit++; }
        if (unit == 0) return $"{CacheNumber(value)} B";
        // Integer hundredths only: no floating-point loss, even beyond UInt64.
        var scaled = System.Numerics.BigInteger.DivRem(bytes * 100, divisor, out var remainder);
        if (remainder * 2 >= divisor) scaled++;
        var whole = System.Numerics.BigInteger.DivRem(scaled, 100, out var fraction);
        var decimals = fraction.ToString("D2", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0');
        return $"{(remainder.IsZero ? "" : "≈ ")}{whole.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}{(decimals.Length == 0 ? "" : "." + decimals)} {units[unit]}";
    }
    private static string CacheTableName(string table) => table switch
    {
        "RoomGroups" => "Room groups", "Rooms" => "Rooms", "Subrooms" => "Subrooms",
        "RoomTransitions" => "Room transitions", "SubroomConnections" => "Subroom connections", "CheckLocations" => "Check locations",
        "Maps" => "Maps", "MapZones" => "Map zones", "MapScenes" => "Map scenes", "MapChunks" => "Map chunks", "MapOverlays" => "Map overlays",
        "RequirementPredicates" => "Requirement predicates", "RequirementItems" => "Requirement items", "RequirementCrests" => "Requirement crests",
        "RequirementMethods" => "Requirement methods", "RequirementCountables" => "Requirement countables", "RequirementSouls" => "Requirement souls",
        _ => "Unknown table"
    };
}
