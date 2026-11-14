using System.Net;
using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

public sealed partial class ConfirmedCatalogue
{
    private HttpClient RoomHttp => http;
    private RequirementCatalogueManagerView? referenceSource;
    private RequirementCatalogueReferenceView? preparedReference;
    private string? referenceStateId;
    public async Task<RequirementCatalogueReferenceView?> LoadReferenceAsync(Func<bool> owns, CancellationToken token)
    {
        using var timing = OperationTiming.Start("requirements.reference.read");
        var ownerEpoch = epoch; long ownerGeneration = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        using var admissionTiming = OperationTiming.Start("requirements.reference.admission");
        await admission.WaitAsync(linked.Token);
        try
        {
            if (disposed || ownerEpoch != epoch || mutation || !owns()) return null;
            ownerGeneration = ++generation;
            admissionTiming.End();
            using var readTiming = OperationTiming.Start("requirements.reference.local-read");
            var state = await browser.ReadStateAsync(linked.Token);
            if (!Owns(ownerEpoch, ownerGeneration) || mutation || !owns()) return null;
            if (state is null) throw new InvalidOperationException("The local confirmed vocabulary is not ready.");
            var source = referenceSource;
            var reference = preparedReference;
            if (referenceStateId != state.StateId || source is null || reference is null)
            {
                var read = await browser.ReadVocabularyAsync(expected: state, token: linked.Token);
                source = await LocalLogicMaterializer.VocabularyAsync(read, referenceSource,
                    () => browser.YieldAsync(linked.Token), linked.Token);
                if (!ReferenceEquals(source, referenceSource) || reference is null)
                    reference = LocalLogicMaterializer.Reference(source);
            }
            readTiming.End();
            // Feature-owned pinned details, never manager/common CAT publication.
            var current = Owns(ownerEpoch, ownerGeneration) && !mutation && owns() && !linked.IsCancellationRequested;
            if (current) { referenceSource = source; preparedReference = reference; referenceStateId = state.StateId; }
            timing.End(current ? TimingOutcome.Returned : TimingOutcome.Retired);
            return current ? reference : null;
        }
        catch when (!Owns(ownerEpoch, ownerGeneration) || !owns()) { return null; }
        finally { admission.Release(); }
    }
    // An application instance is a browser document, not cloned tab storage.
    public Guid RoomTabId { get; } = Guid.NewGuid();
    public sealed class RoomTrackingUnavailableException() : Exception("Room tracking is no longer available. Some submitted work may already be saved.") { }
    public sealed record RoomSaveResponse(ReceivedCacheResult<RoomTrackingResponse> Received, double ElapsedMilliseconds);
    internal static string RoomTrackerPath(RoomTrackingIdentity identity) => $"/api/v3/rooms/{identity.RoomId:D}/tracking/{identity.TrackerId:D}";
    internal static string RoomOwnerQuery(RoomTrackingIdentity identity) => $"tabId={identity.TabId:D}&visitId={identity.VisitId:D}";
    public Task<RoomEditing.RoomVisitEntry?> OpenRoomAsync(Guid roomId, Guid visitId, Func<bool> owns, CancellationToken token)
        => OpenRoomAsync(roomId, visitId, owns, token, PreparationContext.Ordinary);

    internal async Task<RoomEditing.RoomVisitEntry?> OpenRoomAsync(Guid roomId, Guid visitId, Func<bool> owns, CancellationToken token,
        PreparationContext context)
    {
        await RefreshAsync(context);
        using var timing = OperationTiming.Start("room.open.admission");
        var ownerEpoch = epoch; long ownerGeneration = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await admission.WaitAsync(linked.Token);
        try
        {
            if (disposed || ownerEpoch != epoch || mutation || !owns()) return null;
            ownerGeneration = ++generation;
            timing.End();
            if (!Rooms.ContainsKey(roomId) || !browser.CanAdoptLocal) return null;
            var document = await ReadLocalDocumentAsync(roomId,
                () => Owns(ownerEpoch, ownerGeneration) && !mutation && owns(), linked.Token, context);
            if (document is null) return null;
            if (!Owns(ownerEpoch, ownerGeneration) || mutation || !owns()) return null;
            readObservations = readObservations.Targeted(roomId, document.Header.RoomGroupId);
            browser.DemandImage(roomId);
            return new(new(RoomTabId, visitId), document);
        }
        catch when (!Owns(ownerEpoch, ownerGeneration) || !owns()) { return null; }
        finally { admission.Release(); }
    }
    public async Task<RoomDocumentReadResponse?> ReadRoomDocumentAsync(RoomTrackingIdentity identity, Func<bool> owns, Action dispatched, CancellationToken token)
    {
        await RefreshAsync();
        var ownerEpoch = epoch; long ownerGeneration = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await admission.WaitAsync(linked.Token);
        try
        {
            if (disposed || ownerEpoch != epoch || mutation || !owns()) return null;
            ownerGeneration = ++generation;
            if (!browser.CanAdoptLocal) return null;
            dispatched();
            var document = await ReadLocalDocumentAsync(identity.RoomId,
                () => Owns(ownerEpoch, ownerGeneration) && !mutation && owns(), linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!Owns(ownerEpoch, ownerGeneration) || mutation || !owns()) return null;
            readObservations = readObservations.Targeted(identity.RoomId, document?.Header.RoomGroupId);
            return new(identity.RoomId, document);
        }
        catch when (!Owns(ownerEpoch, ownerGeneration) || !owns()) { return null; }
        finally { admission.Release(); }
    }

    // One serialized named owner read supplies ONLY this full Room, its scene inputs
    // and exact dependency/orders. Never retain it in the application catalogue.
    private async Task<RoomCompleteDocument?> ReadLocalDocumentAsync(Guid roomId, Func<bool> current, CancellationToken token,
        PreparationContext preparationContext = PreparationContext.Ordinary)
    {
        token.ThrowIfCancellationRequested();
        if (!current()) throw new OperationCanceledException();
        var state = await browser.ReadStateAsync(token);
        token.ThrowIfCancellationRequested();
        if (!current()) throw new OperationCanceledException();
        if (state is null || state.StateId != appliedLocalStateId || !browser.CanAdoptLocal)
            throw new InvalidOperationException("The local Room and common facts are not at the same confirmed checkpoint.");
        var read = await browser.ReadRoomAsync(roomId, state, token);
        token.ThrowIfCancellationRequested();
        if (!current()) throw new OperationCanceledException();
        async Task YieldCurrent()
        {
            token.ThrowIfCancellationRequested();
            if (!current()) throw new OperationCanceledException();
            if (preparationContext == PreparationContext.Ordinary) await browser.YieldAsync(token);
            token.ThrowIfCancellationRequested();
            if (!current()) throw new OperationCanceledException();
        }
        var document = await LocalRoomMaterializer.RoomAsync(read, roomId, browser.Images, YieldCurrent, token);
        token.ThrowIfCancellationRequested();
        if (!current()) throw new OperationCanceledException();
        Rooms.TryGetValue(roomId, out var known);
        if ((document is null) != (known is null) || document is not null &&
            (document.Observation != known!.Observation || document.Header.Observation.IsArchived != known.Header.Observation.IsArchived
                || document.ReadContext is not { } context || !context.RoomOrder.SequenceEqual(PresentationRoomIds)
                || !context.GroupOrder.SequenceEqual(Groups.Select(x => x.Id))
                || context.Group != Groups.SingleOrDefault(x => x.Id == document.Header.RoomGroupId)))
            throw new JsonException("Demanded Room does not match its coherent common context.");
        return document;
    }

    public sealed partial class MutationAdmission
    {
        public Task<RoomSaveResponse> SubmitRoomBatchAsync(RoomTrackingIdentity identity, RoomBatchRequest request, Action dispatched) =>
            PostRoomSaveAsync(identity, "batch", request, dispatched);
        public Task<RoomSaveResponse> ResolveRoomHeadAsync(RoomTrackingIdentity identity, RoomResolveRequest request, Action dispatched) => PostRoomSaveAsync(identity, "resolve", request, dispatched);
        public Task<ReceivedCacheResult<RoomTrackingResponse>> AcknowledgeRoomAsync(RoomTrackingIdentity identity, RoomAckRequest request) => PostRoomAsync(identity, "ack", request);
        private async Task<RoomSaveResponse> PostRoomSaveAsync<T>(RoomTrackingIdentity identity, string action, T request, Action? dispatched = null)
        {
            if (!Current(() => true)) throw new OperationCanceledException();
            using var timing = OperationTiming.Start(action == "batch" ? "save.batch.prepare-send-decode" : "save.resolve.prepare-send-decode");
            using var message = new HttpRequestMessage(HttpMethod.Post, RoomTrackerPath(identity) + "/" + action)
            { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, Json)) };
            message.Content.Headers.ContentType = new("application/json");
            var cacheRequest = BindCache(message);
            // Dispatch evidence belongs immediately before SendAsync, not when a
            // batch is prepared or while central admission is still being acquired.
            dispatched?.Invoke();
            var started = Stopwatch.GetTimestamp();
            using var sendTiming = OperationTiming.Start("save.buffered-send");
            using var response = await store.RoomHttp.SendAsync(message, CancellationToken.None);
            sendTiming.End();
            var result = await DecodeRoomResponseAsync(response, cacheRequest, timing.Id);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            timing.End(TimingOutcome.ResponseDecoded);
            return new(result, elapsed);
        }
        private async Task<ReceivedCacheResult<RoomTrackingResponse>> PostRoomAsync<T>(RoomTrackingIdentity identity, string action, T request)
        {
            if (!Current(() => true)) throw new OperationCanceledException();
            using var timing = OperationTiming.Start("save.ack.buffered-send");
            using var message = new HttpRequestMessage(HttpMethod.Post, RoomTrackerPath(identity) + "/" + action)
                { Content = JsonContent.Create(request, options: Json) };
            var cacheRequest = BindCache(message);
            using var response = await store.RoomHttp.SendAsync(message, CancellationToken.None);
            timing.End();
            return await DecodeRoomResponseAsync(response, cacheRequest, timing.Id);
        }
        private static async Task<ReceivedCacheResult<RoomTrackingResponse>> DecodeRoomResponseAsync(HttpResponseMessage response, CacheRequest request, long diagnosticParent)
        {
            using var timing = OperationTiming.Start("save.decode", parent: diagnosticParent);
            if (response.StatusCode == HttpStatusCode.Gone) throw new RoomTrackingUnavailableException();
            response.EnsureSuccessStatusCode();
            var result = await LogicBrowserCache.DecodeAsync<RoomTrackingResponse>(response, request);
            timing.End(TimingOutcome.ResponseDecoded);
            return result;
        }
        public async Task<ReceivedCacheResult<RoomTrackingResponse>> RecoverRoomAsync(RoomTrackingIdentity identity, long incorporatedThrough, Guid? resolutionAttemptId = null)
        {
            if (!Current(() => true)) throw new OperationCanceledException();
            using var timing = OperationTiming.Start("save.recovery.buffered-send");
            var path = RoomTrackerPath(identity) + $"/results?{RoomOwnerQuery(identity)}&after={incorporatedThrough}";
            if (resolutionAttemptId is { } attempt) path += $"&resolutionAttemptId={attempt:D}";
            using var message = new HttpRequestMessage(HttpMethod.Get, path);
            var cacheRequest = BindCache(message);
            using var response = await store.RoomHttp.SendAsync(message, CancellationToken.None);
            timing.End();
            return await DecodeRoomResponseAsync(response, cacheRequest, timing.Id);
        }
    }
}
