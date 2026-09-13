namespace Silksong_Rando_Logic_Manager.Data;

public sealed class RequirementPredicate
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public string InputSyntax { get; set; } = string.Empty;

    public string OutputSyntax { get; set; } = string.Empty;

    public string Aliases { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public int SortOrder { get; set; }

}
