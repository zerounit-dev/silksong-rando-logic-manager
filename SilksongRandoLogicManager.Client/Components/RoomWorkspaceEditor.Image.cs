using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;
using Silksong_Rando_Logic_Manager.Components.RoomEditorV2;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client.Components;

public partial class RoomWorkspaceEditor
{
    private RoomImageDraftView? imageServer;
    private RoomImageDialog.Draft? imageDraft;
    private RoomImageDialog? imageContent;
    private RoomImageOpenRequest? imageOpenRequest;
    private readonly Dictionary<RoomSceneImageSource, RoomImageDialog.Values> imageMappings = [];
    private readonly HashSet<Guid> imageUploads = [];
    private IReadOnlyList<RoomImageDialog.Marker> imageMarkers = [];
    private SceneViewportBounds imageViewport = new(0, 0, 1, 1);
    private Guid? imageUploadAttempt, imageSelectedUpload, imageOperationId;
    private Guid? imageReselectionEvidence;
    private bool imageNewUpload;
    private long imageGeneration, imageRevision, imageReset, imageCorrectionFocus;
    private long imageSubmissionInteraction;
    private bool imageBusy, imageSubmitting, imageUploadUncertain, imageFocusPending, imageReleasePending, imageReleaseActive;
    private string? imageFeedback;
    private bool ImageCorrection => workspace.Queue.Blocked?.Evidence is RoomSceneImageCorrectionEvidence evidence && evidence.DraftOwnerId == imageServer?.DraftOwnerId;
    private bool ImageControlDisabled => imageSubmitting || controlPending || !Shell.CanUseRoom || workspace.Queue.RecoveryRequired || workspace.Queue.ProjectionPending || workspace.Queue.TrackingUnavailable;
    private bool ImageDisabled => imageBusy || ImageControlDisabled;
    private bool ImageCancelDisabled => ImageControlDisabled || ImageCorrection;
    private bool OwnsImage(long generation, Guid draft) => Current && imageGeneration == generation && imageServer?.DraftOwnerId == draft;
    private RoomTrackingOwner RoomOwner => new(Opened.Identity.TabId, Opened.Identity.VisitId);
    private static string ImageNumber(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static RoomImageDialog.Values ImageValues(RoomSceneImageSettings settings) => new(ImageNumber(settings.MinX), ImageNumber(settings.MinY), ImageNumber(settings.MaxX), ImageNumber(settings.MaxY), ImageNumber(settings.PixelsPerUnitX), ImageNumber(settings.PixelsPerUnitY), ImageNumber(settings.PixelOffsetX), ImageNumber(settings.PixelOffsetY), settings.Source != RoomSceneImageSource.None);
    private async Task OpenImageAsync()
    {
        if (!TransferKeyboardBeforeAction()) return;
        if (AuthoringBlocked || Archived) return;
        var generation = ++imageGeneration; imageBusy = true;
        imageOpenRequest ??= new(RoomOwner, Guid.NewGuid(), generation);
        try
        {
            var value = await Catalogue.OpenRoomImageAsync(Opened.Identity, imageOpenRequest, () => Current && imageGeneration == generation, lifetime.Token);
            if (!Current || !Shell.CanInteract || imageGeneration != generation || value is null) return;
            imageServer = value; imageRevision = value.Revision; imageMappings.Clear(); imageUploads.Clear(); imageSelectedUpload = imageUploadAttempt = imageOperationId = null;
            imageUploadUncertain = imageSubmitting = false; imageFeedback = null;
            imageDraft = new(value.Settings.Source, ImageValues(value.Settings), value.SourceWidth, value.SourceHeight, value.PreviewUrl, value.Information, false, ++imageReset);
            var document = workspace.Queue.Confirmed.Document!;
            imageMarkers = document.Transitions.Where(x => !x.Observation.IsArchived && FinitePair(x.Metadata.InGamePositionX, x.Metadata.InGamePositionY))
                .Select(x => new RoomImageDialog.Marker("exit", x.Values.Alias, x.Metadata.InGamePositionX!.Value, x.Metadata.InGamePositionY!.Value))
                .Concat(document.Checks.Where(x => !x.Observation.IsArchived && FinitePair(x.Metadata.InGamePositionX, x.Metadata.InGamePositionY))
                    .Select(x => new RoomImageDialog.Marker("check", x.Values.FriendlyName, x.Metadata.InGamePositionX!.Value, x.Metadata.InGamePositionY!.Value, CheckLocationTypeCatalogue.IsEnemy(x.Values.LocationType)))).OrderBy(x => x.Kind).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToArray();
            // Same retained viewport rule: bounds plus the modal's game-position
            // markers. No annotation coordinates, frames or connection markers.
            imageViewport = ImageInitialViewport(value.Settings, document.Scene);
            imageFocusPending = true; StateHasChanged();
        }
        catch (ConfirmedCatalogue.RoomTrackingUnavailableException) { if (Current && imageGeneration == generation) { workspace.Queue.LoseTracking(); NotifyRoomActions(); } }
        catch (Exception exception) { if (Current && imageGeneration == generation) await Shell.ReportRoomErrorAsync(exception, "Scene bounds and image could not be opened. No saved image was changed."); }
        finally { if (Current && imageGeneration == generation) { imageBusy = false; StateHasChanged(); await Shell.DrainRoomRefreshAsync(); } }
    }
    private SceneViewportBounds ImageInitialViewport(RoomSceneImageSettings settings, SceneLayoutView? room)
    {
        var validRoom = room?.HasValidBounds == true;
        var minX = Math.Min(0, validRoom ? room!.SceneMinX : 0); var minY = Math.Min(0, validRoom ? room!.SceneMinY : 0);
        var maxX = Math.Max(0, validRoom ? room!.SceneMaxX!.Value : 0); var maxY = Math.Max(0, validRoom ? room!.SceneMaxY!.Value : 0);
        void Include(double? x, double? y)
        { if (!FinitePair(x, y)) return; minX = Math.Min(minX, x!.Value); minY = Math.Min(minY, y!.Value); maxX = Math.Max(maxX, x.Value); maxY = Math.Max(maxY, y.Value); }
        Include(settings.MinX, settings.MinY); Include(settings.MaxX ?? 0, settings.MaxY ?? 0);
        foreach (var marker in imageMarkers) Include(marker.X, marker.Y);
        var width = maxX - minX; var height = maxY - minY; var padding = SceneViewportMath.Padding;
        var minimum = Math.Max(1d, Math.Max(width, height) * padding);
        var normalizedWidth = Math.Max(width, minimum); var normalizedHeight = Math.Max(height, minimum);
        minX -= (normalizedWidth - width) / 2d; minY -= (normalizedHeight - height) / 2d;
        return new(minX - normalizedWidth * padding, -(minY + normalizedHeight * (1d + padding)), normalizedWidth * (1d + 2d * padding), normalizedHeight * (1d + 2d * padding));
    }
    private void ImageDraftChanged(RoomImageDialog.Values values)
    {
        if (imageDraft is null || ImageDisabled) return;
        imageDraft = imageDraft with { Values = values }; imageRevision++;
    }
    private async Task SelectImageSourceAsync(RoomSceneImageSource source)
    {
        if (ImageDisabled || imageDraft is null || imageServer is null || imageUploadUncertain) return;
        var old = imageDraft;
        imageMappings[old.Source] = old.Values;
        if (imageNewUpload) imageMappings.Remove(RoomSceneImageSource.Uploaded);
        await PrepareImageAsync(async (draft, owns) => await Catalogue.SelectRoomImageAsync(Opened.Identity, draft,
            new(RoomOwner, Guid.NewGuid(), ++imageRevision, source, source == RoomSceneImageSource.Uploaded ? imageSelectedUpload : null), owns, lifetime.Token), value =>
        {
            var defaults = imageMappings.GetValueOrDefault(source) ?? ImageValues(value.Settings);
            imageDraft = old with { Source = source, Values = old.Values with { PixelsPerUnitX = defaults.PixelsPerUnitX, PixelsPerUnitY = defaults.PixelsPerUnitY, PixelOffsetX = defaults.PixelOffsetX, PixelOffsetY = defaults.PixelOffsetY, UseSceneImage = source != RoomSceneImageSource.None, NativeUpload = imageNewUpload || old.Values.NativeUpload, UploadResolution = imageNewUpload ? "recommended" : old.Values.UploadResolution, UploadCustomTarget = imageNewUpload ? "" : old.Values.UploadCustomTarget }, SourceWidth = value.SourceWidth, SourceHeight = value.SourceHeight, PreviewUrl = value.PreviewUrl, Information = value.Information, PendingUpload = source == RoomSceneImageSource.Uploaded && imageSelectedUpload is not null, ResetVersion = ++imageReset };
            imageNewUpload = false;
        });
    }
    private async Task PrepareImageAsync(Func<Guid, Func<bool>, Task<RoomImageDraftView?>> read, Action<RoomImageDraftView> install)
    {
        if (imageServer is null) return;
        var generation = imageGeneration; var draft = imageServer.DraftOwnerId;
        imageBusy = true; imageFeedback = null;
        try
        {
            var value = await read(draft, () => OwnsImage(generation, draft));
            if (value is null || !Shell.CanInteract || !OwnsImage(generation, draft)) return;
            if (value.DraftOwnerId != draft || value.DialogGeneration != imageServer!.DialogGeneration) throw new System.Text.Json.JsonException("Wrong image draft.");
            imageServer = value; imageRevision = Math.Max(imageRevision, value.Revision); install(value);
        }
        catch (ConfirmedCatalogue.RoomTrackingUnavailableException) { if (OwnsImage(generation, draft)) { workspace.Queue.LoseTracking(); NotifyRoomActions(); } }
        catch (Exception exception) { if (OwnsImage(generation, draft)) await Shell.ReportRoomErrorAsync(exception, "The image source could not be prepared. Numeric drafts and the saved image have been retained."); }
        finally { if (OwnsImage(generation, draft)) { imageBusy = false; StateHasChanged(); } }
    }
    private async Task UploadImageAsync(InputFileChangeEventArgs args)
    {
        if (ImageDisabled || imageUploadUncertain || imageServer is null || imageDraft is null) return;
        const long limit = 256L * 1024 * 1024;
        if (args.File.Size > limit) { imageFeedback = "Choose an image no larger than 256 MiB."; return; }
        var generation = imageGeneration; var draft = imageServer.DraftOwnerId;
        var attempt = Guid.NewGuid(); imageUploads.Add(attempt); imageUploadAttempt = attempt; imageUploadUncertain = imageBusy = true;
        try
        {
            await using var stream = args.File.OpenReadStream(limit, lifetime.Token);
            var value = await Catalogue.UploadRoomImageAsync(Opened.Identity, draft, attempt, stream, () => OwnsImage(generation, draft), lifetime.Token);
            if (!OwnsImage(generation, draft) || value is null) return;
            await InstallImageUploadAsync(value);
        }
        catch (ConfirmedCatalogue.RoomTrackingUnavailableException) { if (OwnsImage(generation, draft)) { workspace.Queue.LoseTracking(); NotifyRoomActions(); } }
        catch (Exception exception) { if (OwnsImage(generation, draft)) await Shell.ReportRoomErrorAsync(exception, "The upload outcome could not be confirmed. Check the same upload before selecting the file again; saved room data is unchanged."); }
        finally { if (OwnsImage(generation, draft)) { imageBusy = false; StateHasChanged(); } }
    }
    private async Task RecoverImageUploadAsync()
    {
        if (ImageDisabled || !imageUploadUncertain || imageUploadAttempt is not { } attempt || imageServer is null) return;
        var generation = imageGeneration; var draft = imageServer.DraftOwnerId; imageBusy = true;
        try
        {
            var value = await Catalogue.ReadRoomImageUploadAsync(Opened.Identity, draft, attempt, () => OwnsImage(generation, draft), lifetime.Token);
            if (OwnsImage(generation, draft) && value is not null) await InstallImageUploadAsync(value);
        }
        catch (ConfirmedCatalogue.RoomTrackingUnavailableException) { if (OwnsImage(generation, draft)) { workspace.Queue.LoseTracking(); NotifyRoomActions(); } }
        catch (Exception exception) { if (OwnsImage(generation, draft)) await Shell.ReportRoomErrorAsync(exception, "The upload status could not be read. No upload or room write was repeated."); }
        finally { if (OwnsImage(generation, draft)) { imageBusy = false; StateHasChanged(); } }
    }
    private async Task InstallImageUploadAsync(RoomImageUploadView value)
    {
        if (!Shell.CanInteract) return;
        if (value.UploadAttemptId != imageUploadAttempt) throw new System.Text.Json.JsonException("Wrong image upload.");
        if (value.Status == RoomImageUploadStatus.Receiving) { imageFeedback = "The upload is still receiving. Check its status again before reselecting."; return; }
        imageUploadUncertain = false;
        if (value.Status != RoomImageUploadStatus.Ready) { imageFeedback = "The upload is not available. Select a valid PNG, JPEG or WebP image again."; return; }
        imageSelectedUpload = value.UploadAttemptId;
        imageNewUpload = true;
        imageMappings.Remove(RoomSceneImageSource.Uploaded);
        imageBusy = false;
        await SelectImageSourceAsync(RoomSceneImageSource.Uploaded);
    }
    private bool ReadImageSettings(out RoomSceneImageSettings settings, out SceneImageUploadPreparation? preparation)
    {
        settings = imageServer!.Settings; preparation = null;
        var draft = imageDraft!; var text = draft.Values;
        if (draft.Source == RoomSceneImageSource.None) { settings = settings with { Source = RoomSceneImageSource.None }; return true; }
        double? Number(string value) => string.IsNullOrWhiteSpace(value) ? null : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : throw new FormatException();
        try
        {
            settings = new(Number(text.MinX) ?? throw new FormatException(), Number(text.MinY) ?? throw new FormatException(), Number(text.MaxX), Number(text.MaxY), Number(text.PixelsPerUnitX), Number(text.PixelsPerUnitY), Number(text.PixelOffsetX), Number(text.PixelOffsetY), draft.Source);
            if (draft.PendingUpload) preparation = new(text.NativeUpload, text.UploadResolution, text.UploadResolution == "custom" ? Number(text.UploadCustomTarget) : null);
            return true;
        }
        catch (FormatException) { imageFeedback = "Enter complete finite numbers before applying."; imageCorrectionFocus++; return false; }
    }
    private async Task ResetImageAsync()
    {
        if (ImageDisabled || imageDraft is null || imageServer is null) return;
        if (imageDraft.Source == RoomSceneImageSource.Uploaded)
        { imageDraft = imageDraft with { Values = imageDraft.Values with { PixelOffsetX = "0", PixelOffsetY = "0" }, ResetVersion = ++imageReset }; imageRevision++; return; }
        if (!ReadImageSettings(out var settings, out var preparation)) return;
        await PrepareImageAsync((draft, owns) => Catalogue.EstimateRoomImageAsync(Opened.Identity, draft, new(RoomOwner, Guid.NewGuid(), ++imageRevision, settings, null, preparation), owns, lifetime.Token), value => imageDraft = imageDraft with { Values = ImageValues(value.Settings), ResetVersion = ++imageReset, Information = value.Information });
    }
    private async Task ApplyImageAsync()
    {
        if (ImageDisabled || imageUploadUncertain || imageServer is null || imageDraft is null || !ReadImageSettings(out var settings, out var preparation)) return;
        if (settings.Source == RoomSceneImageSource.Uploaded && workspace.Queue.Blocked?.Evidence is RoomSceneImageCorrectionEvidence { RequiresUploadReselection: true } && imageSelectedUpload is null)
        { imageFeedback = "Select the image file again before applying this correction."; return; }
        imageSubmitting = true; imageFeedback = null;
        imageSubmissionInteraction = nativeOwner!.Invoke<long>("roomInteractionGeneration");
        if (ImageCorrection && workspace.Queue.Blocked?.Evidence is RoomSceneImageCorrectionEvidence evidence)
            await ResolveAsync(new CorrectRoomSceneImageApply(evidence.DraftOwnerId, ++imageRevision, evidence.ReviewedBaseline, settings, settings.Source == RoomSceneImageSource.Uploaded ? imageSelectedUpload : null, preparation));
        else
        {
            var operation = workspace.Enqueue(new ApplyRoomSceneImage(imageServer.DraftOwnerId, ++imageRevision, imageServer.Baseline, settings, settings.Source == RoomSceneImageSource.Uploaded ? imageSelectedUpload : null, preparation));
            imageOperationId = operation.OperationId;
            await DrainAsync();
        }
        if (ImageCorrection) { imageSubmitting = false; imageCorrectionFocus++; }
    }
    private async Task ClearImageAsync()
    {
        if (ImageDisabled || ImageCorrection || imageServer is null) return;
        imageSubmitting = true;
        imageSubmissionInteraction = nativeOwner!.Invoke<long>("roomInteractionGeneration");
        imageOperationId = workspace.Enqueue(new ClearRoomSceneBounds(imageServer.DraftOwnerId, ++imageRevision, imageServer.Baseline)).OperationId;
        await DrainAsync();
    }
    private async Task CloseImageAsync()
    {
        if (ImageCancelDisabled || imageServer is null || imageDraft is null) return;
        var interaction = nativeOwner?.Invoke<long>("roomInteractionGeneration");
        var generation = ++imageGeneration;
        imageDraft = null; imageFocusPending = false; imageReleasePending = true;
        await ReleaseImageAsync();
        if (Current && imageGeneration == generation)
        {
            if (interaction is { } captured) pendingBoundaryFocus = ("scene-image-capture", captured);
            await Shell.DrainRoomRefreshAsync();
        }
    }
    private Task ImageKeyDownAsync(KeyboardEventArgs args) => args.Key == "Escape" ? CloseImageAsync() : Task.CompletedTask;
    private void ObserveImageResponse(RoomTrackingResponse response)
    {
        if (imageOperationId is { } id && response.Results.Any(x => x.Receipt.OperationId == id && !x.ProjectionPending
            && x.Receipt.Sequence <= workspace.Queue.IncorporatedThrough))
        {
            imageDraft = null; imageSubmitting = false; imageReleasePending = true; imageOperationId = null;
            pendingBoundaryFocus = ("scene-image-capture", imageSubmissionInteraction);
        }
        else if (ImageCorrection)
        {
            imageSubmitting = false; imageFeedback = workspace.Queue.Blocked!.Message; imageCorrectionFocus++;
            if (workspace.Queue.Blocked.Evidence is RoomSceneImageCorrectionEvidence { RequiresUploadReselection: true } && imageReselectionEvidence != workspace.Queue.Blocked.EvidenceId)
            { imageReselectionEvidence = workspace.Queue.Blocked.EvidenceId; imageSelectedUpload = null; }
        }
    }
    private async Task ReleaseImageAsync()
    {
        if (!imageReleasePending || imageReleaseActive || imageServer is null) return;
        imageReleasePending = false; // One best-effort attempt, not a render-driven retry loop.
        imageReleaseActive = imageBusy = true;
        var id = imageServer.DraftOwnerId;
        try
        {
            await Catalogue.ReleaseRoomImageAsync(Opened.Identity, id, new(RoomOwner, Guid.NewGuid(), imageUploads.ToImmutableArray()), CancellationToken.None);
            imageReleasePending = false; imageServer = null; imageOpenRequest = null; imageUploads.Clear(); imageSelectedUpload = null;
        }
        catch (Exception exception) { if (Current) await Shell.ReportRoomErrorAsync(exception, "Image draft cleanup failed. Saved room state is unchanged by cleanup."); }
        finally
        {
            // A dismissed modal's open attempt is retired even if its cleanup
            // response was lost. A later opening owns a fresh draft, never a
            // potentially released former owner. Block that opening until this
            // completion can no longer clear its account.
            imageOpenRequest = null;
            imageReleaseActive = imageBusy = false;
            if (Current) StateHasChanged();
        }
    }
}
