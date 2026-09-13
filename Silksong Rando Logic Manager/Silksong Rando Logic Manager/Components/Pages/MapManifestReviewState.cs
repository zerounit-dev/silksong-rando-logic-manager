using Microsoft.AspNetCore.Components.Web.Virtualization;
using Silksong_Rando_Logic_Manager.Services;

namespace Silksong_Rando_Logic_Manager.Components.Pages;

/// <summary>Transient, non-persisted presentation state for one manifest plan.</summary>
public sealed class MapManifestReviewState(MapReconciliationPlan plan)
{
    private FocusedZoneProjection? focusedZoneProjection;

    // Diagnostic/test visibility for the transient focused-review projection.
    public int FocusedZoneProjectionMaterializationCount { get; private set; }
    public IReadOnlyList<MapManifestPreviewShape> FocusedZoneShapes => focusedZoneProjection?.Shapes ?? [];
    public IReadOnlyList<MapUnitBounds> FocusedZoneViewBounds => focusedZoneProjection?.ViewBounds ?? [];

    public IReadOnlyList<MapManifestZoneSummary> Zones => plan.Rows.Where(x => x.ZoneInGameId is not null)
        .GroupBy(x => x.ZoneInGameId!, StringComparer.OrdinalIgnoreCase)
        .Select(rows => new MapManifestZoneSummary(rows.Key,
            plan.MatchedGroupNames.TryGetValue(rows.Key, out var names) ? string.Join(", ", names) : string.Empty,
            rows.Count(x => x.Kind == MapReconciliationKind.Added), rows.Count(x => x.Kind == MapReconciliationKind.Removed),
            rows.Count(x => x.Kind == MapReconciliationKind.Changed), rows.Count(x => x.Kind == MapReconciliationKind.Unchanged)))
        .OrderByDescending(x => x.IsActionable).ThenBy(x => x.ZoneInGameId, StringComparer.OrdinalIgnoreCase).ToList();

    public MapManifestReviewSection GetRows(string zoneInGameId, MapReconciliationKind? kind, string? search)
    {
        var allRows = plan.Rows.Where(x => string.Equals(x.ZoneInGameId, zoneInGameId, StringComparison.OrdinalIgnoreCase)).ToList();
        var zoneRow = allRows.FirstOrDefault(row => row.Entity == MapReconciliationEntity.Zone);
        var filteredRows = kind is { } filter ? allRows.Where(row => row.Kind == filter).ToList() : allRows;
        var term = search?.Trim();
        var rows = string.IsNullOrWhiteSpace(term) ? filteredRows : filteredRows.Where(row => Matches(row, term)).ToList();

        // The zone is the focused review's context, not an additional operation.
        // Pin the actual row when present without changing the remaining filter order.
        if (zoneRow is not null)
        {
            rows.Remove(zoneRow);
            rows.Insert(0, zoneRow);
        }

        return new(rows, allRows.Count);
    }

    public IEnumerable<MapManifestPreviewShape> SummaryShapes() => plan.Rows
        .Where(x => x.Entity == MapReconciliationEntity.Zone && x.ZoneInGameId is not null)
        .SelectMany(RowShapes);

    /// <summary>Non-selectable, cross-zone-derived map-frame projection for the summary.</summary>
    public IEnumerable<MapManifestPreviewShape> SummaryMapFrameShapes()
    {
        var current = CurrentMapBounds();
        var predicted = SelectedMapBounds();
        var mapRowId = plan.Rows.SingleOrDefault(x => x.Entity == MapReconciliationEntity.Map)?.Id ?? Guid.Empty;

        if (current?.IsAvailable == true)
            yield return new(mapRowId, current, "current", MapReconciliationKind.Unchanged, true, false, false);

        // A matching predicted union is already represented by the prior frame.
        if (predicted?.IsAvailable == true && predicted != current)
            yield return new(mapRowId, predicted, "proposal", MapReconciliationKind.Changed, false, false, false);
    }

    public IEnumerable<MapUnitBounds> SummaryViewBounds() => SummaryShapes().Select(x => x.Bounds).Concat(SummaryMapFrameShapes().Select(x => x.Bounds));

    public IEnumerable<MapManifestPreviewShape> ZoneShapes(string zoneInGameId)
    {
        var rows = plan.Rows.Where(x => string.Equals(x.ZoneInGameId, zoneInGameId, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var row in rows)
        {
            foreach (var shape in RowShapes(row)) yield return shape;
            if (row.Entity == MapReconciliationEntity.Scene)
                foreach (var chunk in rows.Where(x => x.Entity == MapReconciliationEntity.Chunk && string.Equals(x.SceneInGameId, row.SceneInGameId, StringComparison.OrdinalIgnoreCase)))
                    foreach (var shape in RowShapes(chunk)) yield return shape with { RowId = row.Id };
        }
    }

    /// <summary>Materializes one focused-zone projection for the current open review.</summary>
    public void FocusZone(string? zoneInGameId)
    {
        if (string.IsNullOrWhiteSpace(zoneInGameId))
        {
            focusedZoneProjection = null;
            return;
        }

        if (string.Equals(focusedZoneProjection?.ZoneInGameId, zoneInGameId, StringComparison.OrdinalIgnoreCase)) return;
        focusedZoneProjection = MaterializeFocusedZone(zoneInGameId);
    }

    public MapUnitBounds? SelectedMapBounds()
    {
        var bounds = plan.Rows.Where(x => x.Entity == MapReconciliationEntity.Chunk)
            .Select(x => x.Kind switch
            {
                MapReconciliationKind.Removed => x.Selected ? null : x.CurrentBounds,
                MapReconciliationKind.Added => x.Selected ? x.ProposedBounds : null,
                MapReconciliationKind.Changed => x.Selected ? x.ProposedBounds : x.CurrentBounds,
                _ => x.CurrentBounds
            }).Where(x => x?.IsAvailable == true).Cast<MapUnitBounds>().ToList();
        return bounds.Count == 0 ? null : new(bounds.Min(x => x.MinX), bounds.Min(x => x.MinY), bounds.Max(x => x.MaxX), bounds.Max(x => x.MaxY));
    }
    public MapUnitBounds? CurrentMapBounds() => plan.Rows.SingleOrDefault(x => x.Entity == MapReconciliationEntity.Map)?.CurrentBounds;

    public void SetRowSelected(MapReconciliationPlanRow row, bool selected)
    {
        var before = CaptureFocusedZoneSelection(row.ZoneInGameId);
        plan.SetRowSelected(row, selected);
        RebuildFocusedZoneIfSelectionChanged(row.ZoneInGameId, before);
    }

    private IReadOnlyDictionary<Guid, bool>? CaptureFocusedZoneSelection(string? zoneInGameId) =>
        focusedZoneProjection is not null && string.Equals(focusedZoneProjection.ZoneInGameId, zoneInGameId, StringComparison.OrdinalIgnoreCase)
            ? plan.Rows.Where(row => string.Equals(row.ZoneInGameId, zoneInGameId, StringComparison.OrdinalIgnoreCase)).ToDictionary(row => row.Id, row => row.Selected)
            : null;

    private void RebuildFocusedZoneIfSelectionChanged(string? zoneInGameId, IReadOnlyDictionary<Guid, bool>? before)
    {
        if (before is not null && focusedZoneProjection is not null &&
            plan.Rows.Any(row => string.Equals(row.ZoneInGameId, zoneInGameId, StringComparison.OrdinalIgnoreCase) && before[row.Id] != row.Selected))
            focusedZoneProjection = MaterializeFocusedZone(focusedZoneProjection.ZoneInGameId);
    }

    private FocusedZoneProjection MaterializeFocusedZone(string zoneInGameId)
    {
        var rows = plan.Rows.Where(row => string.Equals(row.ZoneInGameId, zoneInGameId, StringComparison.OrdinalIgnoreCase)).ToList();
        var chunksByScene = rows.Where(row => row.Entity == MapReconciliationEntity.Chunk && row.SceneInGameId is not null)
            .GroupBy(row => row.SceneInGameId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var shapes = new List<MapManifestPreviewShape>();
        foreach (var row in rows)
        {
            shapes.AddRange(RowShapes(row));
            if (row.Entity == MapReconciliationEntity.Scene && row.SceneInGameId is { } sceneInGameId && chunksByScene.TryGetValue(sceneInGameId, out var chunks))
                foreach (var chunk in chunks)
                    shapes.AddRange(RowShapes(chunk).Select(shape => shape with { RowId = row.Id }));
        }

        FocusedZoneProjectionMaterializationCount++;
        return new(zoneInGameId, shapes, shapes.Select(shape => shape.Bounds).ToList());
    }

    private static IEnumerable<MapManifestPreviewShape> RowShapes(MapReconciliationPlanRow row)
    {
        if (row.CurrentBounds?.IsAvailable == true) yield return new(row.Id, row.CurrentBounds, "current", row.Kind, true, row.Selected, false);
        if (row.Kind != MapReconciliationKind.Unchanged && row.ProposedBounds?.IsAvailable == true)
            yield return new(row.Id, row.ProposedBounds, "proposal", row.Kind, false, row.Selected, true);
    }
    private static bool Matches(MapReconciliationPlanRow row, string term) => $"{row.Entity} {row.ZoneInGameId} {row.SceneInGameId} {row.CacheIndex} {row.Label} {row.CurrentValue} {row.ProposedValue}".Contains(term, StringComparison.OrdinalIgnoreCase);

    private sealed record FocusedZoneProjection(string ZoneInGameId, IReadOnlyList<MapManifestPreviewShape> Shapes, IReadOnlyList<MapUnitBounds> ViewBounds);
}

public sealed record MapManifestZoneSummary(string ZoneInGameId, string MatchedGroups, int Added, int Removed, int Changed, int Unchanged)
{ public bool IsActionable => Added + Removed + Changed > 0; }
public sealed record MapManifestPreviewShape(Guid RowId, MapUnitBounds Bounds, string Layer, MapReconciliationKind Kind, bool IsCurrent, bool Selected, bool IsSelectable);
public sealed record MapManifestReviewSection(ICollection<MapReconciliationPlanRow> Rows, int Total)
{
    /// <summary>Returns only the requested filtered rows for the table virtualizer.</summary>
    public IReadOnlyList<MapReconciliationPlanRow> ViewportRows(int startIndex, int count)
    {
        var start = Math.Clamp(startIndex, 0, Rows.Count);
        var take = Math.Max(0, Math.Min(count, Rows.Count - start));
        return Rows.Skip(start).Take(take).ToList();
    }

    /// <summary>Provides a bounded filtered row range without serializing the entire section to the browser.</summary>
    public ValueTask<ItemsProviderResult<MapReconciliationPlanRow>> LoadViewportAsync(ItemsProviderRequest request) =>
        ValueTask.FromResult(new ItemsProviderResult<MapReconciliationPlanRow>(ViewportRows(request.StartIndex, request.Count), Rows.Count));
}
