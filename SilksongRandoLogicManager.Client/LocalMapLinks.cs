using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

// Diagnostics over confirmed persisted resolver IDs; never writes/re-resolves them.
// Matching deliberately follows the server report's .NET trim/OrdinalIgnoreCase,
// including archived matches and OutOfSync, not SQL collation or JS case folding.
internal static class LocalMapLinks
{
    internal static LandingLinks Prepare(MapDataSnapshot input, IReadOnlyDictionary<Guid, ConfirmedRoomFacts> rooms,
        Guid? room, Guid? scene)
    {
        var zones = input.Zones.ToDictionary(x => x.Id);
        var chunks = input.Chunks.ToLookup(x => x.MapSceneId);
        var references = rooms.Values.GroupBy(x => x.Header.Values.ReferenceId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var selected = input.Scenes.Where(x => room is { } r ? x.ResolvedRoomId == r : scene is not { } s || x.Id == s);
        var rows = selected.OrderBy(x => zones[x.MapZoneId].InGameId).ThenBy(x => x.InGameId).Select(x =>
        {
            string? status = null;
            if (!string.IsNullOrWhiteSpace(x.RoomReferenceText))
            {
                var matches = references.GetValueOrDefault(x.RoomReferenceText.Trim()) ?? [];
                var active = matches.Where(r => !r.Header.Observation.IsArchived).ToArray();
                status = active.Length > 1 ? "Ambiguous" : active.Length == 1
                    ? active[0].Observation.RoomId == x.ResolvedRoomId ? "Resolved" : "OutOfSync"
                    : matches.Length > 0 ? "TargetArchived" : "Unresolved";
            }
            var name = x.ResolvedRoomId is { } id && rooms.TryGetValue(id, out var linked) && !linked.Header.Observation.IsArchived
                ? linked.Header.Values.FriendlyName : null;
            return new LandingLinkRow(x.Id, zones[x.MapZoneId].InGameId, x.InGameId,
                string.Join(", ", chunks[x.Id].OrderBy(c => c.CacheIndex).Select(c => c.CacheIndex)), x.RoomReferenceText, status, name);
        }).ToArray();
        return new(rows, rooms.Values.Where(x => !x.Header.Observation.IsArchived).OrderBy(x => x.Header.Values.ReferenceId)
            .Select(x => x.Header.Values.ReferenceId).ToArray());
    }
}
