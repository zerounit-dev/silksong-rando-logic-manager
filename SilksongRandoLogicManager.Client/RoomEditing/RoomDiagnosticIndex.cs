using System.Collections.Immutable;
using SilksongRandoLogicManager.Contracts;
using static Silksong_Rando_Logic_Manager.Services.RoomPresentationRules;

namespace SilksongRandoLogicManager.Client.RoomEditing;

/// <summary>Private candidate preparation only. Facts and their update inputs are
/// immutable and travel together through catalogue capture/install/rollback.</summary>
public static class RoomDiagnosticIndex
{
    public sealed record Destination(
        ImmutableDictionary<string, (int Active, Guid Id, bool Archived)> References,
        ImmutableArray<string> Suggestions,
        ImmutableDictionary<Guid, TransitionLookupFacts> ById,
        ImmutableDictionary<(Guid?, Guid?), int> Inverses);

    internal sealed record RoomInput(string Reference, bool Archived,
        ImmutableArray<TransitionLookupFacts> Transitions, ImmutableArray<CheckLookupFacts> Checks,
        ImmutableHashSet<string> CheckNames);

    public sealed record Facts(
        ImmutableDictionary<string, (int Active, Guid Id, bool Archived)> References,
        ImmutableDictionary<Guid, Destination> Destinations,
        ImmutableDictionary<string, ImmutableHashSet<Guid>> CheckOwners)
    {
        internal ImmutableDictionary<Guid, RoomInput> Inputs { get; init; } = ImmutableDictionary<Guid, RoomInput>.Empty;
        internal ImmutableDictionary<string, ImmutableDictionary<Guid, bool>> ReferenceOwners { get; init; } =
            ImmutableDictionary.Create<string, ImmutableDictionary<Guid, bool>>(StringComparer.OrdinalIgnoreCase);
    }
    public static Facts Empty { get; } = new(
        ImmutableDictionary.Create<string, (int, Guid, bool)>(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<Guid, Destination>.Empty,
        ImmutableDictionary.Create<string, ImmutableHashSet<Guid>>(StringComparer.OrdinalIgnoreCase));

    public static Facts Prepare(Facts prior, IReadOnlyDictionary<Guid, ConfirmedRoomFacts> source, IReadOnlyCollection<Guid> changed)
    {
        var preparation = new Preparation(prior, source, changed);
        foreach (var _ in preparation.Steps()) { }
        return preparation.Result;
    }
    public static async Task<Facts> PrepareAsync(Facts prior, IReadOnlyDictionary<Guid, ConfirmedRoomFacts> source,
        IReadOnlyCollection<Guid> changed, Func<Task> yield)
    {
        var preparation = new Preparation(prior, source, changed);
        var work = 0;
        foreach (var _ in preparation.Steps())
            if (++work % 128 == 0) await yield(); // Real browser task + caller generation guard.
        return preparation.Result;
    }
    private sealed class Preparation(Facts prior, IReadOnlyDictionary<Guid, ConfirmedRoomFacts> source, IReadOnlyCollection<Guid> changed)
    {
        public Facts Result { get; private set; } = prior;
        public IEnumerable<int> Steps()
        {
            var inputs = prior.Inputs.ToBuilder();
            var destinations = prior.Destinations.ToBuilder();
            var owners = prior.CheckOwners.ToBuilder();
            var referenceGroups = prior.ReferenceOwners.ToBuilder();
            var references = prior.References.ToBuilder();
            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var contentChanged = false;
            foreach (var id in changed)
            {
                prior.Inputs.TryGetValue(id, out var old);
                source.TryGetValue(id, out var room);
                if (old is null && room is null) { yield return 0; continue; }
                var reference = room?.Header.Values.ReferenceId.Trim() ?? "";
                var archived = room?.Header.Observation.IsArchived ?? false;
                var referenceChanged = old is null || room is null || old.Archived != archived
                    || !StringComparer.OrdinalIgnoreCase.Equals(old.Reference, reference);
                if (referenceChanged)
                {
                    if (old is not null) ReferenceOwner(id, old.Reference, old.Archived, true);
                    if (room is not null) ReferenceOwner(id, reference, archived, false);
                }
                ImmutableHashSet<string>? names = null;
                if (room is not null && old is not null && old.Archived == archived && old.Checks == room.Checks)
                    names = old.CheckNames;
                else if (room is not null)
                {
                    var nextNames = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in room.Checks)
                    {
                        if (!archived && !row.Observation.IsArchived && Text(row.Values.FriendlyName)) nextNames.Add(row.Values.FriendlyName.Trim());
                        yield return 0;
                    }
                    names = old is not null && old.CheckNames.SetEquals(nextNames) ? old.CheckNames : nextNames.ToImmutable();
                }
                if (!ReferenceEquals(names, old?.CheckNames))
                {
                    if (old is not null)
                        foreach (var name in old.CheckNames)
                        {
                            var remaining = owners[name].Remove(id);
                            if (remaining.Count == 0) owners.Remove(name); else owners[name] = remaining;
                            yield return 0;
                        }
                    if (names is not null)
                        foreach (var name in names)
                        {
                            owners[name] = owners.TryGetValue(name, out var set) ? set.Add(id) : ImmutableHashSet.Create(id);
                            yield return 0;
                        }
                }
                // Lookup records contain precisely identity/archive/alias/authored
                // destination/resolved IDs, never observation, Notes or Requirements.
                var sameTransitions = room is not null && old is not null && old.Transitions == room.Transitions;
                if (!sameTransitions && room is not null && old is not null && old.Transitions.Length == room.Transitions.Length)
                {
                    sameTransitions = true;
                    for (var i = 0; i < room.Transitions.Length; i++)
                    {
                        if (old.Transitions[i] != room.Transitions[i]) sameTransitions = false;
                        yield return 0;
                    }
                }
                if (room is not null && !sameTransitions)
                {
                    var aliases = new Dictionary<string, (int Active, Guid Id, bool Archived)>(StringComparer.OrdinalIgnoreCase);
                    var active = ImmutableDictionary.CreateBuilder<Guid, TransitionLookupFacts>();
                    var inverses = new Dictionary<(Guid?, Guid?), int>();
                    var suggestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in room.Transitions)
                    {
                        if (Text(row.Values.Alias))
                        {
                            var key = row.Values.Alias.Trim();
                            var previous = aliases.GetValueOrDefault(key);
                            var count = previous.Active + (row.Observation.IsArchived ? 0 : 1);
                            aliases[key] = (count, count == 1 ? row.Observation.IsArchived ? previous.Id : row.Observation.EntityId : Guid.Empty,
                                previous.Archived || row.Observation.IsArchived);
                        }
                        if (!row.Observation.IsArchived)
                        {
                            active.Add(row.Observation.EntityId, row);
                            var endpoint = (row.ResolvedDestinationRoomId, row.ResolvedDestinationTransitionId);
                            inverses[endpoint] = inverses.GetValueOrDefault(endpoint) + 1;
                            if (Text(row.Values.Alias)) suggestions.Add(row.Values.Alias);
                        }
                        yield return 0;
                    }
                    destinations[id] = new(aliases.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
                        suggestions.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray(), active.ToImmutable(), inverses.ToImmutableDictionary());
                }
                if (referenceChanged || !sameTransitions || !ReferenceEquals(names, old?.CheckNames))
                {
                    contentChanged = true;
                    if (room is null) { inputs.Remove(id); destinations.Remove(id); }
                    else inputs[id] = new(reference, archived, sameTransitions ? old!.Transitions : room.Transitions, room.Checks, names!);
                }
                else if (room is not null && old!.Checks != room.Checks)
                    // Equal public names still consume the new immutable input proof.
                    inputs[id] = old with { Checks = room.Checks };
                yield return 0;
            }
            foreach (var key in affected)
            {
                var group = referenceGroups[key];
                if (group.Count == 0) { referenceGroups.Remove(key); references.Remove(key); continue; }
                var count = 0; var unique = Guid.Empty; var archived = false;
                foreach (var (id, isArchived) in group)
                {
                    if (isArchived) archived = true; else { count++; unique = id; }
                    yield return 0;
                }
                references[key] = (count, count == 1 ? unique : Guid.Empty, archived);
            }
            var preparedInputs = inputs.ToImmutable();
            if (contentChanged)
                Result = new(references.ToImmutable(), destinations.ToImmutable(), owners.ToImmutable())
                    { Inputs = preparedInputs, ReferenceOwners = referenceGroups.ToImmutable() };
            else if (!ReferenceEquals(preparedInputs, prior.Inputs))
                Result = prior with { Inputs = preparedInputs };

            void ReferenceOwner(Guid id, string reference, bool archived, bool remove)
            {
                if (!Text(reference)) return;
                var group = referenceGroups.TryGetValue(reference, out var existing) ? existing : ImmutableDictionary<Guid, bool>.Empty;
                referenceGroups[reference] = remove ? group.Remove(id) : group.SetItem(id, archived);
                affected.Add(reference);
            }
        }
    }
}
