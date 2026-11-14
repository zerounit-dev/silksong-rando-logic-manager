using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Client.Components;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Pages;

public partial class Home
{
    [Inject] public ClientRuntime Runtime { get; set; } = default!;
    [CascadingParameter] public App Shell { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "editMapLinksForRoom")] public Guid? EditMapLinksForRoom { get; set; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true };
    private readonly CancellationTokenSource lifetime = new();
    private LandingAreaMapView? landing;
    private MapLinksDialog? mapLinks;
    private bool linksOpen;
    private OverlayDraft? overlayDraft;
    private Guid? openedQuery;
    private string pendingAction = "";
    private string? feedback;
    private bool disposed, loading, openingLinks, pending, showDialogPending, calibrationOwned;
    private long generation;
    private string? observedMapState;
    private ElementReference overlayDialog, calibrationSvg;
    private bool Current => !disposed && Shell.CanInteract && App.IsHomeRoute(Navigation.ToBaseRelativePath(Navigation.Uri));
    private bool ToolsBlocked => loading || openingLinks || pending || !Shell.CanUseLanding(this) || linksOpen || overlayDraft is not null;
    protected override async Task OnInitializedAsync()
    {
        Shell.RegisterClientUpdateRisk(this, () => pending || overlayDraft is not null);
        Catalogue.Changed += LocalMapChanged;
        landing = Shell.TakeInitialLanding();
        if (landing is null) await RefreshMapAsync(false);
    }
    private void LocalMapChanged()
    {
        if (!Current || overlayDraft is not null || pending || Catalogue.ConfirmedMapStateId is not { } state || state == observedMapState) return;
        _ = AdoptLocalMapAsync(generation);
    }
    private async Task AdoptLocalMapAsync(long operation)
    {
        bool Owns() => Current && operation == generation;
        var targetState = Catalogue.ConfirmedMapStateId;
        try
        {
            var value = await Catalogue.LoadLandingAsync(Owns, lifetime.Token);
            if (Owns() && overlayDraft is null && !pending && value is not null)
            { landing = value; observedMapState = targetState; StateHasChanged(); }
        }
        catch (OperationCanceledException) when (!Owns()) { }
        catch (Exception error) { if (Owns()) await Shell.ReportLandingErrorAsync(error, "The map could not be refreshed. The current map, if available, has been retained."); }
    }
    private async Task OpenQueryLinksAsync()
    {
        if (EditMapLinksForRoom is { } id && openedQuery != id && Current && !ToolsBlocked && mapLinks is not null)
        {
            openedQuery = id;
            await OpenLinksAsync(new(id, null));
            if (Current) StateHasChanged();
        }
    }
    private async Task RefreshMapAsync(bool explicitRefresh = true)
    {
        if (!Current || loading || linksOpen || overlayDraft is not null || !Shell.LockLanding(this)) return;
        using var timing = OperationTiming.Start("map.home.combined-read-decode-preparation");
        var operation = ++generation; loading = true;
        try
        {
            var value = explicitRefresh
                ? await Catalogue.RefreshLandingMapAsync(() => Current && operation == generation, lifetime.Token)
                : await Catalogue.LoadLandingAsync(() => Current && operation == generation, lifetime.Token);
            if (Current && operation == generation && value is not null) { landing = value; timing.End(); }
        }
        catch (Exception exception) { if (Current && operation == generation) await Shell.ReportLandingErrorAsync(exception, "The map could not be refreshed. The current map, if available, has been retained."); }
        finally { if (operation == generation) loading = false; await Shell.ReleaseLandingAsync(this); }
    }
    private void NavigateRoom(AreaMapSurface.LinkedMapRoomIntent intent)
    { if (Current && !ToolsBlocked && !Shell.NavigationBlocked && !Shell.GeneralErrorOpen) Navigation.NavigateTo($"/rooms/{intent.TargetRoomId}"); }
    private async Task OpenLinksAsync(AreaMapSurface.MapLinkContextIntent intent)
    {
        if (!Current || ToolsBlocked || mapLinks is null || !Shell.LockLanding(this)) return;
        using var timing = OperationTiming.Start("map.home.open-links");
        openingLinks = true;
        StateHasChanged();
        try
        {
            await Shell.RememberLandingInitiatorAsync();
            if (Current) linksOpen = await mapLinks.OpenAsync(intent);
            timing.End(!Current ? TimingOutcome.Retired : linksOpen ? TimingOutcome.Complete : TimingOutcome.FailedOrRetired);
        }
        catch (Exception exception) { if (Current) await Shell.ReportLandingErrorAsync(exception, "Map links could not be loaded."); }
        finally { openingLinks = false; if (!linksOpen) await Shell.ReleaseLandingAsync(this); if (Current) StateHasChanged(); }
    }
    private async Task LinksClosedAsync(bool changed)
    {
        linksOpen = false;
        await Shell.ReleaseLandingAsync(this);
    }
    private async Task RunCommandAsync(string action, string path, object command)
    {
        if (!Shell.CanAuthor || !Current || pending || !Shell.CanUseLanding(this) || overlayDraft is null) return;
        using var timing = OperationTiming.Start("map.calibration.command");
        using var outcome = Runtime.ObserveMutation();
        pending = true; pendingAction = action; feedback = null;
        var operation = ++generation; var acknowledged = false;
        StateHasChanged();
        try
        {
            await Shell.FreezeLandingDialogAsync(overlayDialog, true);
            using var admissionTiming = OperationTiming.Start("map.calibration.admission");
            await Catalogue.MutateAsync(async admission =>
            {
                admissionTiming.End();
                using var sendTiming = OperationTiming.Start("map.calibration.buffered-send", parent: timing.Id);
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v3/" + path) { Content = JsonContent.Create(command, command.GetType(), options: Json) };
                var cacheRequest = admission.BindCache(request);
                outcome.Dispatched();
                using var response = await Http.SendAsync(request, CancellationToken.None);
                sendTiming.End();
                using var decodeTiming = OperationTiming.Start("map.calibration.decode", parent: timing.Id);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadFromJsonAsync<ApiError>(Json);
                    if ((int)response.StatusCode is 400 or 404 && error is { Code: "invalid" or "missing", Message.Length: > 0 })
                    { outcome.OutcomeKnown(); if (Current && operation == generation) feedback = error.Message; decodeTiming.End(TimingOutcome.ResponseDecoded); timing.End(TimingOutcome.Rejected); return; }
                    throw new InvalidOperationException("Map command outcome unconfirmed.");
                }
                var received = await LogicBrowserCache.DecodeAsync<LandingCommit>(response, cacheRequest);
                var result = received.Result;
                try
                {
                decodeTiming.End(TimingOutcome.ResponseDecoded);
                if (!result.Committed) throw new JsonException();
                acknowledged = true;
                outcome.OutcomeKnown();
                if (!Current || operation != generation) return;
                try
                {
                    await RetireCalibrationAsync();
                    await Shell.CloseLandingDialogAsync(overlayDialog);
                }
                finally { overlayDraft = null; }
                StateHasChanged();
                timing.End(TimingOutcome.CommittedFollowupReturned);
                }
                finally { admission.PublishCache(received); }
            }, lifetime.Token);
        }
        catch (Exception exception)
        {
            timing.End(acknowledged ? TimingOutcome.KnownCommitFollowupFailed : TimingOutcome.Uncertain);
            if (Current) await Shell.ReportLandingErrorAsync(exception, acknowledged ? "Saved; refresh failed. Do not repeat the saved command." : "The map command outcome could not be confirmed. Review current data before another action; no automatic retry was attempted.");
        }
        finally
        {
            pending = false; pendingAction = "";
            if (Current)
            {
                try { if (overlayDraft is not null) await Shell.FreezeLandingDialogAsync(overlayDialog, false); }
                catch (Exception exception) { await Shell.ReportLandingErrorAsync(exception, "The map dialog could not be updated."); }
                if (overlayDraft is null) await Shell.ReleaseLandingAsync(this);
                await Shell.DrainLandingAsync(); StateHasChanged();
            }
            LocalMapChanged();
        }
    }
    private async Task OpenOverlayAsync()
    {
        if (!Shell.CanAuthor || !Current || ToolsBlocked || landing?.Surface?.Decoration.Overlay is not { } overlay || !Shell.LockLanding(this)) return;
        try
        {
            await Shell.RememberLandingInitiatorAsync();
            if (!Current || !Shell.CanInteract) return;
            overlayDraft = new() { MapId = overlay.MapId, ScaleXPercent = overlay.ScaleXPercent, ScaleYPercent = overlay.ScaleYPercent, LeftOffsetPercent = overlay.LeftOffsetPercent, BottomOffsetPercent = overlay.BottomOffsetPercent };
            feedback = null; showDialogPending = true;
        }
        catch (Exception exception) { if (Current) await Shell.ReportLandingErrorAsync(exception, "Image calibration could not be opened."); }
        finally { if (overlayDraft is null) await Shell.ReleaseLandingAsync(this); }
    }
    private MapOverlayPlacement? DraftPlacement
    {
        get
        {
            if (overlayDraft is not { } d || landing?.Surface is not { } map || map.Decoration.Overlay is not { } overlay) return null;
            var b = map.Geometry.MapBounds;
            return new MapOverlayPlacementService().TryProject(b.X, b.Y, b.X + b.Width, b.Y + b.Height, d.ScaleXPercent, d.ScaleYPercent, d.LeftOffsetPercent, d.BottomOffsetPercent, overlay.AspectRatio, out var p) && double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Width) && p.Width > 0 && double.IsFinite(p.Height) && p.Height > 0 ? p : null;
        }
    }
    private Task ApplyOverlayAsync()
    {
        if (pending || Shell.GeneralErrorOpen || overlayDraft is not { } d) return Task.CompletedTask;
        if (DraftPlacement is null) { feedback = "Scale must be finite and positive; offsets and projected placement must be finite."; return Task.CompletedTask; }
        return RunCommandAsync("overlay", "map-overlay/placement", new LandingPlacement(d.MapId, d.ScaleXPercent, d.ScaleYPercent, d.LeftOffsetPercent, d.BottomOffsetPercent));
    }
    private async Task RetireCalibrationAsync()
    { if (calibrationOwned) { calibrationOwned = false; await JS.InvokeVoidAsync("mapOverlayCalibration.dispose", calibrationSvg); } }
    private async Task CloseOverlayAsync()
    {
        if (!Current || !Shell.CanInteract || pending || Shell.GeneralErrorOpen || overlayDraft is null) return;
        try { await RetireCalibrationAsync(); await Shell.CloseLandingDialogAsync(overlayDialog); overlayDraft = null; await Shell.ReleaseLandingAsync(this); LocalMapChanged(); }
        catch (Exception exception) { if (Current) await Shell.ReportLandingErrorAsync(exception, "Image calibration could not be closed."); }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!Current || !Shell.CanInteract) return;
        try
        {
            await OpenQueryLinksAsync();
            if (showDialogPending && overlayDraft is not null && !Shell.GeneralErrorOpen)
            {
                if (await Shell.ShowLandingDialogAsync(overlayDialog)) showDialogPending = false;
            }
            if (!Current) return;
            if (overlayDraft is not null)
            {
                var draft = overlayDraft; var svg = calibrationSvg;
                await JS.InvokeVoidAsync("mapOverlayCalibration.initialize", svg);
                if (!Current || !ReferenceEquals(overlayDraft, draft)) { await JS.InvokeVoidAsync("mapOverlayCalibration.dispose", svg); return; }
                calibrationOwned = true;
            }
        }
        catch (Exception exception) { if (Current && !Shell.GeneralErrorOpen) await Shell.ReportLandingErrorAsync(exception, "The map dialog could not be displayed."); }
    }
    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private string ViewBox { get { var b = landing!.Surface!.Geometry.MapBounds; return $"{Number(b.X)} {Number(b.Y)} {Number(b.Width)} {Number(b.Height)}"; } }
    private string MapTransform { get { var b = landing!.Surface!.Geometry.MapBounds; return $"translate(0 {Number(b.Y * 2 + b.Height)}) scale(1 -1)"; } }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        Shell.ReleaseClientUpdateRisk(this);
        disposed = true; Catalogue.Changed -= LocalMapChanged; generation++; lifetime.Cancel();
        try { await RetireCalibrationAsync(); }
        catch (JSException) { }
        finally { await Shell.ReleaseLandingAsync(this); lifetime.Dispose(); }
    }
    private sealed class OverlayDraft
    {
        public Guid MapId { get; init; }
        public double ScaleXPercent { get; set; }
        public double ScaleYPercent { get; set; }
        public double LeftOffsetPercent { get; set; }
        public double BottomOffsetPercent { get; set; }
    }
}
