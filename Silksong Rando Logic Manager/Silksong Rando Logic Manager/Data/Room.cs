namespace Silksong_Rando_Logic_Manager.Data;

public sealed class Room : ArchivableEntity
{
    public Guid? RoomGroupId { get; set; }

    public string ReferenceId { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string? InGameId { get; set; }

    public string? Contributors { get; set; }

    public string? Comments { get; set; }

    public double SceneMinX { get; set; }

    public double SceneMinY { get; set; }

    public double? SceneMaxX { get; set; }

    public double? SceneMaxY { get; set; }

    public double? SceneImagePixelsPerUnitX { get; set; }

    public double? SceneImagePixelsPerUnitY { get; set; }

    public double? SceneImagePixelOffsetX { get; set; }

    public double? SceneImagePixelOffsetY { get; set; }

    public bool UseSceneImage { get; set; } = true;
    public bool UseUploadedSceneImage { get; set; }
    public string? SceneImageOriginalHash { get; set; }
    public string? SceneImageCropHash { get; set; }

    public bool IsSceneImageStale { get; set; }

    public int SortOrder { get; set; }

    public RoomGroup? RoomGroup { get; set; }

    public ICollection<Subroom> Subrooms { get; } = new List<Subroom>();

    public ICollection<RoomTransition> Transitions { get; } = new List<RoomTransition>();

    public ICollection<SubroomConnection> Connections { get; } = new List<SubroomConnection>();

    public ICollection<CheckLocation> CheckLocations { get; } = new List<CheckLocation>();
}
