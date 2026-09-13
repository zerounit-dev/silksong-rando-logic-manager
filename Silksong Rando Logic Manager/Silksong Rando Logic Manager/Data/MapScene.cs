namespace Silksong_Rando_Logic_Manager.Data;

public sealed class MapScene
{
    public Guid Id { get; set; }
    public Guid MapZoneId { get; set; }
    public string InGameId { get; set; } = string.Empty;
    public string? FriendlyName { get; set; }
    public string? RoomReferenceText { get; set; }
    public Guid? ResolvedRoomId { get; set; }
    public MapZone? MapZone { get; set; }
    public Room? ResolvedRoom { get; set; }
    public ICollection<MapChunk> Chunks { get; } = new List<MapChunk>();
}
