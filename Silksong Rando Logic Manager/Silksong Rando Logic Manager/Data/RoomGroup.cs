namespace Silksong_Rando_Logic_Manager.Data;

public sealed class RoomGroup : AuditedEntity
{
    public string FriendlyName { get; set; } = string.Empty;

    public string? ZoneReferenceText { get; set; }

    public Guid? ResolvedMapZoneId { get; set; }

    public int SortOrder { get; set; }

    public MapZone? ResolvedMapZone { get; set; }

    public ICollection<Room> Rooms { get; } = new List<Room>();
}
