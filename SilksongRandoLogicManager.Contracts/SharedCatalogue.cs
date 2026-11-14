namespace SilksongRandoLogicManager.Contracts;

public sealed record SharedFreshness(DateTime RoomsUtc, DateTime RequirementsUtc);
/// <summary>Exact cached timestamps of one committed mutation/contiguous segment.
/// These are publication evidence, never save preconditions or a coverage claim.
/// Compare the current client token to BeforeUtc for equality only; complete applied
/// coverage permits AfterUtc, otherwise keep the current token. Chronological ordering
/// of shared tokens is not authority to install or suppress record facts.</summary>
public sealed record ScopeTimestampBracket(DateTime BeforeUtc, DateTime AfterUtc);
public sealed record SharedMutationTimestamps(ScopeTimestampBracket Rooms, ScopeTimestampBracket Requirements);
public sealed record CatalogueGroup(Guid Id, string FriendlyName, int SortOrder, string? ZoneReferenceText,
    Guid? ResolvedMapZoneId, bool IsVirtual, DateTime UpdatedUtc);
public sealed record VocabularyDefinition(Guid Id, string Name, string? Category, int SortOrder, string Aliases,
    string? InputSyntax, string Output, string Notes);
public sealed record CatalogueVocabulary(DateTime Token,
    IReadOnlyList<VocabularyDefinition> Predicates, IReadOnlyList<VocabularyDefinition> Crests,
    IReadOnlyList<VocabularyDefinition> Methods, IReadOnlyList<VocabularyDefinition> Items,
    IReadOnlyList<VocabularyDefinition> Countables, IReadOnlyList<VocabularyDefinition> Souls);
