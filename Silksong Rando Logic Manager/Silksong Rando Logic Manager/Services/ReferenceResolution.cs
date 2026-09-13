namespace Silksong_Rando_Logic_Manager.Services;

public enum ReferenceResolutionStatus
{
    Resolved,
    Unresolved,
    Ambiguous,
    OutOfSync,
    TargetArchived
}

public sealed record ReferenceResolution(
    string EntityType,
    Guid EntityId,
    string FieldName,
    ReferenceResolutionStatus Status,
    Guid? ResolvedId);

public sealed record LogicResolutionReport(IReadOnlyList<ReferenceResolution> References)
{
    public bool HasIssues => References.Any(x => x.Status != ReferenceResolutionStatus.Resolved);
}
