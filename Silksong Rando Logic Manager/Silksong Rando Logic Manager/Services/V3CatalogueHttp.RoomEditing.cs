using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Silksong_Rando_Logic_Manager.Authentication;
using Wire = SilksongRandoLogicManager.Contracts;

namespace Silksong_Rando_Logic_Manager.Services;

public static partial class V3CatalogueHttp
{
    private static LiveAccess Access(HttpContext context) => context.RequestServices.GetRequiredService<LiveAccess>();
    private static Guid RoomGuid(string text) => Guid.TryParse(text, out var id) && id != Guid.Empty ? id : throw new RequestBindingException();
    private static Guid Nonempty(Guid id) => id != Guid.Empty ? id : throw new RequestBindingException();
    private static Wire.RoomTrackingIdentity RoomIdentity(Guid room, Guid tracker, Wire.RoomTrackingOwner owner)
    {
        if (!Wire.RoomEditingIdentity.ValidOwner(owner)) throw new RequestBindingException();
        return new(owner.TabId, owner.VisitId, Nonempty(room), Nonempty(tracker));
    }

    private static void MapRoomEditing(RouteGroupBuilder api)
    {
        api.MapPost("/rooms/{room}/tracking/open", async (HttpContext context, Guid room, Wire.RoomOpenRequest request,
            RoomEditingTracker tracker, RoomEditingDocumentLoader loader) =>
        {
            Nonempty(room);
            var result = await tracker.OpenAsync(Access(context), room, request, t => loader.LoadAsync(room, t), context.RequestAborted);
            if (result is null) await ErrorAsync(context, 404, "missing", "This room no longer exists.");
            else await WriteAsync(context, 200, result);
        }).RequireAuthorization(AccessPolicies.Normal);

        var visit = api.MapGroup("/rooms/{room}/tracking/{tracker}");
        visit.MapGet("/document", async (HttpContext context, Guid room, Guid tracker, Guid tabId, Guid visitId,
            RoomEditingTracker tracking, RoomEditingDocumentLoader loader) =>
        {
            var identity = RoomIdentity(room, tracker, new(tabId, visitId));
            var document = await tracking.ReadDocumentAsync(Access(context), identity, t => loader.LoadAsync(room, t), context.RequestAborted);
            await WriteAsync(context, 200, new Wire.RoomDocumentReadResponse(room, document));
        }).RequireAuthorization(AccessPolicies.Normal);
        visit.MapGet("/results", async (HttpContext context, Guid room, Guid tracker, Guid tabId, Guid visitId,
            long after, Guid? resolutionAttemptId, RoomEditingTracker tracking, RoomEditingRecovery recovery, RoomEditingImages images) =>
        {
            var identity = RoomIdentity(room, tracker, new(tabId, visitId));
            if (resolutionAttemptId is { } attempt) Nonempty(attempt);
            var result = await tracking.WithVisitAsync(Access(context), identity, async entry =>
            {
                await recovery.RecoverProjectionsAsync(entry);
                await images.CollectAsync(entry);
                return entry.Read(after, resolutionAttemptId: resolutionAttemptId);
            }, context.RequestAborted);
            await WriteCacheResultAsync(context, 200, result);
        }).RequireAuthorization(AccessPolicies.Normal);
        visit.MapPost("/batch", async (HttpContext context, Guid room, Guid tracker, Wire.RoomBatchRequest request,
            RoomEditingTracker tracking, RoomEditingWorkflow workflow) =>
        {
            var identity = RoomIdentity(room, tracker, request.Owner);
            var result = await tracking.WithVisitAsync(Access(context), identity, async entry =>
            {
                var start = Stopwatch.GetTimestamp();
                var registered = entry.RegisterBatch(request);
                if (registered) await workflow.ExecutePrefixAsync(entry);
                // Historical range is permitted only for a validated immutable duplicate.
                var after = registered ? request.Incorporated.IncorporatedThrough
                    : Math.Max(request.Incorporated.IncorporatedThrough, entry.Progress.AcknowledgedThrough);
                return entry.Read(after, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }, context.RequestAborted);
            await WriteCacheResultAsync(context, 200, result);
        }).RequireAuthorization(AccessPolicies.Author);
        visit.MapPost("/ack", async (HttpContext context, Guid room, Guid tracker, Wire.RoomAckRequest request, RoomEditingTracker tracking) =>
        {
            var identity = RoomIdentity(room, tracker, request.Owner);
            var result = await tracking.WithVisitAsync(Access(context), identity, entry =>
            {
                entry.Acknowledge(request);
                return Task.FromResult(entry.Read(request.Incorporated.IncorporatedThrough));
            }, context.RequestAborted);
            await WriteCacheResultAsync(context, 200, result);
        }).RequireAuthorization(AccessPolicies.Normal);
        visit.MapPost("/resolve", async (HttpContext context, Guid room, Guid tracker, Wire.RoomResolveRequest request,
            RoomEditingTracker tracking, RoomEditingWorkflow workflow) =>
        {
            var identity = RoomIdentity(room, tracker, request.Owner);
            var result = await tracking.WithVisitAsync(Access(context), identity, async entry =>
            {
                var start = Stopwatch.GetTimestamp();
                await workflow.ResolveAsync(entry, request);
                return entry.Read(entry.Progress.AcknowledgedThrough, Stopwatch.GetElapsedTime(start).TotalMilliseconds, request.ResolutionAttemptId);
            }, context.RequestAborted);
            await WriteCacheResultAsync(context, 200, result);
        }).RequireAuthorization(AccessPolicies.Author);

        var drafts = visit.MapGroup("/image-drafts").RequireAuthorization(AccessPolicies.Author);
        drafts.MapPost("/open", async (HttpContext context, Guid room, Guid tracker, Wire.RoomImageOpenRequest request,
            RoomEditingTracker tracking, RoomEditingImages images) =>
        {
            var identity = RoomIdentity(room, tracker, request.Owner);
            var result = await tracking.WithVisitAsync(Access(context), identity, entry => images.OpenAsync(entry, request), context.RequestAborted);
            await WriteAsync(context, 200, OwnedPreview(room, tracker, request.Owner, result));
        });
        drafts.MapPost("/{draft}/select", async (HttpContext context, Guid room, Guid tracker, Guid draft, Wire.RoomImageSelectRequest request,
            RoomEditingTracker tracking, RoomEditingImages images) =>
        {
            var identity = RoomIdentity(room, tracker, request.Owner); Nonempty(draft);
            var result = await tracking.WithVisitAsync(Access(context), identity, entry => images.SelectAsync(entry, draft, request), context.RequestAborted);
            await WriteAsync(context, 200, OwnedPreview(room, tracker, request.Owner, result));
        });
        drafts.MapPost("/{draft}/estimate", async (HttpContext context, Guid room, Guid tracker, Guid draft, Wire.RoomImageEstimateRequest request,
            RoomEditingTracker tracking, RoomEditingImages images) =>
        {
            var identity = RoomIdentity(room, tracker, request.Owner); Nonempty(draft);
            var result = await tracking.WithVisitAsync(Access(context), identity, entry => images.EstimateAsync(entry, draft, request), context.RequestAborted);
            await WriteAsync(context, 200, OwnedPreview(room, tracker, request.Owner, result));
        });
        drafts.MapPost("/{draft}/release", async (HttpContext context, Guid room, Guid tracker, Guid draft, Wire.RoomImageReleaseRequest request,
            RoomEditingTracker tracking, RoomEditingImages images) =>
        {
            var identity = RoomIdentity(room, tracker, request.Owner); Nonempty(draft);
            var result = await tracking.WithVisitAsync(Access(context), identity, async entry =>
            { await images.ReleaseAsync(entry, draft, request); return entry.Progress; }, context.RequestAborted);
            await WriteAsync(context, 200, result);
        });
        drafts.MapGet("/{draft}/uploads/{attempt}", async (HttpContext context, Guid room, Guid tracker, Guid draft, Guid attempt,
            Guid tabId, Guid visitId, RoomEditingTracker tracking, RoomEditingImages images) =>
        {
            var identity = RoomIdentity(room, tracker, new(tabId, visitId)); Nonempty(draft); Nonempty(attempt);
            var result = await tracking.WithVisitAsync(Access(context), identity,
                entry => Task.FromResult(images.Status(entry, draft, attempt)), context.RequestAborted);
            await WriteAsync(context, 200, result);
        });
        drafts.MapPost("/{draft}/uploads/{attempt}", RoomImageUploadAsync);
        drafts.MapGet("/{draft}/preview/{handle}", async (HttpContext context, Guid room, Guid tracker, Guid draft, Guid handle,
            Guid tabId, Guid visitId, RoomEditingTracker tracking, RoomEditingImages images) =>
        {
            var identity = RoomIdentity(room, tracker, new(tabId, visitId)); Nonempty(draft); Nonempty(handle);
            var bytes = await tracking.WithVisitAsync(Access(context), identity, entry => images.PreviewAsync(entry, draft, handle),
                context.RequestAborted, renewActivity: false);
            if (bytes is null) await ErrorAsync(context, 404, "missing", "This draft preview is no longer available.");
            else { context.Response.ContentType = "image/webp"; await context.Response.Body.WriteAsync(bytes, CancellationToken.None); }
        }).WithMetadata(new ApplicationVersionEndpoint(ApplicationVersionChannel.NativePreview));
    }

    private static async Task RoomImageUploadAsync(HttpContext context, Guid room, Guid tracker, Guid draft, Guid attempt,
        Guid tabId, Guid visitId, RoomEditingTracker tracking, RoomEditingImages images)
    {
        var identity = RoomIdentity(room, tracker, new(tabId, visitId)); Nonempty(draft); Nonempty(attempt);
        if (!string.Equals(context.Request.ContentType?.Split(';')[0].Trim(), "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        { await ErrorAsync(context, 415, "media", "Send an application/octet-stream image."); return; }
        if (context.Request.ContentLength > SceneImageFileService.UploadLimit)
        { await ErrorAsync(context, 413, "size", "Choose an image no larger than 256 MiB."); return; }
        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = SceneImageFileService.UploadLimit;
        var result = await tracking.WithVisitAsync(Access(context), identity,
            entry => images.UploadAsync(entry, draft, attempt, context.Request.ContentLength, context.Request.Body, context.RequestAborted), context.RequestAborted);
        await WriteAsync(context, 200, result);
    }

    private static Wire.RoomImageDraftView OwnedPreview(Guid room, Guid tracker, Wire.RoomTrackingOwner owner, Wire.RoomImageDraftView result)
    {
        if (result.PreviewUrl is not { } preview || !preview.StartsWith("/room-image-drafts/", StringComparison.Ordinal)) return result;
        var handle = RoomGuid(preview["/room-image-drafts/".Length..]);
        return result with { PreviewUrl = $"/api/v3/rooms/{room:D}/tracking/{tracker:D}/image-drafts/{result.DraftOwnerId:D}/preview/{handle:D}?tabId={owner.TabId:D}&visitId={owner.VisitId:D}&appVersion={ApplicationBuild.Version}" };
    }
}
