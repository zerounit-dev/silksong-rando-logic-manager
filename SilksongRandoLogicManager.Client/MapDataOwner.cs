using System.Collections.Immutable;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;
using SilksongRandoLogicManager.Contracts.Diagnostics;

namespace SilksongRandoLogicManager.Client;

/// <summary>Application-lifetime immutable imported input and demanded-map derivations.
/// Canonical room records/statuses remain references to the shared owner, not a replica.</summary>
public sealed class MapDataOwner : IDisposable
{
    internal sealed class Entry
    {
        internal required MapDataSnapshot Input { get; init; }
        internal required Hierarchy Index { get; init; }
        internal required ImmutableDictionary<Guid, Guid?> Associations { get; init; }
        internal required ImmutableDictionary<Guid, Guid?> EffectiveOwners { get; init; }
        internal required ImmutableDictionary<Guid, ImmutableArray<MapDataZone>> RoomZones { get; init; }
        internal required ImmutableDictionary<Guid, ConfirmedRoomFacts> Canonical { get; set; }
        internal required ImmutableHashSet<Guid> AssociatedRooms { get; init; }
        internal ImmutableDictionary<Guid, Derived> Maps { get; set; } = ImmutableDictionary<Guid, Derived>.Empty;
        internal ImmutableHashSet<Guid> ValidMaps { get; set; } = ImmutableHashSet<Guid>.Empty;
        internal ImmutableHashSet<Guid> ProjectionValidMaps { get; set; } = ImmutableHashSet<Guid>.Empty;
        internal ImmutableHashSet<Guid> UnlinkedValidMaps { get; set; } = ImmutableHashSet<Guid>.Empty;
        internal ImmutableHashSet<Guid> OverlayValidMaps { get; set; } = ImmutableHashSet<Guid>.Empty;
    }
    internal sealed record Hierarchy(ImmutableArray<MapDataMap> OrderedMaps,
        ImmutableDictionary<Guid, MapDataMap> Maps, ImmutableDictionary<Guid, MapDataZone> Zones,
        ImmutableDictionary<Guid, MapDataScene> Scenes, ImmutableDictionary<Guid, MapDataChunk> Chunks,
        Membership Membership,
        ImmutableDictionary<Guid, MapDataOverlay> Overlays);
    internal sealed record Membership(ImmutableHashSet<Guid> MapIds,
        ImmutableDictionary<Guid, Guid> ZoneMaps, ImmutableDictionary<Guid, Guid> SceneZones,
        ImmutableDictionary<Guid, Guid> ChunkScenes, ImmutableDictionary<Guid, ImmutableArray<Guid>> MapChunks);
    internal sealed record Derived(ImmutableArray<MapRenderProjectionChunk> Inputs,
        GlobalAreaMapGeometryView Geometry, ImmutableDictionary<Guid, AreaMapOwnerDecoration> Unlinked,
        AreaMapOverlayView? Overlay,
        ImmutableDictionary<Guid, AreaMapOwnerDecoration> Linked,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> LabelSource,
        ImmutableDictionary<Guid, RoomStatus> StatusSource);
    private Entry? retained;
    internal bool IsDisposed { get; private set; }
    internal Entry? Capture() => IsDisposed ? null : retained;
    internal void Accept(Entry entry) { if (!IsDisposed) retained = entry; }

    internal Entry Prepare(MapDataSnapshot input, ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms,
        ImmutableDictionary<Guid, RoomStatus> statuses, long observation = 0)
    {
        using var freeze = observation != 0 ? OperationTiming.Start("local-map-freeze", parent: observation) : null;
        var frozen = Freeze(input);
        freeze?.End();
        using var indexing = observation != 0 ? OperationTiming.Start("local-map-hierarchy-index-validation", parent: observation) : null;
        var index = Index(frozen);
        if (indexing is not null)
        {
            new LocalLoadWork().Report(indexing, ReferenceEquals(index, retained?.Index),
                (long)frozen.Maps.Count + frozen.Zones.Count + frozen.Scenes.Count + frozen.Chunks.Count + frozen.FirstOverlays.Count);
            indexing.End();
        }
        using var coverage = observation != 0 ? OperationTiming.Start("local-map-owner-coverage-associations", parent: observation) : null;
        // Validate *every* supplied owner, including scenes without renderable chunks.
        foreach (var scene in frozen.Scenes)
            if (scene.ResolvedRoomId is { } id && (!rooms.ContainsKey(id) || !statuses.ContainsKey(id)))
                throw new JsonException("Map owner lacks canonical coverage.");
        var associations = frozen.Scenes.ToImmutableDictionary(x => x.Id, x => x.ResolvedRoomId);
        coverage?.End();
        return Create(frozen, index, associations, rooms, retained, observation);
    }

    internal Entry Synchronize(ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms,
        ImmutableDictionary<Guid, RoomStatus> statuses, long observation = 0)
    {
        using var synchronize = observation != 0 ? OperationTiming.Start("local-map-retained-input-synchronize", parent: observation) : null;
        var entry = retained ?? throw new InvalidOperationException("No accepted map input.");
        if (ReferenceEquals(entry.Canonical, rooms)) { synchronize?.End(TimingOutcome.Skipped); return entry; }
        var lifecycleChanged = false;
        foreach (var id in entry.AssociatedRooms)
        {
            if (!rooms.TryGetValue(id, out var room)) { lifecycleChanged = true; continue; }
            if (!statuses.ContainsKey(id)) throw new JsonException("Canonical map owner lacks status.");
            if (room.Header.Observation.IsArchived != entry.Canonical[id].Header.Observation.IsArchived) lifecycleChanged = true;
        }
        if (!lifecycleChanged) { entry.Canonical = rooms; synchronize?.End(); return entry; }
        // All associations were canonically covered at acceptance. Public canonical
        // absence now follows admitted removal; never apply this rule to a new candidate.
        var associations = entry.Associations.ToImmutableDictionary(x => x.Key,
            x => x.Value is { } id && rooms.ContainsKey(id) ? x.Value : null);
        foreach (var id in associations.Values.OfType<Guid>())
            if (!statuses.ContainsKey(id)) throw new JsonException("Canonical map owner lacks status.");
        var candidate = Create(entry.Input, entry.Index, associations, rooms, entry, observation);
        synchronize?.End();
        return candidate;
    }

    private static Entry Create(MapDataSnapshot input, Hierarchy index, ImmutableDictionary<Guid, Guid?> associations,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms, Entry? previous, long observation = 0)
    {
        using var create = observation != 0 ? OperationTiming.Start("local-map-effective-associations-context-reuse-preparation", parent: observation) : null;
        var effective = associations.ToImmutableDictionary(x => x.Key,
            x => x.Value is { } id && !rooms[id].Header.Observation.IsArchived ? x.Value : null);
        if (previous is not null && Same(previous.EffectiveOwners, effective)) effective = previous.EffectiveOwners;
        var roomZones = previous is not null && ReferenceEquals(effective, previous.EffectiveOwners)
            && ReferenceEquals(index.Membership, previous.Index.Membership)
            && index.Zones.All(x => previous.Index.Zones.TryGetValue(x.Key, out var old) && x.Value.MapId == old.MapId
                && MapContextSelection.ZoneBounds(x.Value) == MapContextSelection.ZoneBounds(old))
            ? previous.RoomZones
            : input.Scenes.Where(x => effective[x.Id] is not null).GroupBy(x => effective[x.Id]!.Value)
                .ToImmutableDictionary(g => g.Key, g => g.Select(x => index.Zones[x.MapZoneId]).DistinctBy(x => x.Id).ToImmutableArray());
        var projectionValid = previous is null ? ImmutableHashSet<Guid>.Empty : previous.ProjectionValidMaps.Where(id =>
            index.Maps.ContainsKey(id) && ProjectionFactsEqual(id, index, previous.Index, effective, previous.EffectiveOwners)).ToImmutableHashSet();
        var unlinkedValid = previous is null ? ImmutableHashSet<Guid>.Empty : previous.UnlinkedValidMaps.Where(id =>
            projectionValid.Contains(id) && DecorationFactsEqual(id, index, previous.Index)).ToImmutableHashSet();
        var overlayValid = previous is null ? ImmutableHashSet<Guid>.Empty : previous.OverlayValidMaps.Where(id =>
            index.Maps.TryGetValue(id, out var map) && MapContextSelection.MapBounds(map) == MapContextSelection.MapBounds(previous.Index.Maps[id])
            && index.Overlays.GetValueOrDefault(id) == previous.Index.Overlays.GetValueOrDefault(id)).ToImmutableHashSet();
        var valid = projectionValid.Intersect(unlinkedValid).Intersect(overlayValid).Where(id =>
            MapContextSelection.MapBounds(index.Maps[id]) == MapContextSelection.MapBounds(previous!.Index.Maps[id])).ToImmutableHashSet();
        var result = new Entry() { Input = input, Index = index, Associations = associations, EffectiveOwners = effective,
            AssociatedRooms = associations.Values.OfType<Guid>().ToImmutableHashSet(),
            RoomZones = roomZones, Canonical = rooms, Maps = previous?.Maps.Where(x => index.Maps.ContainsKey(x.Key)).ToImmutableDictionary()
                ?? ImmutableDictionary<Guid, Derived>.Empty, ValidMaps = valid, ProjectionValidMaps = projectionValid,
            UnlinkedValidMaps = unlinkedValid, OverlayValidMaps = overlayValid };
        if (create is not null) { new LocalLoadWork().Report(create, ReferenceEquals(effective, previous?.EffectiveOwners), associations.Count); create.End(); }
        return result;
    }
    private static bool ProjectionFactsEqual(Guid id, Hierarchy a, Hierarchy b, IReadOnlyDictionary<Guid, Guid?> ownersA, IReadOnlyDictionary<Guid, Guid?> ownersB)
    {
        var chunksA = a.Membership.MapChunks.GetValueOrDefault(id, []); var chunksB = b.Membership.MapChunks.GetValueOrDefault(id, []);
        var priorIds = chunksB.ToHashSet();
        return chunksA.Length == chunksB.Length && chunksA.Select(chunk => a.Chunks[chunk]).All(x => priorIds.Contains(x.Id) && b.Chunks.TryGetValue(x.Id, out var old)
            && x.MinX == old.MinX && x.MinY == old.MinY && x.MaxX == old.MaxX && x.MaxY == old.MaxY && x.Z == old.Z
            && ownersA[x.MapSceneId] == ownersB.GetValueOrDefault(old.MapSceneId));
    }
    private static bool DecorationFactsEqual(Guid id, Hierarchy a, Hierarchy b)
    {
        var ids = a.Membership.MapChunks.GetValueOrDefault(id, []);
        return ids.All(chunk => b.Chunks.TryGetValue(chunk, out var old) && a.Chunks[chunk].CacheIndex == old.CacheIndex
            && a.Chunks[chunk].MapSceneId == old.MapSceneId
            && a.Scenes[a.Chunks[chunk].MapSceneId].InGameId == b.Scenes[old.MapSceneId].InGameId
            && a.Zones[a.Scenes[a.Chunks[chunk].MapSceneId].MapZoneId].InGameId == b.Zones[b.Scenes[old.MapSceneId].MapZoneId].InGameId);
    }
    private static bool Same<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> a, IReadOnlyDictionary<TKey, TValue> b) where TKey : notnull =>
        a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var value) && EqualityComparer<TValue>.Default.Equals(x.Value, value));

    internal LandingAreaMapView Landing(Entry entry, ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms,
        ImmutableDictionary<Guid, RoomStatus> statuses, long observation = 0)
    {
        using var selection = observation != 0 ? OperationTiming.Start("local-home-map-context-selection", parent: observation) : null;
        var map = entry.Index.OrderedMaps.FirstOrDefault();
        if (map is null) { selection?.End(TimingOutcome.Missing); return new(LandingAreaMapState.NoImportedMap, null); }
        if (MapContextSelection.MapBounds(map) is not { } bounds) { selection?.End(TimingOutcome.Missing); return new(LandingAreaMapState.UnusableGeometry, null); }
        selection?.End();
        return new(LandingAreaMapState.Ready, Surface(entry, map, new("map-wireframe", map.Id.ToString(), bounds, null, false), rooms, statuses, observation));
    }
    internal RoomMapContextView Room(Entry entry, Guid room, ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms,
        ImmutableDictionary<Guid, RoomStatus> statuses, long observation = 0)
    {
        using var selection = observation != 0 ? OperationTiming.Start("local-room-map-context-selection", parent: observation) : null;
        var (map, zone) = MapContextSelection.Select(entry.Index.OrderedMaps, entry.Index.Maps,
            entry.RoomZones.TryGetValue(room, out var zones) ? zones : ImmutableArray<MapDataZone>.Empty);
        if (map is null || MapContextSelection.MapBounds(map) is not { } bounds)
        { selection?.End(TimingOutcome.Missing); return new(room, entry.Index.Maps.Count == 0 ? RoomMapContextState.NoImportedMap : RoomMapContextState.UnusableGeometry, null); }
        var initial = zone is null ? bounds : MapContextSelection.ProjectZone(MapContextSelection.ZoneBounds(zone)!, bounds);
        var key = zone is null ? $"room-context-map-{map.Id:N}" : $"room-context-{zone.Id:N}";
        selection?.End();
        return new(room, RoomMapContextState.Ready,
            Surface(entry, map, new($"room-map-context-{room:N}", key, initial, room, true), rooms, statuses, observation));
    }

    private static AreaMapSurfaceView Surface(Entry entry, MapDataMap map, AreaMapHostContextView context,
        ImmutableDictionary<Guid, ConfirmedRoomFacts> rooms, ImmutableDictionary<Guid, RoomStatus> statuses, long observation = 0)
    {
        using var surface = observation != 0 ? OperationTiming.Start("local-map-demanded-surface", parent: observation) : null;
        entry.Maps.TryGetValue(map.Id, out var prior);
        var derived = prior;
        if (!entry.ValidMaps.Contains(map.Id))
        {
        using var inputWork = observation != 0 ? OperationTiming.Start("local-map-projection-inputs-compare", parent: observation) : null;
        var chunks = entry.Index.Membership.MapChunks.GetValueOrDefault(map.Id, []).Select(id => entry.Index.Chunks[id]);
        var inputs = entry.ProjectionValidMaps.Contains(map.Id) ? prior!.Inputs : chunks.Where(x => x.MinX.HasValue && x.MinY.HasValue && x.MaxX.HasValue && x.MaxY.HasValue)
            .Select(x => new MapRenderProjectionChunk(x.Id, x.Id.ToString(), entry.EffectiveOwners[x.MapSceneId],
                x.MinX!.Value, x.MinY!.Value, x.MaxX!.Value, x.MaxY!.Value, x.Z)).OrderBy(x => x.ChunkId).ToImmutableArray();
        var unchanged = prior is not null && prior.Inputs.SequenceEqual(inputs);
        if (inputWork is not null) { new LocalLoadWork().Report(inputWork, unchanged, inputs.Length); inputWork.End(); }
        using var projection = observation != 0 ? OperationTiming.Start("local-map-geometry-projection", parent: observation) : null;
        var counts = projection is not null && !unchanged ? new MapProjectionObservation() : null;
        var owners = unchanged ? prior!.Geometry.Owners : MapRenderProjection.Project(new(map.Id, inputs), counts).ToImmutableArray();
        if (projection is not null)
        {
            if (counts is not null) LocalLoadWork.Geometry(projection, counts);
            projection.End(unchanged ? TimingOutcome.Skipped : TimingOutcome.Complete);
        }
        var bounds = MapContextSelection.MapBounds(map)!;
        using var keyWork = observation != 0 ? OperationTiming.Start("local-map-cache-key-geometry-assembly", parent: observation) : null;
        var geometry = unchanged && prior!.Geometry.MapBounds == bounds ? prior.Geometry
            : new GlobalAreaMapGeometryView(map.Id, bounds, unchanged ? prior!.Geometry.CacheKey : MapRenderProjection.CreateCacheKey(map.Id, inputs), owners, inputs.Length);
        if (keyWork is not null) { new LocalLoadWork().Report(keyWork, ReferenceEquals(geometry, prior?.Geometry)); keyWork.End(); }
        using var decoration = observation != 0 ? OperationTiming.Start("local-map-unlinked-overlay-decoration", parent: observation) : null;
        var unlinked = entry.UnlinkedValidMaps.Contains(map.Id) ? prior!.Unlinked : owners.Where(x => x.UnlinkedChunkId is not null).Select(x => entry.Index.Chunks[x.UnlinkedChunkId!.Value])
            .ToImmutableDictionary(x => x.Id, x => {
                var scene = entry.Index.Scenes[x.MapSceneId]; var zone = entry.Index.Zones[scene.MapZoneId];
                return new AreaMapOwnerDecoration(null, scene.Id, $"unlinked | {zone.InGameId} | {scene.InGameId}-{x.CacheIndex}", AppliedRoomStatus.Neutral);
            });
        if (prior is not null && Same(prior.Unlinked, unlinked)) unlinked = prior.Unlinked;
        var overlay = entry.OverlayValidMaps.Contains(map.Id) ? prior!.Overlay : Overlay(entry.Index.Overlays.GetValueOrDefault(map.Id), bounds);
        if (overlay == prior?.Overlay) overlay = prior?.Overlay;
        derived = new Derived(unchanged ? prior!.Inputs : inputs, geometry, unlinked, overlay,
            prior?.Linked ?? ImmutableDictionary<Guid, AreaMapOwnerDecoration>.Empty,
            prior?.LabelSource ?? ImmutableDictionary<Guid, ConfirmedRoomFacts>.Empty,
            prior?.StatusSource ?? ImmutableDictionary<Guid, RoomStatus>.Empty);
        if (prior is not null && ReferenceEquals(prior.Geometry, geometry) && ReferenceEquals(prior.Unlinked, unlinked) && ReferenceEquals(prior.Overlay, overlay)) derived = prior;
        entry.Maps = entry.Maps.SetItem(map.Id, derived);
        entry.ValidMaps = entry.ValidMaps.Add(map.Id);
        entry.ProjectionValidMaps = entry.ProjectionValidMaps.Add(map.Id);
        entry.UnlinkedValidMaps = entry.UnlinkedValidMaps.Add(map.Id);
        entry.OverlayValidMaps = entry.OverlayValidMaps.Add(map.Id);
        if (decoration is not null) { new LocalLoadWork().Report(decoration, ReferenceEquals(unlinked, prior?.Unlinked) && ReferenceEquals(overlay, prior?.Overlay), unlinked.Count); decoration.End(); }
        }
        else if (surface is not null) { new LocalLoadWork().Report(surface, reused: true, items: derived!.Geometry.Owners.Count); }
        using var linkedWork = observation != 0 ? OperationTiming.Start("local-map-linked-label-status-decoration", parent: observation) : null;
        var priorLinked = derived!.Linked;
        if (!ReferenceEquals(derived!.LabelSource, rooms) || !ReferenceEquals(derived.StatusSource, statuses)
            || !ReferenceEquals(prior?.Geometry.Owners, derived.Geometry.Owners))
        {
            var linkedChanged = !ReferenceEquals(prior?.Geometry.Owners, derived.Geometry.Owners)
                || derived.Geometry.Owners.Any(x => x.ActiveResolvedRoomId is { } id
                    && (!derived.Linked.TryGetValue(id, out var old) || old.Label != rooms[id].Header.Values.FriendlyName
                        || old.Status != Status(statuses[id])));
            var linked = !linkedChanged ? derived.Linked : derived.Geometry.Owners.Where(x => x.ActiveResolvedRoomId is not null).ToImmutableDictionary(x => x.ActiveResolvedRoomId!.Value,
                x => new AreaMapOwnerDecoration(x.ActiveResolvedRoomId, null, rooms[x.ActiveResolvedRoomId!.Value].Header.Values.FriendlyName,
                    Status(statuses[x.ActiveResolvedRoomId!.Value])));
            // The renderer consumes this admitted decoration, not newer common facts.
            // Reuse it only when both labels and canonical statuses remain equal.
            if (Same(linked, derived.Linked))
                linked = derived.Linked;
            derived = derived with { Linked = linked, LabelSource = rooms, StatusSource = statuses };
            entry.Maps = entry.Maps.SetItem(map.Id, derived);
        }
        if (linkedWork is not null) { new LocalLoadWork().Report(linkedWork, ReferenceEquals(derived.Linked, priorLinked), derived.Linked.Count); linkedWork.End(); }
        surface?.End();
        return new(derived.Geometry, new(derived.Linked, derived.Unlinked, derived.Overlay), context);
    }

    internal static AppliedRoomStatus Status(RoomStatus status) => status switch {
        RoomStatus.Neutral => AppliedRoomStatus.Neutral, RoomStatus.Success => AppliedRoomStatus.Success,
        RoomStatus.Warning => AppliedRoomStatus.Warning, RoomStatus.Danger => AppliedRoomStatus.Danger,
        _ => throw new JsonException("Invalid canonical map status.") };

    private static AreaMapOverlayView? Overlay(MapDataOverlay? overlay, MapSvgBounds bounds)
    {
        if (overlay?.Asset is not { } asset || !new MapOverlayPlacementService().TryProject(bounds.X, bounds.Y,
            bounds.X + bounds.Width, bounds.Y + bounds.Height, overlay.ScaleXPercent, overlay.ScaleYPercent,
            overlay.LeftOffsetPercent, overlay.BottomOffsetPercent, asset.AspectRatio, out var placement)) return null;
        return new(overlay.Id, overlay.MapId, asset.Url, asset.AspectRatio, overlay.ScaleXPercent, overlay.ScaleYPercent,
            overlay.LeftOffsetPercent, overlay.BottomOffsetPercent, placement);
    }

    private static MapDataSnapshot Freeze(MapDataSnapshot input)
    {
        if (input.Maps is null || input.Zones is null || input.Scenes is null || input.Chunks is null || input.FirstOverlays is null)
            throw new JsonException("Incomplete map hierarchy.");
        return new(input.Maps.ToImmutableArray(), input.Zones.ToImmutableArray(), input.Scenes.ToImmutableArray(),
            input.Chunks.ToImmutableArray(), input.FirstOverlays.ToImmutableArray());
    }
    private Hierarchy Index(MapDataSnapshot input)
    {
        static ImmutableDictionary<Guid, T> Ids<T>(IReadOnlyList<T> items, Func<T, Guid> id) where T : class
        {
            if (items.Any(x => x is null || id(x) == Guid.Empty) || items.Select(id).Distinct().Count() != items.Count)
                throw new JsonException("Invalid map identities.");
            return items.ToImmutableDictionary(id);
        }
        var maps = Ids(input.Maps, x => x.Id); var zones = Ids(input.Zones, x => x.Id);
        var scenes = Ids(input.Scenes, x => x.Id); var chunks = Ids(input.Chunks, x => x.Id);
        var overlays = Ids(input.FirstOverlays, x => x.Id);
        if (zones.Values.Any(x => !maps.ContainsKey(x.MapId) || x.InGameId is null)
            || scenes.Values.Any(x => !zones.ContainsKey(x.MapZoneId) || x.InGameId is null || x.ResolvedRoomId == Guid.Empty)
            || chunks.Values.Any(x => !scenes.ContainsKey(x.MapSceneId))
            || overlays.Values.Any(x => !maps.ContainsKey(x.MapId) || x.ImageAssetKey is null || x.Asset is { Url: null })
            || overlays.Values.Select(x => x.MapId).Distinct().Count() != overlays.Count)
            throw new JsonException("Invalid map membership.");
        foreach (var chunk in chunks.Values.Where(x => x.MinX.HasValue && x.MinY.HasValue && x.MaxX.HasValue && x.MaxY.HasValue))
            MapRenderProjection.Validate(new(chunk.Id, chunk.Id.ToString(), null, chunk.MinX!.Value, chunk.MinY!.Value, chunk.MaxX!.Value, chunk.MaxY!.Value, chunk.Z));
        var overlayMaps = overlays.Values.ToImmutableDictionary(x => x.MapId);
        if (retained?.Index is { } prior && Same(prior.Maps, maps) && Same(prior.Zones, zones)
            && Same(prior.Scenes, scenes) && Same(prior.Chunks, chunks) && Same(prior.Overlays, overlayMaps))
            return prior;
        var mapIds = maps.Keys.ToImmutableHashSet();
        var zoneMaps = zones.ToImmutableDictionary(x => x.Key, x => x.Value.MapId);
        var sceneZones = scenes.ToImmutableDictionary(x => x.Key, x => x.Value.MapZoneId);
        var chunkScenes = chunks.ToImmutableDictionary(x => x.Key, x => x.Value.MapSceneId);
        var previousMembership = retained?.Index.Membership;
        var membership = previousMembership is not null && previousMembership.MapIds.SetEquals(mapIds)
            && Same(previousMembership.ZoneMaps, zoneMaps) && Same(previousMembership.SceneZones, sceneZones)
            && Same(previousMembership.ChunkScenes, chunkScenes) ? previousMembership
            : new Membership(mapIds, zoneMaps, sceneZones, chunkScenes,
                chunks.Values.GroupBy(x => zones[scenes[x.MapSceneId].MapZoneId].MapId)
                    .ToImmutableDictionary(g => g.Key, g => g.Select(x => x.Id).ToImmutableArray()));
        return new(maps.Values.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToImmutableArray(), maps, zones, scenes, chunks,
            membership, overlayMaps);
    }
    public void Dispose() { IsDisposed = true; retained = null; }
}
