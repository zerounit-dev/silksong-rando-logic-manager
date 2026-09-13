namespace Silksong_Rando_Logic_Manager.Data;

public sealed class RequirementItem
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string OutputValue { get; set; } = string.Empty;

    public string Aliases { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public int SortOrder { get; set; }

}
