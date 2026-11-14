using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

internal sealed class RoomEditingImages(SceneImageCaptureService captures, SceneImageFileService files,
    ILogger<RoomEditingImages> logger, Authentication.LiveAccess access)
{
    public async Task<Wire.RoomImageDraftView> OpenAsync(RoomEditingTracker.Entry entry, Wire.RoomImageOpenRequest request)
    {
        Owner(entry, request.Owner);
        if (request.OpenAttemptId == Guid.Empty || request.DialogGeneration < 0)
            throw new RoomTrackingProtocolException("Invalid image open identity.");
        var known = entry.Images.Drafts.Values.SingleOrDefault(x => x.Open.OpenAttemptId == request.OpenAttemptId);
        if (known is not null)
        {
            if (known.Open != request) throw new RoomTrackingProtocolException("The image open identity was reused.");
            return known.View;
        }
        if (entry.Progress.RegisteredThrough != entry.Progress.AcknowledgedThrough || entry.Blocked is not null || entry.ProjectionPending)
            throw new RoomTrackingProtocolException("Drain the room queue before opening image preparation.");
        var result = await captures.GetDraftAsync(entry.Identity.RoomId, CancellationToken.None);
        var id = Guid.NewGuid();
        var view = View(id, request.DialogGeneration, 0, result);
        entry.Images.Drafts.Add(id, new(id, request, result.Draft, view));
        return view;
    }

    public async Task<Wire.RoomImageUploadView> UploadAsync(RoomEditingTracker.Entry entry, Guid draftId, Guid attemptId,
        long? declaredLength, Stream input, CancellationToken token)
    {
        var draft = entry.Images.Get(draftId);
        if (attemptId == Guid.Empty || declaredLength is < 0) throw new RoomTrackingProtocolException("Invalid upload identity.");
        if (draft.Uploads.TryGetValue(attemptId, out var known))
        {
            if (known.DeclaredLength != declaredLength) throw new RoomTrackingProtocolException("The upload identity was reused with different input metadata.");
            return UploadView(attemptId, known);
        }
        Editable(entry, draft);
        var upload = new RoomEditingImageState.Upload(declaredLength);
        draft.Uploads.Add(attemptId, upload); // Identity exists before stream consumption.
        try
        {
            var resource = await files.StageAsync(access, input, token);
            upload.Resource = resource;
            upload.Status = Wire.RoomImageUploadStatus.Ready;
        }
        catch (Exception failure)
        {
            upload.Status = Wire.RoomImageUploadStatus.Failed;
            logger.LogError(failure, "Image upload {AttemptId} failed in tracker {TrackerId}", attemptId, entry.Identity.TrackerId);
            throw;
        }
        return UploadView(attemptId, upload);
    }

    public Wire.RoomImageUploadView Status(RoomEditingTracker.Entry entry, Guid draftId, Guid attemptId)
    {
        var draft = entry.Images.Get(draftId);
        return draft.Uploads.TryGetValue(attemptId, out var upload) ? UploadView(attemptId, upload)
            : new(attemptId, Wire.RoomImageUploadStatus.Missing, null, null, null, null);
    }

    public async Task<Wire.RoomImageDraftView> SelectAsync(RoomEditingTracker.Entry entry, Guid draftId, Wire.RoomImageSelectRequest request)
    {
        Owner(entry, request.Owner);
        var draft = entry.Images.Get(draftId);
        if (Read(draft, request.RequestId, request, out var known)) return known!;
        Revision(entry, draft, request.Revision);
        if (!Enum.IsDefined(request.Source) || request.Source != Wire.RoomSceneImageSource.Uploaded && request.UploadAttemptId is not null)
            throw new RoomTrackingProtocolException("Invalid image source selection.");
        var resource = Resource(draft, request.UploadAttemptId);
        var result = await captures.SelectSourceAsync(entry.Identity.RoomId, draft.Capture,
            request.Source == Wire.RoomSceneImageSource.None ? null : request.Source == Wire.RoomSceneImageSource.Uploaded,
            resource);
        draft.Capture = result.Draft;
        return Remember(draft, request.RequestId, request.Revision, request, result);
    }

    public async Task<Wire.RoomImageDraftView> EstimateAsync(RoomEditingTracker.Entry entry, Guid draftId, Wire.RoomImageEstimateRequest request)
    {
        Owner(entry, request.Owner);
        var draft = entry.Images.Get(draftId);
        if (Read(draft, request.RequestId, request, out var known)) return known!;
        Revision(entry, draft, request.Revision);
        if (request.Settings is null || !Enum.IsDefined(request.Settings.Source)
            || request.Settings.Source != Wire.RoomSceneImageSource.Uploaded && request.UploadAttemptId is not null)
            throw new RoomTrackingProtocolException("Invalid image source.");
        var capture = Capture(draft.Capture, request.Settings, draft.Capture.Baseline, Resource(draft, request.UploadAttemptId), request.Preparation);
        var result = await captures.GetEstimateAsync(entry.Identity.RoomId, capture, CancellationToken.None);
        // Explicit estimate is a draft read, not a persisted room baseline advance.
        draft.Capture = result.Draft;
        return Remember(draft, request.RequestId, request.Revision, request, result);
    }

    public async Task<byte[]?> PreviewAsync(RoomEditingTracker.Entry entry, Guid draftId, Guid handle)
    {
        var draft = entry.Images.Get(draftId);
        if (!draft.Uploads.Values.Any(x => x.Resource?.Handle == handle && x.Status == Wire.RoomImageUploadStatus.Ready))
            throw new RoomTrackingProtocolException("The preview does not belong to this image draft.");
        using var lifecycle = await files.AcquireLifecycleAsync(CancellationToken.None);
        return await files.OpenStagedPreviewAsync(access, handle);
    }

    public async Task ReleaseAsync(RoomEditingTracker.Entry entry, Guid draftId, Wire.RoomImageReleaseRequest request)
    {
        Owner(entry, request.Owner);
        var draft = entry.Images.Get(draftId);
        if (request.ReleaseId == Guid.Empty || request.UploadAttemptIds.IsDefault
            || request.UploadAttemptIds.Distinct().Count() != request.UploadAttemptIds.Length
            || request.UploadAttemptIds.Any(x => x == Guid.Empty))
            throw new RoomTrackingProtocolException("Invalid image release request.");
        if (draft.Releases.TryGetValue(request.ReleaseId, out var known))
        {
            if (known.Owner != request.Owner || !known.UploadAttemptIds.SequenceEqual(request.UploadAttemptIds))
                throw new RoomTrackingProtocolException("The image release identity was reused.");
        }
        else
        {
            draft.Releases.Add(request.ReleaseId, request);
            draft.ReleasePending = true;
            // Cancel retires the entire owned modal, including an upload whose
            // response was lost. Attempt IDs are the client's account, not proof
            // that registration happened or authority over another draft's files.
            foreach (var upload in draft.Uploads.Values) upload.ReleasePending = true;
        }
        await CollectAsync(entry);
    }

    public async Task<RoomMutationEffect?> ExecuteAsync(RoomEditingTracker.Entry entry, Wire.CorrectRoomSceneImageApply? correction = null)
    {
        var head = entry.CurrentHead ?? throw new RoomTrackingProtocolException("There is no image queue head.");
        var (draftId, revision, baseline) = head.Operation switch
        {
            Wire.ApplyRoomSceneImage x => (x.DraftOwnerId, x.Revision, x.Baseline),
            Wire.ClearRoomSceneBounds x => (x.DraftOwnerId, x.Revision, x.Baseline),
            _ => throw new RoomTrackingProtocolException("The head is not an image command.")
        };
        var draft = entry.Images.Get(draftId);
        Wire.RoomSceneImageSettings? settings = (head.Operation as Wire.ApplyRoomSceneImage)?.Settings;
        Guid? uploadAttempt = (head.Operation as Wire.ApplyRoomSceneImage)?.OwnedUploadAttemptId;
        var preparation = (head.Operation as Wire.ApplyRoomSceneImage)?.Preparation;
        if (correction is not null)
        {
            if (head.Operation is not Wire.ApplyRoomSceneImage || entry.Blocked?.Evidence is not Wire.RoomSceneImageCorrectionEvidence evidence
                || correction.DraftOwnerId != draftId || correction.Revision < Math.Max(revision, draft.Revision)
                || correction.ReviewedBaseline != evidence.ReviewedBaseline)
                throw new RoomTrackingProtocolException("The image correction does not match the reviewed Apply.");
            baseline = correction.ReviewedBaseline; revision = correction.Revision;
            settings = correction.Settings; uploadAttempt = correction.OwnedUploadAttemptId; preparation = correction.Preparation;
            if (uploadAttempt is { } attempt && !draft.Uploads.ContainsKey(attempt))
                throw new RoomTrackingProtocolException("The correction input does not belong to this draft.");
        }
        RoomImageUpload? resource = null;
        if (settings?.Source == Wire.RoomSceneImageSource.Uploaded && uploadAttempt is { } selected)
        {
            if (!draft.Uploads.TryGetValue(selected, out var upload) || upload.Status != Wire.RoomImageUploadStatus.Ready || upload.Resource is null)
            { Correct(entry, draftId, revision, baseline, true); return null; }
            resource = upload.Resource;
            // Lookup failure here is a specifically classified missing owned stage,
            // before publication starts, not a catch-all inference about rollback.
            try { files.GetStaged(resource.Handle); }
            catch (InvalidOperationException) { Correct(entry, draftId, revision, baseline, true); return null; }
        }
        var admission = new RoomImageCommitAdmission(entry);
        var applyDraft = settings is null ? null : Capture(draft.Capture, settings, Baseline(baseline), resource, preparation);
        if (applyDraft?.UseSceneImage == true)
        {
            // These are the retained publisher's pure prerequisites, evaluated
            // before any filesystem/database write. Only this proven no-write
            // validation boundary is classified as a reviewed settings correction.
            try
            {
                var validationDraft = resource is not null && preparation?.Native == true
                    ? applyDraft with { PixelsPerUnitX = 64, PixelsPerUnitY = 64, PixelOffsetX = 0, PixelOffsetY = 0 } : applyDraft;
                SceneImageCaptureService.ValidateGeneration(validationDraft);
                if (resource is not null)
                    SceneImageUploadMath.Calculate(resource.Width, resource.Height, validationDraft.PixelsPerUnitX, validationDraft.PixelsPerUnitY,
                        preparation ?? throw new InvalidOperationException("The replacement preparation is unavailable."),
                        validationDraft.MinX, validationDraft.MinY, validationDraft.MaxX, validationDraft.MaxY);
                if (!validationDraft.UseUploadedSceneImage && !captures.IsSourceReady())
                    throw new InvalidOperationException("The ScriptersWar source is unavailable.");
            }
            catch (InvalidOperationException)
            { Correct(entry, draftId, revision, baseline, false, "Review the image settings and source prerequisites before applying."); return null; }
        }
        try
        {
            if (head.Operation is Wire.ClearRoomSceneBounds)
                await captures.ClearTrackedBoundsAsync(entry.Identity.RoomId, Baseline(baseline), admission);
            else
            {
                var result = await captures.ApplyTrackedAsync(entry.Identity.RoomId,
                    applyDraft!, admission);
                if (result.CleanupException is not null)
                    entry.RecordCleanupWarning(head.Sequence, "Image saved; postcommit cleanup failed. See server diagnostics.");
            }
        }
        catch (SceneImageCaptureConflictException conflict)
        {
            if (head.Operation is Wire.ApplyRoomSceneImage) Correct(entry, draftId, revision, Baseline(conflict.Baseline), false);
            else RoomEditingMutationScope.Corrective(entry, entry.Identity.RoomId, "The room scene state changed. Copy pending work before reloading.");
            return null;
        }
        catch (RoomImageTargetUnavailableException)
        {
            // Only the publisher's explicit missing/archived proven-no-write
            // seams produce this type; unknown failures/commits are not converted.
            RoomEditingMutationScope.Missing(entry, entry.Identity.RoomId);
            return null;
        }
        catch (RoomImageOriginalUnavailableException)
        {
            Correct(entry, draftId, revision, baseline, true);
            return null;
        }
        finally
        {
            if (entry.Progress.TerminalThrough >= head.Sequence) entry.Images.Complete(head.OperationId);
        }
        return new(entry.Identity.RoomId, [entry.Identity.RoomId], admission.SharedPlan!.Advances, RoomProjectionFamilies.None,
            (admission.BoundsChanged ? Wire.RoomSceneConcerns.Canvas | Wire.RoomSceneConcerns.Viewport : Wire.RoomSceneConcerns.None)
                | Wire.RoomSceneConcerns.BoundsImageAction | Wire.RoomSceneConcerns.ImageDisplay,
            new([], [], [], [])) { SharedPlan = admission.SharedPlan };
    }

    public async Task CollectAsync(RoomEditingTracker.Entry entry)
    {
        foreach (var draft in entry.Images.Drafts.Values.Where(x => x.Operations.Count == 0))
            foreach (var upload in draft.Uploads.Values.Where(x => x.ReleasePending && x.Resource is not null))
                try
                {
                    await files.DiscardUploadInternalAsync(upload.Resource!.Handle);
                    upload.Resource = null;
                    upload.Status = Wire.RoomImageUploadStatus.Missing;
                }
                catch (Exception failure) { logger.LogError(failure, "Retired image input cleanup failed in tracker {TrackerId}", entry.Identity.TrackerId); }
    }

    private Wire.RoomImageDraftView View(Guid id, long generation, long revision, SceneImageCaptureDraftResult result) => new(id, generation, revision,
        Baseline(result.Draft.Baseline), Settings(result.Draft), result.Draft.SourcePixelWidth, result.Draft.SourcePixelHeight,
        result.Draft.UseSceneImage && !result.Draft.UseUploadedSceneImage && captures.IsSourceReady(), result.Draft.PreviewUrl, result.Information);
    private Wire.RoomImageDraftView Remember(RoomEditingImageState.Draft draft, Guid requestId, long revision, object request, SceneImageCaptureDraftResult result)
    {
        var view = View(draft.Id, draft.Open.DialogGeneration, revision, result);
        draft.Revision = revision; draft.View = view;
        draft.Reads.Add(requestId, (request, view));
        return view;
    }
    private static bool Read(RoomEditingImageState.Draft draft, Guid requestId, object request, out Wire.RoomImageDraftView? known)
    {
        if (requestId == Guid.Empty) throw new RoomTrackingProtocolException("Missing image request identity.");
        if (!draft.Reads.TryGetValue(requestId, out var read)) { known = null; return false; }
        if (!Equals(read.Request, request)) throw new RoomTrackingProtocolException("The image request identity was reused.");
        known = read.Result; return true;
    }
    private static void Editable(RoomEditingTracker.Entry entry, RoomEditingImageState.Draft draft)
    {
        var correction = entry.Blocked?.Evidence is Wire.RoomSceneImageCorrectionEvidence evidence
            && evidence.DraftOwnerId == draft.Id && entry.CurrentHead?.Operation is Wire.ApplyRoomSceneImage;
        if (draft.ReleasePending || draft.Operations.Count != 0 && !correction)
            throw new RoomTrackingProtocolException("This image draft is retired or has an admitted operation.");
    }
    private static void Revision(RoomEditingTracker.Entry entry, RoomEditingImageState.Draft draft, long revision)
    {
        Editable(entry, draft);
        if (revision <= draft.Revision) throw new RoomTrackingProtocolException("The image draft revision is obsolete.");
    }
    private static RoomImageUpload? Resource(RoomEditingImageState.Draft draft, Guid? attempt)
    {
        if (attempt is null) return null;
        if (!draft.Uploads.TryGetValue(attempt.Value, out var upload) || upload.Status != Wire.RoomImageUploadStatus.Ready || upload.Resource is null)
            throw new RoomTrackingProtocolException("The owned image input is not ready.");
        return upload.Resource;
    }
    private static Wire.RoomImageUploadView UploadView(Guid id, RoomEditingImageState.Upload upload) => new(id, upload.Status,
        upload.Resource?.Handle, upload.Resource?.Width, upload.Resource?.Height, upload.Resource?.Hash);
    private static void Owner(RoomEditingTracker.Entry entry, Wire.RoomTrackingOwner owner)
    {
        if (!Wire.RoomEditingIdentity.ValidOwner(owner) || owner.TabId != entry.Identity.TabId || owner.VisitId != entry.Identity.VisitId)
            throw new RoomTrackingProtocolException("The image request belongs to another visit.");
    }
    private static void Correct(RoomEditingTracker.Entry entry, Guid draft, long revision, Wire.RoomSceneImageBaseline baseline, bool reselect, string? message = null) =>
        RoomEditingMutationScope.Block(entry, Wire.RoomBlockedDisposition.CorrectiveBlocked,
            message ?? (reselect ? "The image input is unavailable. Select the file again before reviewing Apply." : "The room scene state changed. Review the current image baseline before applying."),
            new Wire.RoomSceneImageCorrectionEvidence(draft, revision, baseline, reselect));
    private static SceneImageCaptureDraft Capture(SceneImageCaptureDraft draft, Wire.RoomSceneImageSettings settings, SceneImageCaptureBaseline baseline,
        RoomImageUpload? resource, SceneImageUploadPreparation? preparation) => draft with
        {
            MinX = settings.MinX, MinY = settings.MinY, MaxX = settings.MaxX, MaxY = settings.MaxY,
            PixelsPerUnitX = settings.PixelsPerUnitX, PixelsPerUnitY = settings.PixelsPerUnitY, PixelOffsetX = settings.PixelOffsetX, PixelOffsetY = settings.PixelOffsetY,
            UseSceneImage = settings.Source != Wire.RoomSceneImageSource.None, UseUploadedSceneImage = settings.Source == Wire.RoomSceneImageSource.Uploaded,
            Baseline = baseline, UpdatedUtc = baseline.UpdatedUtc, UploadHandle = resource?.Handle,
            OriginalHash = resource?.Hash ?? baseline.OriginalHash, UploadPreparation = resource is null ? null : preparation
        };
    private static Wire.RoomSceneImageSettings Settings(SceneImageCaptureDraft draft) => new(draft.MinX, draft.MinY, draft.MaxX, draft.MaxY,
        draft.PixelsPerUnitX, draft.PixelsPerUnitY, draft.PixelOffsetX, draft.PixelOffsetY,
        !draft.UseSceneImage ? Wire.RoomSceneImageSource.None : draft.UseUploadedSceneImage ? Wire.RoomSceneImageSource.Uploaded : Wire.RoomSceneImageSource.ScriptersWar);
    private static SceneImageCaptureBaseline Baseline(Wire.RoomSceneImageBaseline x) => new(x.MinX, x.MinY, x.MaxX, x.MaxY,
        x.PixelsPerUnitX, x.PixelsPerUnitY, x.PixelOffsetX, x.PixelOffsetY, x.UseSceneImage, x.Stale, x.UpdatedUtc)
        { UseUploadedSceneImage = x.UseUploadedSceneImage, OriginalHash = x.OriginalHash, CropHash = x.CropHash };
    private static Wire.RoomSceneImageBaseline Baseline(SceneImageCaptureBaseline x) => new(x.MinX, x.MinY, x.MaxX, x.MaxY,
        x.PixelsPerUnitX, x.PixelsPerUnitY, x.PixelOffsetX, x.PixelOffsetY, x.UseSceneImage, x.UseUploadedSceneImage, x.Stale, x.OriginalHash, x.CropHash,
        // SQLite loses kind, not UTC ticks. Keep the exact image CAS timestamp.
        DateTime.SpecifyKind(x.UpdatedUtc, DateTimeKind.Utc));
}
