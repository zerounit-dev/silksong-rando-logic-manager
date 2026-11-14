using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private static long nextFollowPage;
    private readonly long followPage = Interlocked.Increment(ref nextFollowPage);
    private long followRequestToken;
    private Task? followRegistration;
    private bool followRegistrationAttempted;
    private bool FollowReady => Current && workspace.IsAdmitted && nativeOwner is not null && Shell.CanUseRoom && !Shell.RoomSwitchPending && !WorkspaceBlocksDeparture();
    private bool OwnsFollow(long token, long page) => Current && page == followPage && token == followRequestToken;
    private async Task RegisterFollowAsync()
    {
        followRegistrationAttempted = true;
        followRegistration = JS.InvokeVoidAsync("archipelagoConnection.registerRoom", root, nativeReference, followPage).AsTask();
        await followRegistration;
    }
    private async Task RetireFollowAsync()
    {
        if (!followRegistrationAttempted) return;
        followRegistrationAttempted = false;
        Exception? failure = null;
        try { if (followRegistration is not null) await followRegistration; }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { failure = exception; }
        try { await JS.InvokeVoidAsync("archipelagoConnection.unregisterRoom", root); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (Exception exception) { failure ??= exception; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [JSInvokable]
    public async Task FollowArchipelagoRoomAsync(long token, string? previous, string? current, long page)
    {
        if (!Current || page != followPage || !followRegistrationAttempted || token <= followRequestToken) return;
        followRequestToken = token;
        if (!FollowReady || ArchipelagoRoomNames.Normalize(current) is not { } gameId || ArchipelagoRoomNames.Same(previous, current)) return;
        try
        {
            var value = await Catalogue.FindFollowRoomAsync(gameId, () => OwnsFollow(token, page), lifetime.Token);
            if (!OwnsFollow(token, page) || !FollowReady || value?.RoomId is not { } target || target == Opened.Identity.RoomId) return;
            if (!await JS.InvokeAsync<bool>("archipelagoConnection.followCurrent", token, root) || !OwnsFollow(token, page) || !FollowReady) return;
            if (!await nativeOwner!.InvokeAsync<bool>("followBoundary", token, nativeReference, page) || !OwnsFollow(token, page) || !FollowReady) return;
            if (!await JS.InvokeAsync<bool>("archipelagoConnection.followCurrent", token, root) || !OwnsFollow(token, page) || !FollowReady) return;
            Shell.NavigateRoomAction(this, $"/rooms/{target:D}", () => OwnsFollow(token, page) && FollowReady);
        }
        catch (OperationCanceledException) when (!Current) { }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) when (!Current) { }
        catch (Exception exception) { if (OwnsFollow(token, page)) await Shell.ReportRoomErrorAsync(exception, "Archipelago room follow could not complete. Existing room work has been retained."); }
    }
    [JSInvokable]
    public bool PreflightArchipelagoBoundary(long token, ArchipelagoFollowBoundary? native, long page)
    {
        if (!OwnsFollow(token, page) || !FollowReady) return false;
        if (native is null) return true;
        if (native.Kind == "header") return native.Field is "name" or "reference" or "game" or "contributors" or "comments";
        if (native.Kind is not ("subroom" or "transition" or "connection" or "check") || !Guid.TryParse(native.Client, out var client)) return false;
        var fields = native.Kind switch
        {
            "subroom" => new[] { "name", "reference", "notes" },
            "transition" => ["alias", "name", "source", "destination-room", "destination-alias", "unsafe-arrival", "requirements", "notes", "todo", "verification"],
            "connection" => ["alias", "name", "source", "destination", "requirements", "notes", "todo", "verification"],
            _ => ["type", "name", "subroom", "requirements", "notes", "todo", "verification"]
        };
        if (!fields.Contains(native.Field)) return false;
        try { return !workspace.Find(native.Kind, client).IsArchived; }
        catch (InvalidOperationException) { return false; }
    }
}
