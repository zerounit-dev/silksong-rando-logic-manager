using System.Text.Json.Serialization;

namespace Silksong_Rando_Logic_Manager.Services;

public sealed record RequirementCatalogueExportResult(string FileName, byte[] JsonUtf8);

public sealed record RequirementCatalogueImportParseResult(
    RequirementCatalogueSnapshot? Snapshot,
    IReadOnlyList<string> Errors)
{
    public bool IsAccepted => Snapshot is not null && Errors.Count == 0;
    public static RequirementCatalogueImportParseResult Accepted(RequirementCatalogueSnapshot snapshot) => new(snapshot, []);
    public static RequirementCatalogueImportParseResult Rejected(params string[] errors) => new(null, errors);
}

public sealed record RequirementCatalogueSnapshot(
    IReadOnlyList<RequirementCatalogueExchangePredicate> Predicates,
    IReadOnlyList<RequirementCatalogueExchangeItem> Items);

public sealed record RequirementCatalogueExchangeDocument(
    [property: JsonPropertyName("requirementCatalogueVersion")] int RequirementCatalogueVersion,
    [property: JsonPropertyName("predicates")] IReadOnlyList<RequirementCatalogueExchangePredicate> Predicates,
    [property: JsonPropertyName("items")] IReadOnlyList<RequirementCatalogueExchangeItem> Items);

public sealed record RequirementCatalogueExchangePredicate(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("inputSyntax")] string InputSyntax,
    [property: JsonPropertyName("outputSyntax")] string OutputSyntax,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("sortOrder")] int SortOrder,
    [property: JsonPropertyName("aliases")] string Aliases);

public sealed record RequirementCatalogueExchangeItem(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("outputValue")] string OutputValue,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("sortOrder")] int SortOrder,
    [property: JsonPropertyName("aliases")] string Aliases);

public enum RequirementCatalogueImportStatus
{
    Committed,
    ValidationRejected,
    Failed
}

public sealed record RequirementCatalogueImportOutcome(
    RequirementCatalogueImportStatus Status,
    IReadOnlyList<RequirementCatalogueValidationIssue> ValidationIssues,
    string? Message = null)
{
    public static RequirementCatalogueImportOutcome Committed() => new(RequirementCatalogueImportStatus.Committed, []);
    public static RequirementCatalogueImportOutcome ValidationRejected(IReadOnlyList<RequirementCatalogueValidationIssue> issues) => new(RequirementCatalogueImportStatus.ValidationRejected, issues);
    public static RequirementCatalogueImportOutcome Failed(string message) => new(RequirementCatalogueImportStatus.Failed, [], message);
}
