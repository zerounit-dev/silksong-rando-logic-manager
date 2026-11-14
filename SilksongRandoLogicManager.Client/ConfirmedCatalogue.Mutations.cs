using System.Collections.Immutable;
using System.Text.Json;
using Silksong_Rando_Logic_Manager.Services;
using SilksongRandoLogicManager.Contracts;

namespace SilksongRandoLogicManager.Client;

public sealed record PreparedCataloguePublication(Action<Action> Install);

public sealed partial class ConfirmedCatalogue
{
    public sealed partial class MutationAdmission
    {
        public CacheRequest BindCache(HttpRequestMessage request) => store.Browser.Bind(request);
        public void PublishCache<T>(ReceivedCacheResult<T> result) => store.Browser.Publish(result);
        // Only the owning feature calls this AFTER full native/document/I admission.
        // Do not scan raw retained results or override a different common source.
        public void ObserveRoomImage(RoomCompleteDocument document)
        {
            if (Current(() => true) && store.Rooms.TryGetValue(document.Observation.RoomId, out var room)
                && room.Observation == document.Observation && document.Header.Metadata.ImageExists is { } exists)
                store.Browser.ObserveImages([new(document.Observation.RoomId, exists)]);
        }
        /// <summary>Call only after feature visit/receipt/row admission. Preparation
        /// is private; the returned installer may follow native workspace staging.
        /// No read, retry, save precondition or draft mutation is performed here.</summary>
        public MutationReadContext CaptureReadContext()
        {
            if (!Current(() => true)) throw new OperationCanceledException();
            return new(store);
        }

        /// <summary>Only for a separately later read of originally MISSING
        /// projection evidence. Never use this to freshen a usable original result.
        /// Retain with that read identity if delivery is lost.</summary>
        public MutationReadContext CaptureProjectionReadContext()
        {
            if (!Current(() => true)) throw new OperationCanceledException();
            return new(store, projectionRead: true);
        }

        // Sources correspond one-for-one with the ORIGINAL committed segments.
        // A missing source is conservative uncertainty, not a feature/I/A veto.
        public PreparedCataloguePublication PreparePublication(IReadOnlyList<SharedMutationSegment> segments,
            IReadOnlyList<MutationReadContext?> sources)
            => PreparePublicationCore(segments, sources, applyFacts: true);

        // For a feature that has already installed the facts through its coherent
        // update path. Verify the GLOBAL projected effects against CURRENT RAM; never
        // reinstall them or notify merely to record freshness. This is not an
        // complete-document equality, operation-success, room incorporation or acknowledgement decision.
        public void RecordAppliedCoverage(IReadOnlyList<SharedMutationSegment> segments,
            IReadOnlyList<MutationReadContext?> sources) =>
            PreparePublicationCore(segments, sources, applyFacts: false).Install(() => { });

        private PreparedCataloguePublication PreparePublicationCore(IReadOnlyList<SharedMutationSegment> segments,
            IReadOnlyList<MutationReadContext?> sources, bool applyFacts)
        {
            if (!Current(() => true)) throw new OperationCanceledException();
            if (segments is null || segments.Any(x => x is null)) throw new JsonException("Missing mutation segments.");
            if (sources is null || sources.Count != segments.Count) throw new JsonException("Missing original segment source associations.");
            var captured = store.CaptureCandidate();
            var prepared = captured;
            for (var i = 0; i < segments.Count; i++)
            {
                var source = sources[i] is { } supplied && ReferenceEquals(supplied.Owner, store) ? supplied : null;
                prepared = PrepareMutation(prepared, captured, segments[i], source, applyFacts);
            }
            var capturedGeneration = store.generation;
            return new(incorporate =>
            {
                // Recheck immediately before the single install, after any native
                // generation/Stage checks performed by the owning workspace.
                if (!Current(() => true) || store.generation != capturedGeneration) throw new OperationCanceledException();
                // Publication itself is synchronous. Compare the current confirmed
                // tokens here, not merely when native staging was prepared.
                var current = store.CaptureCandidate();
                // Native staging and this install are synchronous. If another
                // publication nevertheless intervened, prepare again against its
                // current storage instead of substituting a different candidate.
                if (current != captured) throw new OperationCanceledException();
                if (segments.Count == 0) incorporate();
                else store.Install(prepared, incorporate);
            });
        }

        public void Publish(SharedMutationSegment segment, MutationReadContext? source) =>
            PreparePublication([segment], [source]).Install(() => { });
    }

    private static Candidate PrepareMutation(Candidate prior, Candidate applicationBase, SharedMutationSegment segment,
        MutationReadContext? source, bool applyFacts)
    {
        if (!prior.Initialized) throw new JsonException("The shared store is not initialized.");
        var rooms = prior.Rooms; var groups = prior.Groups; var order = prior.Order;
        var vocabulary = prior.Vocabulary;
        var ownsDefinitionSource = source is not null && ReferenceEquals(applicationBase.Vocabulary, source.Vocabulary);
        // Earlier scoped reads in this same response protect against subsequent
        // original results. Separately later read segments share request admission,
        // but must not suppress each other merely because an earlier one was read.
        var provenanceBase = source?.IsProjectionRead == true ? applicationBase : prior;
        var observedRooms = new HashSet<Guid>(); var observedGroups = new HashSet<Guid>();
        var changedRooms = new HashSet<Guid>();
        var membershipChanged = false;
        var observedMemberships = new HashSet<Guid>(); var observedGroupOrder = false; var observedRoomOrder = false;
        if (segment.Rooms is { } roomEvidence) ValidateBracket(roomEvidence.Timestamps);
        if (segment.Requirements is { } definitionEvidence) ValidateBracket(definitionEvidence.Timestamps);
        if (segment.Rooms is { } patch)
        {
            ValidateBracket(patch.Timestamps);
            if (patch.Replacements is null || patch.Removals is null || patch.Groups is null
                || patch.RoomOrders is null) throw new JsonException("Incomplete room mutation patch.");
            Unique(patch.Replacements, x => x.After?.Observation?.RoomId ?? Guid.Empty);
            Unique(patch.Removals, x => x.Before?.RoomId ?? Guid.Empty);
            var removalIds = patch.Removals.Select(x => x.Before.RoomId).ToHashSet();
            if (patch.Replacements.Any(x => removalIds.Contains(x.After.Observation.RoomId))) throw new JsonException("Mutation replacement/removal overlap.");
            var admittedAll = source is not null;
            bool CanUpdateGroup(Guid id) => SameGroupRead(provenanceBase, source, id)
                && (applicationBase.Groups.Any(x => x.Id == id) || !source!.Groups.Any(x => x.Id == id));
            bool HasGroup(Guid? id) => id is null || groups.Any(x => x.Id == id)
                || patch.Groups.Any(x => x.After is { } group && group.Id == id && CanUpdateGroup(group.Id));
            foreach (var replacement in patch.Replacements)
            {
                RoomDocumentReconciliation.Validate(replacement.After);
                var id = replacement.After.Observation.RoomId;
                if (replacement.Before is { } before && !RoomEditingIdentity.ValidObservation(before, id)) throw new JsonException("Wrong replacement baseline identity.");
                rooms.TryGetValue(id, out var old);
                var projected = ConfirmedRoomFacts.Project(replacement.After, old);
                if (SameGlobalRoomFacts(old, projected)) { observedRooms.Add(id); continue; }
                if (!applyFacts || !SameRoomRead(provenanceBase, source, id)
                    || !applicationBase.Rooms.ContainsKey(id) && source!.Rooms.ContainsKey(id)
                    || !HasGroup(replacement.After.Header.RoomGroupId)
                    || old is not null && old.Observation == replacement.After.Observation
                        && applicationBase.Rooms.TryGetValue(id, out var retainedAtApplication)
                        && (!source!.Rooms.TryGetValue(id, out var originalAtSource)
                            || !SameGlobalRoomFacts(retainedAtApplication, originalAtSource))
                    || old is not null && old.Observation.CacheFlagUtc > replacement.After.Observation.CacheFlagUtc)
                {
                    admittedAll = false;
                    continue;
                }
                rooms = rooms.SetItem(id, projected);
                changedRooms.Add(id);
                membershipChanged |= old is null;
                observedRooms.Add(id);
            }
            foreach (var removal in patch.Removals)
            {
                if (!RoomEditingIdentity.ValidObservation(removal.Before, removal.Before.RoomId)) throw new JsonException("Unusable removed room observation.");
                if (!rooms.TryGetValue(removal.Before.RoomId, out var old)) { observedRooms.Add(removal.Before.RoomId); continue; }
                if (!applyFacts || !SameRoomRead(provenanceBase, source, removal.Before.RoomId)
                    || old.Observation.CacheFlagUtc > removal.Before.CacheFlagUtc)
                { admittedAll = false; continue; }
                rooms = rooms.Remove(removal.Before.RoomId);
                changedRooms.Add(removal.Before.RoomId);
                membershipChanged = true;
                observedRooms.Add(removal.Before.RoomId);
            }
            var groupChanges = patch.Groups.Select(x =>
            {
                if (x is null || x.Before is null && x.After is null || x.Before is { } b && x.After is { } a && b.Id != a.Id)
                    throw new JsonException("Unusable group change.");
                var value = x.After ?? x.Before!;
                if (value.Id == Guid.Empty || value.FriendlyName is null || value.UpdatedUtc.Kind != DateTimeKind.Utc)
                    throw new JsonException("Unusable group facts.");
                return (Id: value.Id, Change: x);
            }).ToArray();
            Unique(groupChanges, x => x.Id);
            {
                var byId = groups.ToDictionary(x => x.Id);
                foreach (var change in groupChanges)
                {
                    if (change.Change.After is { } after)
                    {
                        byId.TryGetValue(change.Id, out var old);
                        if (old == after) { observedGroups.Add(change.Id); continue; }
                        if (!applyFacts || !CanUpdateGroup(change.Id)
                             // Protect intervening facts independently of authored
                             // timestamps (imports preserve equal/backward stamps).
                             || applicationBase.Groups.FirstOrDefault(x => x.Id == change.Id)
                                 != source!.Groups.FirstOrDefault(x => x.Id == change.Id))
                        { admittedAll = false; continue; }
                        byId[change.Id] = after;
                        observedGroups.Add(change.Id);
                    }
                    else if (!byId.ContainsKey(change.Id)) { observedGroups.Add(change.Id); continue; }
                    else if (!applyFacts || !CanUpdateGroup(change.Id) || rooms.Values.Any(x => x.Header.RoomGroupId == change.Id))
                    {
                        // A retained unrelated fact may still reference this group
                        // on mismatch. Do not orphan it or invent its deletion.
                        admittedAll = false;
                    }
                    else if (byId.TryGetValue(change.Id, out var old) && old != change.Change.Before)
                    { admittedAll = false; }
                    else { byId.Remove(change.Id); observedGroups.Add(change.Id); }
                }
                groups = groups.Where(x => byId.ContainsKey(x.Id)).Select(x => byId[x.Id])
                    .Concat(groupChanges.Where(x => !groups.Any(g => g.Id == x.Id) && byId.ContainsKey(x.Id)).Select(x => byId[x.Id])).ToImmutableArray();
                if (membershipChanged)
                {
                    var orderedRoomIds = order.ToHashSet();
                    order = order.Where(rooms.ContainsKey).Concat(rooms.Keys.Where(id => !orderedRoomIds.Contains(id))).ToImmutableArray();
                }
                var canRepairRoomOrder = (patch.RoomOrders.Count != 0 || patch.ReadPresentationOrder is not null)
                    && applyFacts && admittedAll && source is not null
                    && ReferenceEquals(provenanceBase.Reads.RoomOrder, source.Reads.RoomOrder)
                    && applicationBase.Order.SequenceEqual(source.Order);
                var canRepairGroupOrder = patch.GroupOrder is not null && applyFacts && admittedAll && source is not null
                    && ReferenceEquals(provenanceBase.Reads.GroupOrder, source.Reads.GroupOrder)
                    && applicationBase.Groups.Select(x => x.Id).SequenceEqual(source.Groups.Select(x => x.Id));
                var membershipKeys = new HashSet<Guid?>();
                foreach (var repair in patch.RoomOrders)
                {
                    if (repair is null || repair.RoomIds is null || !membershipKeys.Add(repair.GroupId)) throw new JsonException("Duplicate or missing membership order.");
                    Unique(repair.RoomIds, x => x);
                    var known = rooms.Values.Where(x => x.Header.RoomGroupId == repair.GroupId).Select(x => x.Observation.RoomId).ToHashSet();
                    var exact = known.SetEquals(repair.RoomIds);
                    if (!exact) admittedAll = false;
                    var repaired = RepairKnownOrder(order, repair.RoomIds, known);
                    var membershipKey = repair.GroupId ?? Guid.Empty;
                    var sameMembershipRead = source is not null && ReferenceEquals(
                        provenanceBase.Reads.MembershipOrders.GetValueOrDefault(membershipKey),
                        source.Reads.MembershipOrders.GetValueOrDefault(membershipKey));
                    if (canRepairRoomOrder && sameMembershipRead) order = repaired;
                    else if (!order.SequenceEqual(repaired)) admittedAll = false;
                    if (exact && order.SequenceEqual(repaired)) observedMemberships.Add(membershipKey);
                }
                if (patch.ReadPresentationOrder is { } readOrder)
                {
                    Unique(readOrder, x => x);
                    var known = rooms.Keys.ToHashSet();
                    if (!known.SetEquals(readOrder)) admittedAll = false;
                    var repaired = RepairKnownOrder(order, readOrder, known);
                    if (canRepairRoomOrder) order = repaired;
                    else if (!order.SequenceEqual(repaired)) admittedAll = false;
                    if (order.SequenceEqual(repaired)) observedRoomOrder = true;
                }
                if (patch.GroupOrder is { } groupOrder)
                {
                    Unique(groupOrder, x => x);
                    var known = groups.Select(x => x.Id).ToHashSet();
                    var exact = known.SetEquals(groupOrder);
                    if (!exact) admittedAll = false;
                    var groupIds = RepairKnownOrder(groups.Select(x => x.Id).ToImmutableArray(), groupOrder, known);
                    var values = groups.ToDictionary(x => x.Id);
                    if (canRepairGroupOrder) groups = groupIds.Select(id => values[id]).ToImmutableArray();
                    else if (!groups.Select(x => x.Id).SequenceEqual(groupIds)) admittedAll = false;
                    if (exact && groups.Select(x => x.Id).SequenceEqual(groupIds)) observedGroupOrder = true;
                }
            }
        }
        if (segment.Requirements is { } definitions)
        {
            ValidateBracket(definitions.Timestamps);
            if (definitions.Definitions is null || definitions.Orders is null) throw new JsonException("Incomplete definition patch.");
            if (definitions.Replacement is { } whole)
            {
                if (definitions.Definitions.Count != 0 || definitions.Orders.Count != 0 || whole.Token != definitions.Timestamps.AfterUtc)
                    throw new JsonException("Mixed whole/targeted vocabulary patch.");
                var replacement = PrepareVocabulary(whole);
                if (!SameVocabularyFacts(vocabulary!, replacement))
                {
                    if (applyFacts && ownsDefinitionSource) vocabulary = replacement;
                }
            }
            else
            {
                var kinds = vocabulary!.Kinds.ToBuilder();
                var changed = new HashSet<(CatalogueDefinitionKind, Guid)>();
                foreach (var change in definitions.Definitions)
                {
                    if (change is null || !Enum.IsDefined(change.Kind) || change.Before is null && change.After is null
                        || change.Before is { } b && change.After is { } a && b.Id != a.Id) throw new JsonException("Unusable definition change.");
                    var value = change.After ?? change.Before!;
                    if (value.Id == Guid.Empty || !changed.Add((change.Kind, value.Id))) throw new JsonException("Duplicate definition mutation identity.");
                    var index = (int)change.Kind;
                    var rows = kinds[index]; var at = -1;
                    for (var i = 0; i < rows.Length; i++) if (rows[i].Value.Id == value.Id) { at = i; break; }
                    var old = at < 0 ? null : rows[at].Value;
                    if (change.After is { } after)
                    {
                        if (after.Name is null || after.Aliases is null || after.Output is null || after.Notes is null
                            || change.Kind == CatalogueDefinitionKind.Predicate && after.InputSyntax is null) throw new JsonException("Unusable definition.");
                        if (old == after) continue;
                        if (applyFacts && ownsDefinitionSource)
                        {
                            var prepared = new ConfirmedDefinition(after, RequirementCatalogueLanguage.ParseAliases(after.Aliases).ToImmutableArray());
                            kinds[index] = at < 0 ? rows.Add(prepared) : rows.SetItem(at, prepared);
                        }
                    }
                    else if (old is not null)
                    {
                        if (applyFacts && ownsDefinitionSource)
                            kinds[index] = rows.RemoveAt(at);
                    }
                }
                var repaired = new HashSet<CatalogueDefinitionKind>();
                foreach (var repair in definitions.Orders)
                {
                    if (repair is null || !Enum.IsDefined(repair.Kind) || repair.DefinitionIds is null || !repaired.Add(repair.Kind)) throw new JsonException("Unusable definition order.");
                    Unique(repair.DefinitionIds, x => x);
                    var index = (int)repair.Kind; var values = kinds[index].ToDictionary(x => x.Value.Id);
                    var known = values.Keys.ToHashSet();
                    var current = kinds[index].Select(x => x.Value.Id).ToImmutableArray();
                    var repairedOrder = RepairKnownOrder(current, repair.DefinitionIds, known);
                    if (!current.SequenceEqual(repairedOrder))
                    {
                        if (applyFacts && ownsDefinitionSource)
                            kinds[index] = repairedOrder.Select(id => values[id]).ToImmutableArray();
                    }
                }
                var next = new ConfirmedVocabulary(kinds.ToImmutable());
                if (!SameVocabularyFacts(vocabulary, next)) vocabulary = next;
            }
        }
        if (groups.SequenceEqual(prior.Groups)) groups = prior.Groups;
        if (order != prior.Order && order.SequenceEqual(prior.Order)) order = prior.Order;
        var candidate = PrepareCandidate(prior, rooms, groups, order, vocabulary, changedRooms);
        return source?.IsProjectionRead == true && applyFacts ? candidate with
        {
            Reads = prior.Reads.Observed(observedRooms, observedGroups, observedRoomOrder, observedGroupOrder, observedMemberships)
        } : candidate;
    }

    // Equality of the GLOBAL projection only. Full protocol validation above and
    // workspace/native reconciliation own complete body equality and incorporation.
    private static bool SameGlobalRoomFacts(ConfirmedRoomFacts? left, ConfirmedRoomFacts right) =>
        ReferenceEquals(left, right) || left is not null && left.Observation == right.Observation && left.Header == right.Header
        && left.Transitions.SequenceEqual(right.Transitions) && left.Checks.SequenceEqual(right.Checks)
        && left.Requirements.SequenceEqual(right.Requirements, StringComparer.Ordinal);

    private static bool SameVocabularyFacts(ConfirmedVocabulary left, ConfirmedVocabulary right) =>
        left.Kinds.Length == right.Kinds.Length && left.Kinds.Select((kind, index) =>
            kind.Select(x => x.Value).SequenceEqual(right.Kinds[index].Select(x => x.Value))).All(x => x);

    private static void ValidateBracket(ScopeTimestampBracket value)
    {
        if (value is null || value.BeforeUtc.Kind != DateTimeKind.Utc || value.AfterUtc.Kind != DateTimeKind.Utc
            || value.BeforeUtc.Ticks == 0 || value.AfterUtc.Ticks == 0 || value.AfterUtc < value.BeforeUtc)
            throw new JsonException("Unusable committed timestamp bracket.");
    }

    // On mismatch preserve unrelated retained IDs and their slots. Only known
    // members named by the authoritative repair are reordered; missing identities
    // remain discoverable because this partial incorporation cannot advance token.
    private static ImmutableArray<Guid> RepairKnownOrder(ImmutableArray<Guid> retained,
        IReadOnlyList<Guid> incoming, HashSet<Guid> known)
    {
        var supplied = incoming.Where(known.Contains).ToArray();
        var included = supplied.ToHashSet(); var next = 0;
        return retained.Select(id => included.Contains(id) ? supplied[next++] : id).ToImmutableArray();
    }
}
