namespace Silksong_Rando_Logic_Manager.Data;

public sealed class Subroom : ArchivableEntity
{
    public Guid RoomId { get; set; }

    public string ReferenceId { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public double? SceneUnitX { get; set; }

    public double? SceneUnitY { get; set; }

    public double? SceneUnitWidth { get; set; }

    public double? SceneUnitHeight { get; set; }

    public bool EnableAnnotation { get; set; } = true;

    public int SortOrder { get; set; }

    public Room Room { get; set; } = null!;
}
