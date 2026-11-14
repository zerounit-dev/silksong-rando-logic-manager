namespace SilksongRandoLogicManager.Contracts;

// Feature receipt context for explicit read-only recovery; shared facts/timestamps
// use the STORE owner's permanent SharedMutationSegment contract.
public sealed record SceneAffectedRecovery(IReadOnlyList<Guid> RoomIds, IReadOnlyList<Guid> GroupIds);
