namespace Silksong_Rando_Logic_Manager.Data;

public sealed class MapChunk
{
    public Guid Id { get; set; }
    public Guid MapSceneId { get; set; }
    public int CacheIndex { get; set; }
    public string? InitialState { get; set; }
    public double? MapUnitMinX { get; set; }
    public double? MapUnitMinY { get; set; }
    public double? MapUnitMaxX { get; set; }
    public double? MapUnitMaxY { get; set; }
    public double? MapUnitZ { get; set; }
    public MapScene? MapScene { get; set; }
}
