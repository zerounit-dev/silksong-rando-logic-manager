namespace Silksong_Rando_Logic_Manager.Services;

public sealed record RequirementPredicateReferenceView(
    Guid Id, string Name, string? Category, string InputSyntax, string Notes,
    string Aliases, IReadOnlyList<string> ParsedAliases);
public sealed record RequirementCrestReferenceView(
    Guid Id, string Name, string? Category, string Notes, string Aliases,
    IReadOnlyList<string> ParsedAliases);
public sealed record RequirementItemReferenceView(
    Guid Id, string Name, string? Category, string Notes, string Aliases,
    IReadOnlyList<string> ParsedAliases);
public sealed record RequirementCountableReferenceView(
    Guid Id, string Name, string? Category, string Notes, string Aliases,
    IReadOnlyList<string> ParsedAliases);
public sealed record RequirementSoulReferenceView(
    Guid Id, string Name, string? Category, string Notes, string Aliases,
    IReadOnlyList<string> ParsedAliases);
public sealed record RequirementMethodReferenceView(
    Guid Id, string Name, string? Category, string Notes, string Aliases,
    IReadOnlyList<string> ParsedAliases);

public sealed record RequirementCategorySuggestionsView(
    IReadOnlyList<string> PredicateCategories,
    IReadOnlyList<string> CrestCategories,
    IReadOnlyList<string> MethodCategories,
    IReadOnlyList<string> ItemCategories,
    IReadOnlyList<string> CountableCategories,
    IReadOnlyList<string> SoulCategories);

public sealed record RequirementCatalogueReferenceView(
    IReadOnlyList<RequirementPredicateReferenceView> Predicates,
    IReadOnlyList<RequirementCrestReferenceView> Crests,
    IReadOnlyList<RequirementMethodReferenceView> Methods,
    IReadOnlyList<RequirementItemReferenceView> Items,
    IReadOnlyList<CheckLocationTypeDefinition> LocationTypes,
    IReadOnlyList<RequirementCountableReferenceView> Countables,
    IReadOnlyList<RequirementSoulReferenceView> Souls);
