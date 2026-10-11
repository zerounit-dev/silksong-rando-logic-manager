using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2;

public partial class RoomEditorV2Page
{
    private DotNetObjectReference<RoomEditorV2Page>? archipelagoCallback;
    private ElementReference? archipelagoRegistrationRoot;
    private Task? archipelagoRegistrationAttempt;
    private long registeredFollowPage = -1, followRequestToken;

    private async Task RetireArchipelagoRegistrationAsync()
    {
        // Detach before awaiting: registration may still be in flight, and a
        // retired completion must never publish ownership for a replacement root.
        var root = archipelagoRegistrationRoot;
        var attempt = archipelagoRegistrationAttempt;
        var callback = archipelagoCallback;
        archipelagoRegistrationRoot = null;
        archipelagoRegistrationAttempt = null;
        archipelagoCallback = null;
        registeredFollowPage = -1;
        Exception? failure = null;
        if (root is { } registeredRoot)
        {
            // Retire after the attempt settles, even if its acknowledgement failed.
            // A static instance has no attempted browser acquisition and skips JS.
            try { if (attempt is not null) await attempt; }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { failure = ex; }
            try { await JS.InvokeVoidAsync("archipelagoConnection.unregisterRoom", registeredRoot); }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { failure ??= ex; }
        }
        try { callback?.Dispose(); }
        catch (Exception ex) { failure ??= ex; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private bool FollowPageReady => !disposed && childInteractionOwner is not null &&
        displayedRoomId == RoomId && refresh?.CurrentRoomId == RoomId && view is not null &&
        !refresh.IsLoading && !refresh.RequiresCheckRoomReload && !applicationNavigationActive &&
        !meaningfulPendingDraft && modalState is null && Diagnostics.ErrorDetails is null &&
        roomMapLinkModalState == AreaMapPanePresentation.RoomMapLinkModalState.Closed &&
        commandGate.CurrentCount != 0 && !sceneActionPending && !recheckingRoomLogic &&
        headerSaveStatus.State == V2RoomHeaderSaveState.Saved &&
        subroomTable?.FollowBlocked == false && transitionTable?.FollowBlocked == false &&
        connectionTable?.FollowBlocked == false && checkTable?.FollowBlocked == false;

    private bool OwnsFollow(long token, long page, Guid source) => !disposed && followRequestToken == token &&
        childInteractionOwnerGeneration == page && RoomId == source && displayedRoomId == source && refresh?.CurrentRoomId == source;

    [JSInvokable]
    public async Task FollowArchipelagoRoomAsync(long token, string? previous, string? current, long page)
    {
        if (disposed || page != childInteractionOwnerGeneration || registeredFollowPage != page || token <= followRequestToken) return;
        followRequestToken = token;
        var source = RoomId;
        if (!FollowPageReady || ArchipelagoRoomLookup.Normalize(current) is not { } gameId || ArchipelagoRoomLookup.Same(previous, current)) return;
        try
        {
            var target = await ArchipelagoRooms.FindAsync(gameId);
            if (!OwnsFollow(token, page, source) || !FollowPageReady || target is null || target == source) return;
            if (!await JS.InvokeAsync<bool>("archipelagoConnection.followCurrent", token, pageRoot) || !OwnsFollow(token, page, source) || !FollowPageReady) return;
            if (!await childInteractionOwner!.InvokeAsync<bool>("followBoundary", token, archipelagoCallback, page) || !OwnsFollow(token, page, source) || !FollowPageReady) return;
            // Normal blur saving is independent; AP does not observe or await it.
            // Existing server-owned navigation rejects whatever is busy now.
            if (!await JS.InvokeAsync<bool>("archipelagoConnection.followCurrent", token, pageRoot) || !OwnsFollow(token, page, source) || !FollowPageReady) return;
            TryNavigateToRoom(source, refresh!.RefreshGeneration, target.Value, null);
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) when (disposed || !OwnsFollow(token, page, source)) { }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Archipelago room follow could not complete.");
            if (OwnsFollow(token, page, source)) Diagnostics.Show(exception.ToString());
        }
    }

    [JSInvokable]
    public bool PreflightArchipelagoBoundary(long token, ArchipelagoFollowBoundary? native, long page)
    {
        if (!OwnsFollow(token, page, RoomId) || !FollowPageReady) return false;
        return native?.Kind switch
        {
            null => true,
            "header" => (native.Field is "name" or "reference" or "game" or "contributors" or "comments") && headerDraft is not null,
            "subroom" => subroomTable!.AdmitsFollowBoundary(native),
            "transition" => transitionTable!.AdmitsFollowBoundary(native),
            "connection" => connectionTable!.AdmitsFollowBoundary(native),
            "check" => checkTable!.AdmitsFollowBoundary(native),
            _ => false
        };
    }
}
