namespace Silksong_Rando_Logic_Manager.Data;

public sealed class RoomTransition : ArchivableEntity
{
    public Guid RoomId { get; set; }

    public string Alias { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string? InGameId { get; set; }

    public double? InGamePositionX { get; set; }

    public double? InGamePositionY { get; set; }

    public double? InGamePositionZ { get; set; }

    public double? AnnotationSceneUnitX { get; set; }

    public double? AnnotationSceneUnitY { get; set; }

    public bool EnableAnnotation { get; set; } = true;

    public double? LocalPositionX { get; set; }

    public double? LocalPositionY { get; set; }

    public double? LocalPositionZ { get; set; }

    public string? SourceSubroomReferenceText { get; set; }

    public string? DestinationRoomReferenceText { get; set; }

    public string? DestinationTransitionAliasText { get; set; }

    public string Requirements { get; set; } = string.Empty;

    public bool? RequirementsParseSucceeded { get; set; }

    public string Notes { get; set; } = string.Empty;

    public Guid? ResolvedSourceSubroomId { get; set; }

    public Guid? ResolvedDestinationRoomId { get; set; }

    public Guid? ResolvedDestinationTransitionId { get; set; }

    public int SortOrder { get; set; }

    public bool IsTodo { get; set; }

    public bool? IsVerified { get; set; }

    public Room Room { get; set; } = null!;

    public Subroom? ResolvedSourceSubroom { get; set; }

    public Room? ResolvedDestinationRoom { get; set; }

    public RoomTransition? ResolvedDestinationTransition { get; set; }
}
