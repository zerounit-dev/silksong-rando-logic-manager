using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record MapUnitBounds(double? MinX, double? MinY, double? MaxX, double? MaxY)
{
    public static MapUnitBounds Unavailable { get; } = new(null, null, null, null);
    public bool IsAvailable => MinX is not null;
    /// <summary>Complete rectangle area; unavailable geometry has no area.</summary>
    public double Area => IsAvailable ? (MaxX!.Value - MinX!.Value) * (MaxY!.Value - MinY!.Value) : 0;
}

public sealed record ImportedMapZone(string InGameId, MapUnitBounds Bounds);
public sealed record ImportedMapChunk(string ZoneInGameId, string SceneInGameId, int CacheIndex, string? InitialState, MapUnitBounds Bounds, double? MapUnitZ);
public sealed record MapManifest(string MapInGameId, IReadOnlyList<ImportedMapZone> Zones, IReadOnlyList<ImportedMapChunk> Chunks, int ExcludedAnnotations);
public sealed record MapReconciliationResult(int New, int Missing, int MatchedDifferent, int MatchedUnchanged, int AutoLinked, int UnresolvedAutoLinkCandidates, int ExcludedAnnotations);
public enum MapReconciliationKind { Removed, Added, Changed, Unchanged }
public enum MapReconciliationEntity { Map, Zone, Scene, Chunk }
public sealed class MapReconciliationPlanRow
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required MapReconciliationKind Kind { get; init; }
    public required MapReconciliationEntity Entity { get; init; }
    public required string Label { get; init; }
    public string? Detail { get; init; }
    public string? CurrentValue { get; init; }
    public string? ProposedValue { get; init; }
    public Guid? ExistingId { get; init; }
    public string? ZoneInGameId { get; init; }
    public string? SceneInGameId { get; init; }
    public int? CacheIndex { get; init; }
    public ImportedMapZone? ImportedZone { get; init; }
    public ImportedMapChunk? ImportedChunk { get; init; }
    // Transient review projection only; imported geometry is never persisted here.
    public MapUnitBounds? CurrentBounds { get; set; }
    public MapUnitBounds? ProposedBounds { get; set; }
    public bool Selected { get; set; }
    public bool Locked { get; set; }
}
public sealed class MapReconciliationPlan
{
    private readonly HashSet<Guid> explicitlySelectedRemovalRows = [];
    private readonly HashSet<Guid> explicitlySelectedAdditionRows = [];

    public required MapManifest Manifest { get; init; }
    public Guid? ExistingMapId { get; init; }
    public required IReadOnlyList<MapReconciliationPlanRow> Rows { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MatchedGroupNames { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    // Compatibility-only: direct reconciliation historically created this developer-managed default.
    public bool CreateDefaultOverlay { get; set; }
    public IEnumerable<MapReconciliationPlanRow> RowsFor(MapReconciliationKind kind) => Rows.Where(x => x.Kind == kind);

    public void SetRowSelected(MapReconciliationPlanRow row, bool selected)
    {
        if (row.Locked) return;

        if (row.Kind == MapReconciliationKind.Added)
        {
            if (selected) explicitlySelectedAdditionRows.Add(row.Id);
            else explicitlySelectedAdditionRows.Remove(row.Id);
            ReconcileAdditionDependencies();
            return;
        }
        if (row.Kind != MapReconciliationKind.Removed)
        {
            row.Selected = selected;
            return;
        }

        if (selected) explicitlySelectedRemovalRows.Add(row.Id);
        else explicitlySelectedRemovalRows.Remove(row.Id);
        ReconcileRemovalDependencies();
    }

    internal void InitializeSelections()
    {
        foreach (var row in RowsFor(MapReconciliationKind.Added)) explicitlySelectedAdditionRows.Add(row.Id);
        foreach (var row in RowsFor(MapReconciliationKind.Changed)
                     .Where(row => (row.ProposedBounds?.Area ?? 0) > (row.CurrentBounds?.Area ?? 0)))
            row.Selected = true;
        ReconcileAdditionDependencies();
    }

    private void ReconcileAdditionDependencies()
    {
        var additions = RowsFor(MapReconciliationKind.Added).ToList();
        foreach (var row in additions) { row.Selected = explicitlySelectedAdditionRows.Contains(row.Id); row.Locked = false; }
        foreach (var row in additions.Where(x => IsStructuralAddition(x) && x.Selected && x.Entity is MapReconciliationEntity.Chunk or MapReconciliationEntity.Scene))
        {
            LockAddition(additions, MapReconciliationEntity.Scene, row.ZoneInGameId, row.SceneInGameId);
            LockAddition(additions, MapReconciliationEntity.Zone, row.ZoneInGameId);
            LockAddition(additions, MapReconciliationEntity.Map);
        }
    }
    private static void LockAddition(IEnumerable<MapReconciliationPlanRow> rows, MapReconciliationEntity entity, string? zone = null, string? scene = null)
    {
        foreach (var row in rows.Where(x => x.Entity == entity && (zone is null || x.ZoneInGameId == zone) && (scene is null || x.SceneInGameId == scene))) { row.Selected = true; row.Locked = true; }
    }

    private void ReconcileRemovalDependencies()
    {
        var removals = RowsFor(MapReconciliationKind.Removed).ToList();
        foreach (var row in removals)
        {
            row.Selected = explicitlySelectedRemovalRows.Contains(row.Id);
            row.Locked = false;
        }

        foreach (var row in removals.Where(row => IsStructuralRemoval(row) && explicitlySelectedRemovalRows.Contains(row.Id) && row.Entity == MapReconciliationEntity.Scene))
        {
            foreach (var child in removals.Where(child => child.Entity == MapReconciliationEntity.Chunk && child.ZoneInGameId == row.ZoneInGameId && child.SceneInGameId == row.SceneInGameId))
            {
                child.Selected = true;
                child.Locked = true;
            }
        }
        foreach (var row in removals.Where(row => IsStructuralRemoval(row) && explicitlySelectedRemovalRows.Contains(row.Id) && row.Entity == MapReconciliationEntity.Zone))
        {
            foreach (var child in removals.Where(child => child.Entity != MapReconciliationEntity.Zone && child.ZoneInGameId == row.ZoneInGameId))
            {
                child.Selected = true;
                child.Locked = true;
            }
        }
    }

    internal static bool IsStructuralAddition(MapReconciliationPlanRow row) => row.Kind == MapReconciliationKind.Added && row.ExistingId is null;
    internal static bool IsStructuralRemoval(MapReconciliationPlanRow row) => row.Kind == MapReconciliationKind.Removed &&
        (row.Entity == MapReconciliationEntity.Map || row.Entity == MapReconciliationEntity.Zone && row.ImportedZone is null || row.Entity == MapReconciliationEntity.Chunk && row.ImportedChunk is null || row.Entity == MapReconciliationEntity.Scene);
}

public sealed class MapManifestParser
{
    public async Task<MapManifest> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var mapInGameId = RequiredString(root, "gameMapRoot", "name");
        var zones = new Dictionary<string, ImportedMapZone>(StringComparer.OrdinalIgnoreCase);

        foreach (var zone in Array(root, "zones"))
        {
            if (zone.ValueKind != JsonValueKind.Object) continue;
            var id = RequiredString(zone, "mapZone");
            zones[id] = new ImportedMapZone(id, Bounds(zone, "visibleZoneUnionRootLocalBounds"));
        }

        var chunks = new List<ImportedMapChunk>();
        var excluded = 0;
        foreach (var scene in Array(root, "mapScenes"))
        {
            if (scene.ValueKind != JsonValueKind.Object ||
                !scene.TryGetProperty("mapZoneResolved", out var resolved) || resolved.ValueKind != JsonValueKind.True ||
                !TryString(scene, "mapZone", out var zoneId) || !zones.ContainsKey(zoneId))
            {
                excluded++;
                continue;
            }

            var bounds = Bounds(scene, "mapBoundsRootLocal");
            var initialState = TryString(scene, "initialState", out var state) ? state : null;
            var z = NumberOrNull(scene, "mapBoundsRootLocal", "center", "z");
            var references = Array(scene, "cacheReferences").ToList();
            if (references.Count == 0)
            {
                excluded++;
                continue;
            }

            foreach (var reference in references)
            {
                if (!TryString(reference, "cacheKey", out var sceneId) || !reference.TryGetProperty("cacheIndex", out var indexElement) || !indexElement.TryGetInt32(out var index))
                {
                    excluded++;
                    continue;
                }

                chunks.Add(new ImportedMapChunk(zoneId, sceneId, index, initialState, bounds, z));
            }
        }

        if (chunks.GroupBy(x => (x.ZoneInGameId, x.SceneInGameId, x.CacheIndex), new ImportedChunkIdentityComparer()).Any(x => x.Count() > 1))
        {
            throw new InvalidOperationException("The map manifest contains duplicate zone, cache key, and cache index identities.");
        }

        return new MapManifest(mapInGameId, zones.Values.ToList(), chunks, excluded);
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string RequiredString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"The map manifest is missing {string.Join('.', path)}.");
            if (!current.TryGetProperty(segment, out current)) throw new InvalidOperationException($"The map manifest is missing {string.Join('.', path)}.");
        }

        if (TryString(current, out var value)) return value;
        throw new InvalidOperationException($"The map manifest has an invalid {string.Join('.', path)}.");
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var propertyValue) && TryString(propertyValue, out value);
    }

    private static bool TryString(JsonElement element, out string value)
    {
        value = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() ?? string.Empty : string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static MapUnitBounds Bounds(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return MapUnitBounds.Unavailable;
        if (!element.TryGetProperty(property, out var bounds) || bounds.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return MapUnitBounds.Unavailable;
        var minX = NumberOrNull(bounds, "min", "x");
        var minY = NumberOrNull(bounds, "min", "y");
        var maxX = NumberOrNull(bounds, "max", "x");
        var maxY = NumberOrNull(bounds, "max", "y");
        if (minX is null && minY is null && maxX is null && maxY is null) return MapUnitBounds.Unavailable;
        if (minX is null || minY is null || maxX is null || maxY is null || minX >= maxX || minY >= maxY)
        {
            throw new InvalidOperationException($"The map manifest has an incomplete or unordered {property} rectangle.");
        }

        return new(minX, minY, maxX, maxY);
    }

    private static double? NumberOrNull(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(segment, out current)) return null;
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
    }

    private sealed class ImportedChunkIdentityComparer : IEqualityComparer<(string Zone, string Scene, int Index)>
    {
        public bool Equals((string Zone, string Scene, int Index) x, (string Zone, string Scene, int Index) y) =>
            x.Index == y.Index && string.Equals(x.Zone, y.Zone, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Scene, y.Scene, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Zone, string Scene, int Index) value) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Zone), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Scene), value.Index);
    }
}

public sealed class MapManifestService(IDbContextFactory<LogicDbContext> dbContextFactory)
{
    public async Task<MapReconciliationResult> ReconcileAsync(MapManifest manifest, CancellationToken cancellationToken = default)
    {
        var plan = await BuildPlanAsync(manifest, cancellationToken);
        // Direct reconciliation predates review selection and remains an explicit all-operations API.
        foreach (var row in plan.Rows.Where(row => row.Kind is not MapReconciliationKind.Unchanged).ToList()) plan.SetRowSelected(row, true);
        plan.CreateDefaultOverlay = plan.ExistingMapId is null;
        return await ApplyPlanAsync(plan, cancellationToken);
    }

    public async Task<MapReconciliationPlan> BuildPlanAsync(MapManifest manifest, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var map = await db.Maps.SingleOrDefaultAsync(x => x.InGameId == manifest.MapInGameId, cancellationToken);
        var rows = new List<MapReconciliationPlanRow>();
        if (map is null)
        {
            rows.Add(Row(MapReconciliationKind.Added, MapReconciliationEntity.Map, manifest.MapInGameId, "new imported map", selected: true));
        }

        var existingZones = map is null ? [] : await db.MapZones.Where(x => x.MapId == map.Id).Include(x => x.Scenes).ThenInclude(x => x.Chunks).ToListAsync(cancellationToken);
        var importedZones = manifest.Zones.ToDictionary(x => x.InGameId, StringComparer.OrdinalIgnoreCase);
        foreach (var zone in existingZones.Where(x => !importedZones.ContainsKey(x.InGameId)))
        {
            foreach (var scene in zone.Scenes)
            {
                foreach (var chunk in scene.Chunks) rows.Add(Row(MapReconciliationKind.Removed, MapReconciliationEntity.Chunk, $"{zone.InGameId} | {scene.InGameId}-{chunk.CacheIndex}", "missing from manifest", chunk.Id, zone.InGameId, scene.InGameId, chunk.CacheIndex));
                rows.Add(Row(MapReconciliationKind.Removed, MapReconciliationEntity.Scene, $"{zone.InGameId} | {scene.InGameId}", "missing from manifest", scene.Id, zone.InGameId, scene.InGameId));
            }
            rows.Add(Row(MapReconciliationKind.Removed, MapReconciliationEntity.Zone, zone.InGameId, "missing from manifest", zone.Id, zone.InGameId));
        }

        foreach (var importedZone in manifest.Zones)
        {
            var zone = existingZones.SingleOrDefault(x => string.Equals(x.InGameId, importedZone.InGameId, StringComparison.OrdinalIgnoreCase));
            if (zone is null)
            {
                rows.Add(Row(MapReconciliationKind.Added, MapReconciliationEntity.Zone, importedZone.InGameId, BoundsDetail(importedZone.Bounds), zone: importedZone.InGameId, importedZone: importedZone));
            }
            else rows.Add(Row(BoundsKind(new MapUnitBounds(zone.MapUnitMinX, zone.MapUnitMinY, zone.MapUnitMaxX, zone.MapUnitMaxY), importedZone.Bounds), MapReconciliationEntity.Zone, importedZone.InGameId, BoundsDetail(importedZone.Bounds), zone.Id, importedZone.InGameId, importedZone: importedZone, currentValue: BoundsDetail(zone), proposedValue: BoundsDetail(importedZone.Bounds)));

            var importedChunks = manifest.Chunks.Where(x => string.Equals(x.ZoneInGameId, importedZone.InGameId, StringComparison.OrdinalIgnoreCase)).ToList();
            var importedScenes = importedChunks.GroupBy(x => x.SceneInGameId, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var scene in zone?.Scenes.Where(x => !importedScenes.Any(y => string.Equals(y.Key, x.InGameId, StringComparison.OrdinalIgnoreCase))) ?? [])
            {
                foreach (var chunk in scene.Chunks) rows.Add(Row(MapReconciliationKind.Removed, MapReconciliationEntity.Chunk, $"{importedZone.InGameId} | {scene.InGameId}-{chunk.CacheIndex}", "missing from manifest", chunk.Id, importedZone.InGameId, scene.InGameId, chunk.CacheIndex));
                rows.Add(Row(MapReconciliationKind.Removed, MapReconciliationEntity.Scene, $"{importedZone.InGameId} | {scene.InGameId}", "missing from manifest", scene.Id, importedZone.InGameId, scene.InGameId));
            }

            foreach (var importedScene in importedScenes)
            {
                var scene = zone?.Scenes.SingleOrDefault(x => string.Equals(x.InGameId, importedScene.Key, StringComparison.OrdinalIgnoreCase));
                if (scene is null)
                {
                    rows.Add(Row(MapReconciliationKind.Added, MapReconciliationEntity.Scene, $"{importedZone.InGameId} | {importedScene.Key}", "new cache key", zone: importedZone.InGameId, scene: importedScene.Key));
                }
                    else rows.Add(Row(MapReconciliationKind.Unchanged, MapReconciliationEntity.Scene, $"{importedZone.InGameId} | {importedScene.Key}", "matched scene; authored link retained", scene.Id, importedZone.InGameId, importedScene.Key, currentValue: "authored link retained", proposedValue: "retained"));

                var indexes = importedScene.Select(x => x.CacheIndex).ToHashSet();
                foreach (var chunk in scene?.Chunks.Where(x => !indexes.Contains(x.CacheIndex)) ?? [])
                {
                    rows.Add(Row(MapReconciliationKind.Removed, MapReconciliationEntity.Chunk, $"{importedZone.InGameId} | {importedScene.Key}-{chunk.CacheIndex}", "missing from manifest", chunk.Id, importedZone.InGameId, importedScene.Key, chunk.CacheIndex));
                }

                foreach (var importedChunk in importedScene)
                {
                    var chunk = scene?.Chunks.SingleOrDefault(x => x.CacheIndex == importedChunk.CacheIndex);
                    if (chunk is null)
                    {
                        rows.Add(Row(MapReconciliationKind.Added, MapReconciliationEntity.Chunk, $"{importedZone.InGameId} | {importedScene.Key}-{importedChunk.CacheIndex}", ChunkDetail(importedChunk), zone: importedZone.InGameId, scene: importedScene.Key, cacheIndex: importedChunk.CacheIndex, importedChunk: importedChunk));
                    }
                    else rows.Add(Row(ChunkKind(chunk, importedChunk), MapReconciliationEntity.Chunk, $"{importedZone.InGameId} | {importedScene.Key}-{importedChunk.CacheIndex}", ChunkDetail(importedChunk), chunk.Id, importedZone.InGameId, importedScene.Key, importedChunk: importedChunk, cacheIndex: importedChunk.CacheIndex, currentValue: ChunkDetail(chunk), proposedValue: ChunkDetail(importedChunk)));
                }
            }
        }

        if (map is not null) { var bounds = Union(manifest.Chunks.Select(x => x.Bounds)); rows.Add(Row(BoundsKind(new MapUnitBounds(map.MapUnitMinX, map.MapUnitMinY, map.MapUnitMaxX, map.MapUnitMaxY), bounds), MapReconciliationEntity.Map, manifest.MapInGameId, "derived chunk-frame union", map.Id, currentValue: BoundsDetail(map), proposedValue: BoundsDetail(bounds))); }
        var existingChunks = existingZones.SelectMany(x => x.Scenes).SelectMany(x => x.Chunks).ToDictionary(x => x.Id);
        foreach (var row in rows)
        {
            if (row.Entity == MapReconciliationEntity.Map)
            {
                row.CurrentBounds = map is null ? null : new(map.MapUnitMinX, map.MapUnitMinY, map.MapUnitMaxX, map.MapUnitMaxY);
                row.ProposedBounds = Union(manifest.Chunks.Select(x => x.Bounds));
            }
            else if (row.Entity == MapReconciliationEntity.Zone)
            {
                var current = existingZones.SingleOrDefault(x => x.Id == row.ExistingId);
                row.CurrentBounds = current is null ? null : new(current.MapUnitMinX, current.MapUnitMinY, current.MapUnitMaxX, current.MapUnitMaxY);
                row.ProposedBounds = row.ImportedZone?.Bounds;
            }
            else if (row.Entity == MapReconciliationEntity.Chunk)
            {
                row.CurrentBounds = row.ExistingId is { } id && existingChunks.TryGetValue(id, out var current) ? new(current.MapUnitMinX, current.MapUnitMinY, current.MapUnitMaxX, current.MapUnitMaxY) : null;
                row.ProposedBounds = row.ImportedChunk?.Bounds;
            }
        }
        var groupNames = map is null ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) :
            (await db.RoomGroups.Where(x => x.ResolvedMapZoneId != null).ToListAsync(cancellationToken))
                .Join(existingZones, x => x.ResolvedMapZoneId, x => (Guid?)x.Id, (group, zone) => new { zone.InGameId, group.FriendlyName })
                .GroupBy(x => x.InGameId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Select(y => y.FriendlyName).OrderBy(y => y, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        var plan = new MapReconciliationPlan { Manifest = manifest, ExistingMapId = map?.Id, MatchedGroupNames = groupNames, Rows = rows.OrderBy(x => x.Kind).ThenBy(x => x.Entity).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList() };
        plan.InitializeSelections();
        return plan;
    }

    public async Task<MapReconciliationResult> ApplyPlanAsync(MapReconciliationPlan plan, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = new Counter();
        var selected = plan.Rows.Where(x => x.Selected && x.Kind != MapReconciliationKind.Unchanged).ToList();
        var map = plan.ExistingMapId is { } mapId ? await db.Maps.SingleOrDefaultAsync(x => x.Id == mapId, cancellationToken) : null;
        if (map is null && selected.Any(x => x.Kind == MapReconciliationKind.Added && x.Entity == MapReconciliationEntity.Map))
        {
            map = new Map { InGameId = plan.Manifest.MapInGameId, SortOrder = await db.Maps.CountAsync(cancellationToken) };
            db.Maps.Add(map);
            if (plan.CreateDefaultOverlay) db.MapOverlays.Add(new MapOverlay { MapId = map.Id, FriendlyName = "Area map", ImageAssetKey = "area-map-hd", SortOrder = 0 });
            result.New++;
        }
        if (map is null) throw new InvalidOperationException("The imported map no longer exists. Build a new reconciliation plan.");

        var zones = await db.MapZones.Where(x => x.MapId == map.Id).Include(x => x.Scenes).ThenInclude(x => x.Chunks).ToListAsync(cancellationToken);
        var autoLinkCandidates = new List<MapScene>();

        // Chunks must disappear before their selected parent scenes and zones can become empty.
        foreach (var row in selected.Where(x => MapReconciliationPlan.IsStructuralRemoval(x) && x.Entity == MapReconciliationEntity.Chunk))
        {
            var chunk = zones.SelectMany(x => x.Scenes).SelectMany(x => x.Chunks).SingleOrDefault(x => x.Id == row.ExistingId);
            if (chunk is not null) { db.MapChunks.Remove(chunk); result.Missing++; }
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var row in selected.Where(x => MapReconciliationPlan.IsStructuralRemoval(x) && x.Entity == MapReconciliationEntity.Scene))
        {
            var scene = zones.SelectMany(x => x.Scenes).SingleOrDefault(x => x.Id == row.ExistingId);
            if (scene is not null && !await db.MapChunks.AnyAsync(x => x.MapSceneId == scene.Id, cancellationToken)) { db.MapScenes.Remove(scene); result.Missing++; }
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var row in selected.Where(x => MapReconciliationPlan.IsStructuralRemoval(x) && x.Entity == MapReconciliationEntity.Zone))
        {
            var zone = zones.SingleOrDefault(x => x.Id == row.ExistingId);
            if (zone is not null && !await db.MapScenes.AnyAsync(x => x.MapZoneId == zone.Id, cancellationToken)) { db.MapZones.Remove(zone); result.Missing++; }
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var row in selected.Where(x => MapReconciliationPlan.IsStructuralAddition(x) && x.Entity == MapReconciliationEntity.Zone))
        {
            if (await db.MapZones.AnyAsync(x => x.MapId == map.Id && x.InGameId == row.ZoneInGameId, cancellationToken)) continue;
            var zone = new MapZone { MapId = map.Id, InGameId = row.ZoneInGameId! };
            SetBounds(zone, row.ImportedZone!.Bounds); db.MapZones.Add(zone); result.New++;
        }
        await db.SaveChangesAsync(cancellationToken);
        zones = await db.MapZones.Where(x => x.MapId == map.Id).Include(x => x.Scenes).ThenInclude(x => x.Chunks).ToListAsync(cancellationToken);
        foreach (var row in selected.Where(x => MapReconciliationPlan.IsStructuralAddition(x) && x.Entity == MapReconciliationEntity.Scene))
        {
            var zone = zones.Single(x => string.Equals(x.InGameId, row.ZoneInGameId, StringComparison.OrdinalIgnoreCase));
            if (zone.Scenes.Any(x => string.Equals(x.InGameId, row.SceneInGameId, StringComparison.OrdinalIgnoreCase))) continue;
            var scene = new MapScene { MapZoneId = zone.Id, InGameId = row.SceneInGameId! };
            db.MapScenes.Add(scene); autoLinkCandidates.Add(scene); result.New++;
        }
        await db.SaveChangesAsync(cancellationToken);
        zones = await db.MapZones.Where(x => x.MapId == map.Id).Include(x => x.Scenes).ThenInclude(x => x.Chunks).ToListAsync(cancellationToken);
        foreach (var row in selected.Where(x => MapReconciliationPlan.IsStructuralAddition(x) && x.Entity == MapReconciliationEntity.Chunk))
        {
            var scene = zones.Single(x => string.Equals(x.InGameId, row.ZoneInGameId, StringComparison.OrdinalIgnoreCase)).Scenes.Single(x => string.Equals(x.InGameId, row.SceneInGameId, StringComparison.OrdinalIgnoreCase));
            if (scene.Chunks.Any(x => x.CacheIndex == row.CacheIndex)) continue;
            var chunk = new MapChunk { MapSceneId = scene.Id, CacheIndex = row.CacheIndex!.Value }; Apply(chunk, row.ImportedChunk!); db.MapChunks.Add(chunk); result.New++;
        }
        foreach (var row in selected.Where(x => IsMatchedGeometryOperation(x) && x.Entity == MapReconciliationEntity.Zone))
        {
            var zone = zones.SingleOrDefault(x => x.Id == row.ExistingId); if (zone is not null && SetBounds(zone, row.ImportedZone!.Bounds)) result.MatchedDifferent++;
        }
        foreach (var row in selected.Where(x => IsMatchedGeometryOperation(x) && x.Entity == MapReconciliationEntity.Chunk))
        {
            var chunk = zones.SelectMany(x => x.Scenes).SelectMany(x => x.Chunks).SingleOrDefault(x => x.Id == row.ExistingId); if (chunk is not null && Apply(chunk, row.ImportedChunk!)) result.MatchedDifferent++;
        }
        // The derived map union must see selected tracked geometry clears as well as additions.
        await db.SaveChangesAsync(cancellationToken);
        if (selected.Any(x => x.Entity == MapReconciliationEntity.Map || x.Entity == MapReconciliationEntity.Chunk || x.Entity == MapReconciliationEntity.Zone))
        {
            var bounds = Union((await db.MapChunks.Where(x => x.MapScene!.MapZone!.MapId == map.Id).Select(x => new MapUnitBounds(x.MapUnitMinX, x.MapUnitMinY, x.MapUnitMaxX, x.MapUnitMaxY)).ToListAsync(cancellationToken)));
            SetBounds(map, bounds);
        }
        result.MatchedUnchanged = plan.Rows.Count(x => x.Kind == MapReconciliationKind.Unchanged);
        await db.SaveChangesAsync(cancellationToken);
        await AutoLinkBlankMapScenesAsync(db, autoLinkCandidates, result, cancellationToken);
        await new LogicReferenceResolver(db).ResolveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(result.New, result.Missing, result.MatchedDifferent, result.MatchedUnchanged, result.AutoLinked, result.UnresolvedAutoLinkCandidates, plan.Manifest.ExcludedAnnotations);
    }

    private static MapReconciliationPlanRow Row(MapReconciliationKind kind, MapReconciliationEntity entity, string label, string? detail = null, Guid? existingId = null, string? zone = null, string? scene = null, int? cacheIndex = null, ImportedMapZone? importedZone = null, ImportedMapChunk? importedChunk = null, bool selected = false, string? currentValue = null, string? proposedValue = null) => new()
    { Kind = kind, Entity = entity, Label = label, Detail = detail, CurrentValue = currentValue ?? (kind is MapReconciliationKind.Removed or MapReconciliationKind.Unchanged ? detail : null), ProposedValue = proposedValue ?? (kind is MapReconciliationKind.Added ? detail : null), ExistingId = existingId, ZoneInGameId = zone, SceneInGameId = scene, CacheIndex = cacheIndex, ImportedZone = importedZone, ImportedChunk = importedChunk, Selected = selected || kind == MapReconciliationKind.Added };
    private static string BoundsDetail(MapUnitBounds bounds) => bounds.IsAvailable ? $"bounds: {bounds.MinX}, {bounds.MinY} to {bounds.MaxX}, {bounds.MaxY}" : "bounds unavailable";
    private static string BoundsDetail(Map map) => BoundsDetail(new MapUnitBounds(map.MapUnitMinX, map.MapUnitMinY, map.MapUnitMaxX, map.MapUnitMaxY));
    private static string BoundsDetail(MapZone zone) => BoundsDetail(new MapUnitBounds(zone.MapUnitMinX, zone.MapUnitMinY, zone.MapUnitMaxX, zone.MapUnitMaxY));
    private static string ChunkDetail(ImportedMapChunk chunk) => $"{chunk.InitialState ?? "no state"}; {BoundsDetail(chunk.Bounds)}; Z: {chunk.MapUnitZ?.ToString() ?? "unavailable"}";
    private static string ChunkDetail(MapChunk chunk) => $"{chunk.InitialState ?? "no state"}; {BoundsDetail(new MapUnitBounds(chunk.MapUnitMinX, chunk.MapUnitMinY, chunk.MapUnitMaxX, chunk.MapUnitMaxY))}; Z: {chunk.MapUnitZ?.ToString() ?? "unavailable"}";
    private static MapReconciliationKind BoundsKind(MapUnitBounds current, MapUnitBounds proposed) =>
        !current.IsAvailable && proposed.IsAvailable ? MapReconciliationKind.Added :
        current.IsAvailable && !proposed.IsAvailable ? MapReconciliationKind.Removed :
        BoundsEqual(current, proposed) ? MapReconciliationKind.Unchanged : MapReconciliationKind.Changed;
    private static bool BoundsEqual(MapUnitBounds current, MapUnitBounds proposed) =>
        current.IsAvailable == proposed.IsAvailable && (!current.IsAvailable ||
            EqualWithinTolerance(current.MinX, proposed.MinX) && EqualWithinTolerance(current.MinY, proposed.MinY) &&
            EqualWithinTolerance(current.MaxX, proposed.MaxX) && EqualWithinTolerance(current.MaxY, proposed.MaxY));
    private static MapReconciliationKind ChunkKind(MapChunk entity, ImportedMapChunk value)
    {
        var boundsKind = BoundsKind(new(entity.MapUnitMinX, entity.MapUnitMinY, entity.MapUnitMaxX, entity.MapUnitMaxY), value.Bounds);
        if (boundsKind is MapReconciliationKind.Added or MapReconciliationKind.Removed) return boundsKind;
        return boundsKind == MapReconciliationKind.Unchanged && entity.InitialState == value.InitialState && EqualWithinTolerance(entity.MapUnitZ, value.MapUnitZ)
            ? MapReconciliationKind.Unchanged : MapReconciliationKind.Changed;
    }
    private static bool EqualWithinTolerance(double? current, double? proposed) => current is null || proposed is null ? current == proposed : Math.Abs(current.Value - proposed.Value) <= .01;
    private static bool IsMatchedGeometryOperation(MapReconciliationPlanRow row) => row.ExistingId is not null && row.Entity is MapReconciliationEntity.Map or MapReconciliationEntity.Zone or MapReconciliationEntity.Chunk &&
        row.Kind is MapReconciliationKind.Added or MapReconciliationKind.Removed or MapReconciliationKind.Changed &&
        (row.Entity == MapReconciliationEntity.Map || row.Entity == MapReconciliationEntity.Zone && row.ImportedZone is not null || row.Entity == MapReconciliationEntity.Chunk && row.ImportedChunk is not null);
    private static void Lock(IEnumerable<MapReconciliationPlanRow> rows, MapReconciliationKind kind, MapReconciliationEntity entity, string? zone = null, string? scene = null)
    {
        foreach (var row in rows.Where(x => x.Kind == kind && x.Entity == entity && (zone is null || x.ZoneInGameId == zone) && (scene is null || x.SceneInGameId == scene))) { row.Selected = true; row.Locked = true; }
    }

    internal static async Task AutoLinkBlankMapScenesAsync(LogicDbContext db, IEnumerable<MapScene> scenes, Counter result, CancellationToken cancellationToken)
    {
        var rooms = await db.Rooms.Where(x => !x.IsArchived && x.InGameId != null && x.InGameId != "").ToListAsync(cancellationToken);
        foreach (var scene in scenes.Where(x => string.IsNullOrWhiteSpace(x.RoomReferenceText)))
        {
            var matches = rooms.Where(room => string.Equals(room.InGameId!.Trim(), scene.InGameId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                scene.RoomReferenceText = matches[0].ReferenceId;
                result.AutoLinked++;
            }
            else
            {
                result.UnresolvedAutoLinkCandidates++;
            }
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
    }

    private static bool SetBounds(Map entity, MapUnitBounds value) => SetBoundsCore(entity.MapUnitMinX, entity.MapUnitMinY, entity.MapUnitMaxX, entity.MapUnitMaxY, value, (minX, minY, maxX, maxY) => { entity.MapUnitMinX = minX; entity.MapUnitMinY = minY; entity.MapUnitMaxX = maxX; entity.MapUnitMaxY = maxY; });
    private static bool SetBounds(MapZone entity, MapUnitBounds value) => SetBoundsCore(entity.MapUnitMinX, entity.MapUnitMinY, entity.MapUnitMaxX, entity.MapUnitMaxY, value, (minX, minY, maxX, maxY) => { entity.MapUnitMinX = minX; entity.MapUnitMinY = minY; entity.MapUnitMaxX = maxX; entity.MapUnitMaxY = maxY; });
    private static bool SetBoundsCore(double? minX, double? minY, double? maxX, double? maxY, MapUnitBounds value, Action<double?, double?, double?, double?> set)
    {
        if (minX == value.MinX && minY == value.MinY && maxX == value.MaxX && maxY == value.MaxY) return false;
        set(value.MinX, value.MinY, value.MaxX, value.MaxY);
        return true;
    }

    private static bool Apply(MapChunk chunk, ImportedMapChunk value)
    {
        var changed = chunk.InitialState != value.InitialState || chunk.MapUnitMinX != value.Bounds.MinX || chunk.MapUnitMinY != value.Bounds.MinY || chunk.MapUnitMaxX != value.Bounds.MaxX || chunk.MapUnitMaxY != value.Bounds.MaxY || chunk.MapUnitZ != value.MapUnitZ;
        chunk.InitialState = value.InitialState; chunk.MapUnitMinX = value.Bounds.MinX; chunk.MapUnitMinY = value.Bounds.MinY; chunk.MapUnitMaxX = value.Bounds.MaxX; chunk.MapUnitMaxY = value.Bounds.MaxY; chunk.MapUnitZ = value.MapUnitZ;
        return changed;
    }

    private static MapUnitBounds Union(IEnumerable<MapUnitBounds> bounds)
    {
        var complete = bounds.Where(x => x.IsAvailable).ToList();
        return complete.Count == 0 ? MapUnitBounds.Unavailable : new(complete.Min(x => x.MinX), complete.Min(x => x.MinY), complete.Max(x => x.MaxX), complete.Max(x => x.MaxY));
    }

    internal sealed class Counter { public int New; public int Missing; public int MatchedDifferent; public int MatchedUnchanged; public int AutoLinked; public int UnresolvedAutoLinkCandidates; }
}
