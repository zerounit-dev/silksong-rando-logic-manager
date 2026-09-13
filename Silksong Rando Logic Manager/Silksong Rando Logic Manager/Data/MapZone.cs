namespace Silksong_Rando_Logic_Manager.Data;

public sealed class MapZone
{
    public Guid Id { get; set; }
    public Guid MapId { get; set; }
    public string InGameId { get; set; } = string.Empty;
    public string? FriendlyName { get; set; }
    public double? MapUnitMinX { get; set; }
    public double? MapUnitMinY { get; set; }
    public double? MapUnitMaxX { get; set; }
    public double? MapUnitMaxY { get; set; }
    public Map? Map { get; set; }
    public ICollection<MapScene> Scenes { get; } = new List<MapScene>();
}
