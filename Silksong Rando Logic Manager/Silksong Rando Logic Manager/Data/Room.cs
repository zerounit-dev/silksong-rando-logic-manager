namespace Silksong_Rando_Logic_Manager.Data;

public sealed class Room : ArchivableEntity
{
    public Guid? RoomGroupId { get; set; }

    public string ReferenceId { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string? InGameId { get; set; }

    public string? Contributors { get; set; }

    public string? Comments { get; set; }

    public double? SceneUnitWidth { get; set; }

    public double? SceneUnitHeight { get; set; }

    public double? SceneImageScaleXPercent { get; set; }

    public double? SceneImageScaleYPercent { get; set; }

    public double? SceneImagePanXPercent { get; set; }

    public double? SceneImagePanYPercent { get; set; }

    public bool IsSceneImageStale { get; set; }

    public int SortOrder { get; set; }

    public RoomGroup? RoomGroup { get; set; }

    public ICollection<Subroom> Subrooms { get; } = new List<Subroom>();

    public ICollection<RoomTransition> Transitions { get; } = new List<RoomTransition>();

    public ICollection<SubroomConnection> Connections { get; } = new List<SubroomConnection>();

    public ICollection<CheckLocation> CheckLocations { get; } = new List<CheckLocation>();
}
