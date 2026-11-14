using System.Collections.Immutable;

namespace SilksongRandoLogicManager.Contracts;

// Receiver coverage only. Neither an existence assertion nor permission to write.
public sealed record RoomReceiverInventory(ImmutableArray<Guid> Subrooms, ImmutableArray<Guid> Transitions,
    ImmutableArray<Guid> Connections, ImmutableArray<Guid> Checks);
public sealed record RoomAdmissionBaseline(RoomDocumentObservation Observation, RoomReceiverInventory Inventory);
// Null Document is an authoritative targeted absence, not a retired client read.
public sealed record RoomDocumentReadResponse(Guid RoomId, RoomCompleteDocument? Document);

[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(RoomPresentObservation), "present")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(RoomMissingObservation), "missing")]
public abstract record RoomTargetObservation(Guid RoomId);
public sealed record RoomPresentObservation(RoomDocumentObservation Observation, bool IsArchived, RoomMetadata Metadata)
    : RoomTargetObservation(Observation.RoomId);
public sealed record RoomMissingObservation(Guid RoomId) : RoomTargetObservation(RoomId);

public static class RoomEntryProtocol
{
    public static RoomAdmissionBaseline Baseline(RoomCompleteDocument document)
    {
        RoomDocumentReconciliation.Validate(document);
        return new(document.Observation, new(
            Ids(document.Subrooms.Select(x => x.Observation.EntityId)),
            Ids(document.Transitions.Select(x => x.Observation.EntityId)),
            Ids(document.Connections.Select(x => x.Observation.EntityId)),
            Ids(document.Checks.Select(x => x.Observation.EntityId))));
    }

    private static ImmutableArray<Guid> Ids(IEnumerable<Guid> ids) => ids.OrderBy(x => x).ToImmutableArray();

    public static bool ValidBaseline(RoomAdmissionBaseline? baseline, Guid roomId)
    {
        if (roomId == Guid.Empty || baseline is null || !RoomEditingIdentity.ValidObservation(baseline.Observation, roomId)
            || baseline.Inventory is not { } inventory) return false;
        var families = new[] { inventory.Subrooms, inventory.Transitions, inventory.Connections, inventory.Checks };
        if (families.Any(x => x.IsDefault)) return false;
        var ids = families.SelectMany(x => x).ToArray();
        return ids.All(x => x != Guid.Empty && x != roomId) && ids.Distinct().Count() == ids.Length;
    }

    public static bool SameBaseline(RoomAdmissionBaseline left, RoomAdmissionBaseline right) =>
        left.Observation == right.Observation
        && left.Inventory.Subrooms.SequenceEqual(right.Inventory.Subrooms)
        && left.Inventory.Transitions.SequenceEqual(right.Inventory.Transitions)
        && left.Inventory.Connections.SequenceEqual(right.Inventory.Connections)
        && left.Inventory.Checks.SequenceEqual(right.Inventory.Checks);

    public static bool SameOpen(RoomOpenRequest left, RoomOpenRequest right) => left.Owner == right.Owner && ((left, right) switch
    {
        (RoomCompleteOpenRequest, RoomCompleteOpenRequest) => true,
        (RoomBaselineAdmissionRequest a, RoomBaselineAdmissionRequest b) => SameBaseline(a.Baseline, b.Baseline),
        _ => false
    });

    public static bool ValidTarget(RoomTargetObservation? target, Guid roomId) => roomId != Guid.Empty && (target switch
    {
        RoomPresentObservation present => present.RoomId == roomId && RoomEditingIdentity.ValidObservation(present.Observation, roomId) && present.Metadata is not null,
        RoomMissingObservation missing => missing.RoomId == roomId,
        _ => false
    });

    public static bool Matches(RoomPresentObservation present, RoomCompleteDocument document) =>
        present.Observation == document.Observation && present.IsArchived == document.Header.Observation.IsArchived
        && present.Metadata == document.Header.Metadata;
}
