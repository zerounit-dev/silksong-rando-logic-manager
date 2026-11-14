using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

public sealed partial class ConfirmedCatalogue
{
    private async Task<T?> AdmitImageReadAsync<T>(RoomTrackingIdentity identity, Func<HttpRequestMessage> request, Func<bool> owns, CancellationToken token) where T : class
    {
        var ownerEpoch = epoch; long ownerGeneration = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await admission.WaitAsync(linked.Token);
        try
        {
            if (disposed || ownerEpoch != epoch || mutation || !owns()) return null;
            ownerGeneration = ++generation;
            using var message = request();
            using var response = await http.SendAsync(message, linked.Token);
            if (response.StatusCode == HttpStatusCode.Gone) throw new RoomTrackingUnavailableException();
            response.EnsureSuccessStatusCode();
            var value = await response.Content.ReadFromJsonAsync<T>(Json, linked.Token) ?? throw new JsonException();
            return Owns(ownerEpoch, ownerGeneration) && !mutation && owns() ? value : null;
        }
        catch when (!Owns(ownerEpoch, ownerGeneration) || !owns()) { return null; }
        finally { admission.Release(); }
    }
    public Task<RoomImageDraftView?> OpenRoomImageAsync(RoomTrackingIdentity identity, RoomImageOpenRequest request, Func<bool> owns, CancellationToken token) =>
        AdmitImageReadAsync<RoomImageDraftView>(identity, () => new(HttpMethod.Post, RoomTrackerPath(identity) + "/image-drafts/open") { Content = JsonContent.Create(request, options: Json) }, owns, token);
    public Task<RoomImageDraftView?> SelectRoomImageAsync(RoomTrackingIdentity identity, Guid draft, RoomImageSelectRequest request, Func<bool> owns, CancellationToken token) =>
        AdmitImageReadAsync<RoomImageDraftView>(identity, () => new(HttpMethod.Post, RoomTrackerPath(identity) + $"/image-drafts/{draft:D}/select") { Content = JsonContent.Create(request, options: Json) }, owns, token);
    public Task<RoomImageDraftView?> EstimateRoomImageAsync(RoomTrackingIdentity identity, Guid draft, RoomImageEstimateRequest request, Func<bool> owns, CancellationToken token) =>
        AdmitImageReadAsync<RoomImageDraftView>(identity, () => new(HttpMethod.Post, RoomTrackerPath(identity) + $"/image-drafts/{draft:D}/estimate") { Content = JsonContent.Create(request, options: Json) }, owns, token);
    public Task<RoomImageUploadView?> ReadRoomImageUploadAsync(RoomTrackingIdentity identity, Guid draft, Guid attempt, Func<bool> owns, CancellationToken token) =>
        AdmitImageReadAsync<RoomImageUploadView>(identity, () => new(HttpMethod.Get, RoomTrackerPath(identity) + $"/image-drafts/{draft:D}/uploads/{attempt:D}?" + RoomOwnerQuery(identity)), owns, token);
    public Task<RoomImageUploadView?> UploadRoomImageAsync(RoomTrackingIdentity identity, Guid draft, Guid attempt, Stream stream, Func<bool> owns, CancellationToken token) =>
        AdmitImageReadAsync<RoomImageUploadView>(identity, () =>
        {
            var message = new HttpRequestMessage(HttpMethod.Post, RoomTrackerPath(identity) + $"/image-drafts/{draft:D}/uploads/{attempt:D}?" + RoomOwnerQuery(identity)) { Content = new StreamContent(stream) };
            message.Content.Headers.ContentType = new("application/octet-stream");
            message.SetBrowserRequestStreamingEnabled(true);
            return message;
        }, owns, token);
    public async Task ReleaseRoomImageAsync(RoomTrackingIdentity identity, Guid draft, RoomImageReleaseRequest request, CancellationToken token)
    {
        // Release is resource retirement, never a room mutation. The tracker owns
        // pins for submitted/uncertain work; disposal cannot discard their input.
        using var response = await http.PostAsJsonAsync(RoomTrackerPath(identity) + $"/image-drafts/{draft:D}/release", request, Json, token);
        if (response.StatusCode == HttpStatusCode.Gone) return;
        response.EnsureSuccessStatusCode();
    }
}
