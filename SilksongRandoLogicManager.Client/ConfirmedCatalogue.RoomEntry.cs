using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

public sealed partial class ConfirmedCatalogue
{
    public sealed record RoomEntryCheckRead(RoomTargetObservation Observation, long RequestedCatalogueEpoch, long CompletedCatalogueEpoch);

    // Common mutation epoch/generation retires old publishing reads, NOT these
    // nonpublishing entry requests. Lifetime + mounted visit/attempt owns them;
    // check epoch changes are provenance for conservative reconciliation instead.
    public async Task<RoomBaselineAdmissionResponse?> AdmitRoomBaselineAsync(Guid roomId,
        RoomBaselineAdmissionRequest request, Func<bool> owns, CancellationToken token)
    {
        if (request.Owner.TabId != RoomTabId || !RoomEditingIdentity.ValidOwner(request.Owner)
            || !RoomEntryProtocol.ValidBaseline(request.Baseline, roomId)) throw new JsonException("Invalid local admission baseline.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        bool Current() => !disposed && !runtime.Retired && owns();
        await admission.WaitAsync(linked.Token);
        try { if (!Current()) return null; }
        finally { admission.Release(); }

        try
        {
            using var response = await http.PostAsJsonAsync<RoomOpenRequest>($"/api/v3/rooms/{roomId:D}/tracking/open", request, Json, linked.Token);
            if (response.StatusCode == HttpStatusCode.Gone) throw new RoomTrackingUnavailableException();
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<RoomOpenResponse>(Json, linked.Token) as RoomBaselineAdmissionResponse
                ?? throw new JsonException("Wrong room admission branch.");
            if (result.Identity is null || result.Identity.RoomId != roomId || result.Identity.TabId != request.Owner.TabId
                || result.Identity.VisitId != request.Owner.VisitId || result.Identity.TrackerId == Guid.Empty
                || !RoomEntryProtocol.ValidBaseline(result.Baseline, roomId)
                || !RoomEntryProtocol.SameBaseline(request.Baseline, result.Baseline)
                || result.Progress is null || result.Progress.RegisteredThrough < 0 || result.Progress.TerminalThrough < 0
                || result.Progress.AcknowledgedThrough < 0 || result.Progress.AcknowledgedThrough > result.Progress.TerminalThrough
                || result.Progress.TerminalThrough > result.Progress.RegisteredThrough)
                throw new JsonException("Wrong room baseline acknowledgement.");
            await admission.WaitAsync(linked.Token);
            try { return Current() ? result : null; }
            finally { admission.Release(); }
        }
        catch when (!Current()) { return null; }
    }

    public async Task<RoomEntryCheckRead?> CheckRoomEntryAsync(Guid roomId, Func<bool> owns, Action<bool> requestActivity, CancellationToken token)
    {
        var ownerEpoch = epoch;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        bool Current() => !disposed && !runtime.Retired && owns();
        await admission.WaitAsync(linked.Token);
        try { if (!Current()) return null; }
        finally { admission.Release(); }

        try
        {
            if (!Current()) return null;
            RoomTargetObservation? result;
            requestActivity(true);
            try
            {
                await RefreshAsync();
                result = Rooms.TryGetValue(roomId, out var room)
                    ? new RoomPresentObservation(room.Observation, room.Header.Observation.IsArchived, room.Header.Metadata)
                    : new RoomMissingObservation(roomId);
                if (!RoomEntryProtocol.ValidTarget(result, roomId)) throw new JsonException("Invalid targeted room observation.");
            }
            finally { if (Current()) requestActivity(false); }
            await admission.WaitAsync(linked.Token);
            try { return Current() ? new(result!, ownerEpoch, epoch) : null; }
            finally { admission.Release(); }
        }
        catch when (!Current()) { return null; }
    }
}
