namespace SilksongRandoLogicManager.Contracts;

/// <summary>One actual commit, or one explicitly proven contiguous committed
/// prefix. Segment order is server commit coverage, never HTTP arrival order.</summary>
public sealed record SharedMutationSegment(RoomMutationPatch? Rooms, VocabularyMutationPatch? Requirements);

public sealed record RoomFactChange(RoomDocumentObservation? Before, RoomScalarSnapshot After);
public sealed record RoomFactRemoval(RoomDocumentObservation Before);
public sealed record GroupFactChange(CatalogueGroup? Before, CatalogueGroup? After);
/// <summary>Authoritative SQLite order for the named membership collection,
/// including archived rooms. Every actually repaired scalar order also travels
/// in the affected room replacements.</summary>
public sealed record RoomMembershipOrder(Guid? GroupId, IReadOnlyList<Guid> RoomIds);

/// <summary>Complete means every effect of THIS covered mutation is represented,
/// not that every game record is included. It is not a complete-document claim
/// for any uncovered row delta; those retain their existing CoversFrom rules.
/// Supplied coverage is not proof of client application: bookkeeping separately
/// verifies applied/already-present effects. Shared timestamps do not admit facts
/// or decide operation success, room incorporation or acknowledgement.</summary>
public sealed record RoomMutationPatch(ScopeTimestampBracket Timestamps, bool Complete,
    IReadOnlyList<RoomFactChange> Replacements, IReadOnlyList<RoomFactRemoval> Removals,
    IReadOnlyList<GroupFactChange> Groups, IReadOnlyList<RoomMembershipOrder> RoomOrders,
    IReadOnlyList<Guid>? GroupOrder)
{
    /// <summary>SQLite presentation order already observed by a targeted complete
    /// room read. Order evidence only: missing IDs do not certify room deletion.</summary>
    public IReadOnlyList<Guid>? ReadPresentationOrder { get; init; }
}

public enum CatalogueDefinitionKind { Predicate, Crest, Method, Item, Countable, Soul }
/// <summary>Before/after are immutable publication evidence, not new edit
/// baselines or server save preconditions. Null after is authoritative removal.</summary>
public sealed record DefinitionFactChange(CatalogueDefinitionKind Kind, VocabularyDefinition? Before, VocabularyDefinition? After);
public sealed record DefinitionKindOrder(CatalogueDefinitionKind Kind, IReadOnlyList<Guid> DefinitionIds);
public sealed record VocabularyMutationPatch(ScopeTimestampBracket Timestamps, bool Complete,
    IReadOnlyList<DefinitionFactChange> Definitions, IReadOnlyList<DefinitionKindOrder> Orders,
    CatalogueVocabulary? Replacement);
