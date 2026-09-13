namespace Silksong_Rando_Logic_Manager.Data;

public sealed class Map
{
    public Guid Id { get; set; }
    public string InGameId { get; set; } = string.Empty;
    public string? FriendlyName { get; set; }
    public int SortOrder { get; set; }
    public double? MapUnitMinX { get; set; }
    public double? MapUnitMinY { get; set; }
    public double? MapUnitMaxX { get; set; }
    public double? MapUnitMaxY { get; set; }
    public ICollection<MapZone> Zones { get; } = new List<MapZone>();
    public ICollection<MapOverlay> Overlays { get; } = new List<MapOverlay>();
}
