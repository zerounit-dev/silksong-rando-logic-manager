using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Contracts;

public static class MapContextSelection
{
    public static MapSvgBounds? Bounds(double? minX, double? minY, double? maxX, double? maxY) =>
        minX is { } x && minY is { } y && maxX is { } right && maxY is { } top
        && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(right) && double.IsFinite(top)
        && right > x && top > y ? new(x, y, right - x, top - y) : null;

    public static (MapDataMap? Map, MapDataZone? Zone) Select(IReadOnlyList<MapDataMap> orderedMaps,
        IReadOnlyDictionary<Guid, MapDataMap> maps, IReadOnlyList<MapDataZone> linkedZones)
    {
        // Count before usability: one usable plus one unusable is still multiple.
        var zone = linkedZones.Count == 1 && ZoneBounds(linkedZones[0]) is not null ? linkedZones[0] : null;
        return (zone is null ? orderedMaps.FirstOrDefault() : maps.GetValueOrDefault(zone.MapId), zone);
    }
    public static MapSvgBounds? MapBounds(MapDataMap map) => Bounds(map.MinX, map.MinY, map.MaxX, map.MaxY);
    public static MapSvgBounds? ZoneBounds(MapDataZone zone) => Bounds(zone.MinX, zone.MinY, zone.MaxX, zone.MaxY);
    public static MapSvgBounds ProjectZone(MapSvgBounds zone, MapSvgBounds map) =>
        new(zone.X, map.Y + map.Y + map.Height - (zone.Y + zone.Height), zone.Width, zone.Height);
}
