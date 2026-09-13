namespace Silksong_Rando_Logic_Manager.Services;

public enum RequirementInputSyntax
{
    PredicateOnly,
    OptionalDifficultyThenPredicate,
    RequiredDifficultyThenPredicate,
    PredicateThenDirection,
    PredicateThenItem,
    PredicateThenCheck,
    PredicateThenQuantity
}

public sealed record RequirementInputSyntaxOption(RequirementInputSyntax Value, string PersistedValue, string DisplayLabel);

public sealed record RequirementCataloguePredicateValidation(
    Guid Id,
    string? Name,
    string? Category,
    RequirementInputSyntax InputSyntax,
    string? OutputSyntax,
    string? Notes,
    string? Aliases);

public sealed record RequirementCatalogueItemValidation(
    Guid Id,
    string? Name,
    string? Category,
    string? OutputValue,
    string? Notes,
    string? Aliases);

public sealed record RequirementCatalogueValidationIssue(
    string Field,
    Guid DefinitionId,
    string? DefinitionName,
    Guid? CollidingDefinitionId,
    string? CollidingDefinitionName,
    string Message);

/// <summary>Pure, non-persisted validation for the definition currently being edited.</summary>
public sealed record RequirementDefinitionDraftValidation(
    IReadOnlyList<RequirementCatalogueValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed record RequirementAtomFormatArguments(
    string? Difficulty = null,
    string? Direction = null,
    string? ItemOutputValue = null,
    int? Quantity = null,
    string? CheckGraphId = null);

public sealed record RequirementPredicateView(
    Guid Id,
    string Name,
    string? Category,
    string InputSyntax,
    string OutputSyntax,
    string Notes,
    int SortOrder,
    string Aliases,
    IReadOnlyList<string> ParsedAliases);

public sealed record RequirementItemView(
    Guid Id,
    string Name,
    string? Category,
    string OutputValue,
    string Notes,
    int SortOrder,
    string Aliases,
    IReadOnlyList<string> ParsedAliases);

public sealed record RequirementCatalogueManagerView(
    IReadOnlyList<RequirementPredicateView> Predicates,
    IReadOnlyList<RequirementItemView> Items);

public sealed record RequirementPredicateReferenceView(
    Guid Id,
    string Name,
    string? Category,
    string InputSyntax,
    string Notes,
    string Aliases,
    IReadOnlyList<string> ParsedAliases);

public sealed record RequirementItemReferenceView(
    Guid Id,
    string Name,
    string? Category,
    string Notes,
    string Aliases,
    IReadOnlyList<string> ParsedAliases);

public sealed record RequirementCatalogueReferenceView(
    IReadOnlyList<RequirementPredicateReferenceView> Predicates,
    IReadOnlyList<RequirementItemReferenceView> Items,
    IReadOnlyList<CheckLocationTypeDefinition> LocationTypes);

public sealed record RequirementCategorySuggestionsView(
    IReadOnlyList<string> PredicateCategories,
    IReadOnlyList<string> ItemCategories);

public sealed record ApplyRequirementPredicate(
    Guid? Id,
    string? Name,
    string? Category,
    string? InputSyntax,
    string? AliasDraft,
    string? OutputSyntax,
    string? Notes);

public sealed record ApplyRequirementItem(
    Guid? Id,
    string? Name,
    string? Category,
    string? AliasDraft,
    string? OutputValue,
    string? Notes);

public sealed record DeleteRequirementPredicate(Guid Id);

public sealed record DeleteRequirementItem(Guid Id);

public sealed record ReorderRequirementPredicate(Guid Id, int TargetIndex);

public sealed record ReorderRequirementItem(Guid Id, int TargetIndex);

public enum RequirementCatalogueCommandStatus
{
    Committed,
    ValidationRejected,
    Missing,
    Failed
}

public sealed record RequirementCatalogueCommandOutcome(
    RequirementCatalogueCommandStatus Status,
    IReadOnlyList<RequirementCatalogueValidationIssue> ValidationIssues,
    string? Message = null)
{
    public static RequirementCatalogueCommandOutcome Committed() => new(RequirementCatalogueCommandStatus.Committed, []);
    public static RequirementCatalogueCommandOutcome ValidationRejected(IReadOnlyList<RequirementCatalogueValidationIssue> issues) => new(RequirementCatalogueCommandStatus.ValidationRejected, issues);
    public static RequirementCatalogueCommandOutcome Missing(string message) => new(RequirementCatalogueCommandStatus.Missing, [], message);
    public static RequirementCatalogueCommandOutcome Failed(string message) => new(RequirementCatalogueCommandStatus.Failed, [], message);
}
