namespace Silksong_Rando_Logic_Manager.Data;

public sealed class MapOverlay
{
    public Guid Id { get; set; }
    public Guid MapId { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string ImageAssetKey { get; set; } = string.Empty;
    public double ScaleXPercent { get; set; } = 100;
    public double ScaleYPercent { get; set; } = 100;
    public double LeftOffsetPercent { get; set; }
    public double BottomOffsetPercent { get; set; }
    public int SortOrder { get; set; }
    public Map? Map { get; set; }
}
