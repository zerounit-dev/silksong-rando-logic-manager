namespace Silksong_Rando_Logic_Manager.Data;

public sealed class SubroomConnection : ArchivableEntity
{
    public Guid RoomId { get; set; }

    public string Alias { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string SourceSubroomReferenceText { get; set; } = string.Empty;

    public string DestinationSubroomReferenceText { get; set; } = string.Empty;

    public string Requirements { get; set; } = string.Empty;

    public bool? RequirementsParseSucceeded { get; set; }

    public string Notes { get; set; } = string.Empty;

    public bool EnableAnnotation { get; set; } = true;

    public double? SceneUnitX { get; set; }

    public double? SceneUnitY { get; set; }

    public Guid? ResolvedSourceSubroomId { get; set; }

    public Guid? ResolvedDestinationSubroomId { get; set; }

    public int SortOrder { get; set; }

    public bool IsTodo { get; set; }

    public bool? IsVerified { get; set; }

    public Room Room { get; set; } = null!;

    public Subroom? ResolvedSourceSubroom { get; set; }

    public Subroom? ResolvedDestinationSubroom { get; set; }
}
