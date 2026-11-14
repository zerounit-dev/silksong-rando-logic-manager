using System.Collections.Immutable;

namespace SilksongRandoLogicManager.Client;

public sealed partial class ConfirmedCatalogue
{
    // Identity witnesses, not clocks or a history of deleted IDs. A complete
    // inventory supersedes all scoped read witnesses. Targeted reads witness only
    // their supplied room/group, plus the explicitly supplied presentation orders.
    internal sealed record ReadObservations(object Inventory,
        ImmutableDictionary<Guid, object> Rooms, ImmutableDictionary<Guid, object> Groups,
        object RoomOrder, object GroupOrder, ImmutableDictionary<Guid, object> MembershipOrders)
    {
        internal static ReadObservations Empty() => new(new(),
            ImmutableDictionary<Guid, object>.Empty, ImmutableDictionary<Guid, object>.Empty, new(), new(),
            ImmutableDictionary<Guid, object>.Empty);
        internal static ReadObservations Complete() => Empty();
        internal ReadObservations Targeted(Guid roomId, Guid? groupId)
            => Observed([roomId], groupId is { } id ? [id] : [], true, true, []);

        internal ReadObservations Observed(IEnumerable<Guid> rooms, IEnumerable<Guid> groups,
            bool roomOrder, bool groupOrder, IEnumerable<Guid> memberships)
        {
            var witness = new object();
            return this with { Rooms = Rooms.SetItems(rooms.Select(id => KeyValuePair.Create(id, witness))),
                Groups = Groups.SetItems(groups.Select(id => KeyValuePair.Create(id, witness))),
                RoomOrder = roomOrder ? witness : RoomOrder, GroupOrder = groupOrder ? witness : GroupOrder,
                MembershipOrders = MembershipOrders.SetItems(memberships.Select(id => KeyValuePair.Create(id, witness))) };
        }
    }

    private ReadObservations readObservations = ReadObservations.Empty();

    /// <summary>Opaque browser-local original-request observation. Retain with the
    /// original request/operation/attempt across failed delivery; never recapture at
    /// Retry for an already usable original result. Its Room values are global lookup
    /// facts only; immutable full native/document evidence stays feature-owned.
    /// Not a save/token precondition.</summary>
    public sealed class MutationReadContext
    {
        internal ConfirmedCatalogue Owner { get; }
        internal ReadObservations Reads { get; }
        internal ImmutableDictionary<Guid, ConfirmedRoomFacts> Rooms { get; }
        internal ImmutableArray<SilksongRandoLogicManager.Contracts.CatalogueGroup> Groups { get; }
        internal ImmutableArray<Guid> Order { get; }
        internal ConfirmedVocabulary? Vocabulary { get; }
        internal bool IsProjectionRead { get; }
        internal MutationReadContext(ConfirmedCatalogue owner, bool projectionRead = false)
        {
            Owner = owner; Reads = owner.readObservations; Rooms = owner.Rooms;
            Groups = owner.Groups; Order = owner.PresentationRoomIds; Vocabulary = owner.Vocabulary;
            IsProjectionRead = projectionRead;
        }
    }

    private static bool SameRoomRead(Candidate current, MutationReadContext? source, Guid id) =>
        source is not null && ReferenceEquals(current.Reads.Inventory, source.Reads.Inventory)
        && ReferenceEquals(current.Reads.Rooms.GetValueOrDefault(id), source.Reads.Rooms.GetValueOrDefault(id));

    private static bool SameGroupRead(Candidate current, MutationReadContext? source, Guid id) =>
        source is not null && ReferenceEquals(current.Reads.Inventory, source.Reads.Inventory)
        && ReferenceEquals(current.Reads.Groups.GetValueOrDefault(id), source.Reads.Groups.GetValueOrDefault(id));
}
