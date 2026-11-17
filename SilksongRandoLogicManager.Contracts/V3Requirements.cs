using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Contracts;

public sealed record RequirementManagerSnapshot(DateTime Token, RequirementCatalogueManagerView Catalogue);
public sealed record RequirementIdentity(Guid Id);
public sealed record RequirementOrder(Guid Id, int TargetIndex);
public sealed record RequirementImportText(string Json);
public sealed record RequirementOperationResult(string Status, IReadOnlyList<RequirementCatalogueValidationIssue> Issues, string? Message = null,
    RequirementRecheckCounts? Counts = null, SharedMutationSegment? Shared = null)
{
    public string? HistoryWarning { get; init; }
}
public sealed record RequirementRecheckCounts(int Successful, int Failed, int Unknown);
