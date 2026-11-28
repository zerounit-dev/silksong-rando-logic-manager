namespace Silksong_Rando_Logic_Manager.Data;

public sealed class RequirementExplanation : ArchivableEntity
{
    public Guid RoomId { get; set; }
    public string ReferenceId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public Room Room { get; set; } = null!;
}
