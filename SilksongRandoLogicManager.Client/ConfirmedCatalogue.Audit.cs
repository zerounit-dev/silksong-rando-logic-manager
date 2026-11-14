using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Client;

public sealed partial class ConfirmedCatalogue
{
    public async Task<RoomAuditSnapshot?> LoadAuditAsync(Guid roomId, Func<bool> owns, CancellationToken token)
    {
        using var timing = OperationTiming.Start("audit.snapshot.read");
        var ownerEpoch = epoch; long ownerGeneration = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        using var admissionTiming = OperationTiming.Start("audit.snapshot.admission");
        await admission.WaitAsync(linked.Token);
        try
        {
            RetireUnless(!disposed && ownerEpoch == epoch && !mutation && owns());
            ownerGeneration = ++generation;
            admissionTiming.End();
            using var sendTiming = OperationTiming.Start("audit.snapshot.buffered-send");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v3/rooms/{roomId:D}/audit");
            var cacheRequest = browser.Bind(request);
            using var response = await http.SendAsync(request, linked.Token);
            sendTiming.End();
            response.EnsureSuccessStatusCode();
            using var decodeTiming = OperationTiming.Start("audit.snapshot.decode");
            var received = await LogicBrowserCache.DecodeAsync<AuditSnapshotResponse>(response, cacheRequest, linked.Token);
            var envelope = received.Result;
            try
            {
            decodeTiming.End(TimingOutcome.ResponseDecoded);
            using var freezeTiming = OperationTiming.Start("audit.snapshot.freeze");
            var snapshot = envelope.Snapshot is { } value ? AuditSnapshotReader.Freeze(value, roomId) : null;
            freezeTiming.End(snapshot is null ? TimingOutcome.Skipped : TimingOutcome.Complete);
            RetireUnless(Owns(ownerEpoch, ownerGeneration) && !mutation && owns());
            linked.Token.ThrowIfCancellationRequested();
            timing.End(snapshot is null ? TimingOutcome.Missing : TimingOutcome.Returned);
            return snapshot;
            }
            finally { browser.Publish(received); }
        }
        catch when (!Owns(ownerEpoch, ownerGeneration) || !owns()) { throw new OperationCanceledException("Audit read retired."); }
        finally { admission.Release(); }
    }

    private static void RetireUnless(bool current)
    { if (!current) throw new OperationCanceledException("Audit read retired."); }

    public AuditRoomCatalogue CaptureAuditCatalogue()
    {
        var current = Sidebar;
        var groups = current.Groups.ToDictionary(x => x.Id);
        return new(current.Rooms.Where(x => !x.IsArchived).Select(room =>
        {
            var group = room.GroupId is { } id ? groups[id] : null;
            return new AuditRoomCatalogueEntry(room.Id, room.FriendlyName, room.InGameId, room.GroupId,
                group?.FriendlyName, group?.SortOrder ?? 0, room.SortOrder);
        }).OrderBy(x => x.GroupId.HasValue).ThenBy(x => x.GroupSortOrder).ThenBy(x => x.GroupName, StringComparer.Ordinal)
            .ThenBy(x => x.GroupId).ThenBy(x => x.SortOrder).ThenBy(x => x.FriendlyName, StringComparer.Ordinal).ThenBy(x => x.Id).ToImmutableArray());
    }
}
