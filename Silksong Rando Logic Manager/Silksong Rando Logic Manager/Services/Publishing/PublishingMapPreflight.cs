namespace Silksong_Rando_Logic_Manager.Services.Publishing;

internal enum MapCheckStatus { Waiting, Checking, Pass, Fail }
public sealed record MapFindingRoom(Guid Id, string Label);
public sealed record MapFindingReason(string Detail, IReadOnlyList<MapFindingRoom> Rooms);
public sealed record MapFindingRecord(string Title, IReadOnlyList<MapFindingReason> Reasons);
public enum MapFindingZoneKind { Authored, Ungrouped, Multiple, NoRoom }
public readonly record struct MapFindingZoneKey(MapFindingZoneKind Kind, Guid? GroupId = null);
public sealed record MapFindingRoomSection(Guid? RoomId, string Label, string? FullRoomLabel, int Count, IReadOnlyList<MapFindingRecord> Records);
public sealed record MapFindingZone(MapFindingZoneKey Key, string Label, int Count, IReadOnlyList<MapFindingRoomSection> Rooms);
public sealed record MapFindingReport(int Count, IReadOnlyList<MapFindingZone> Zones);
internal sealed record MapCheck(string Group, string Name, string Description,
    MapCheckStatus Status = MapCheckStatus.Waiting, MapFindingReport? Report = null, string? NotRun = null);
internal sealed record MapPreflightView(IReadOnlyList<MapCheck> Checks, bool Finished = false, string? Outcome = null, bool AwaitingDecision = false);

internal sealed class PublishingMapPreflight(PreparedGraphEvidence evidence)
{
    private const string DependencyPrefix = "dependency:";
    private const string Impassable = "impassable";
    private static bool IsPossible(IReadOnlyList<string> clause) => !clause.Contains(Impassable, StringComparer.Ordinal);
    private const string Rejected = "The canonical room graph was rejected; a complete graph is required.";
    private const string NoStarts = "No valid unconditional Spawn is assigned to a playable node.";
    private const string NoTranslation = "The shared requirement catalogue is invalid; canonical translation could not run.";
    internal static MapCheck[] NewChecks() =>
    [
        new("Invalid or incomplete logic", "Invalid Subroom Assignment", AssignmentDescription),
        new("Invalid or incomplete logic", "Unknown Prerequisite", "A rule asks the player to complete a check that isn’t defined in the map."),
        new("Invalid or incomplete logic", "Unreadable Rule", "A route or check has a blank or unreadable requirement, so we can’t tell when it’s usable."),
        new("Progression and reachability", "Orphaned Node", "We mapped a spot but gave it no incoming link, and it isn’t a starting point or story arrival."),
        new("Progression and reachability", "Disconnected Island", "A group of spots links together, but there’s no way to reach the group from a starting point or story arrival, even with the locks ignored. The individual spots may all have incoming links, so Orphaned Node would miss them."),
        new("Progression and reachability", "Self-Lock", "A switch is behind its own lock. An alternate route that doesn’t require the switch is enough for this check to pass."),
        new("Progression and reachability", "Progression Deadlock", "The links and prerequisites exist, but nothing in the chain can happen first. The Bell Switch needs the Gate Switch, and the Gate Switch needs the Bell Switch.")
    ];
    private const string AssignmentDescription = "A connection, transition, or check has a missing or invalid subroom assignment.";
    internal static MapCheck[] IntegrityChecks() =>
    [
        new("Graph integrity", "Invalid Identifiers", "A record is missing a name or usable identifier, or two records produce the same graph identifier."),
        new("Graph integrity", "Broken Room Group", "A room has no group, or its group no longer exists."),
        new("Graph integrity", "Invalid Connection", "A connection joins a subroom to itself, or connections with the same alias don’t describe the same link between two subrooms."),
        new("Graph integrity", "Invalid Subroom Assignment", AssignmentDescription),
        new("Graph integrity", "Exit to Nowhere", "A doorway has no valid destination entrance, even if it names the right room."),
        new("Graph integrity", "Invalid Requirement Catalogue Entry", "A Requirement Catalogue entry has invalid syntax, aliases, or output.")
    ];
    internal MapCheck[] Integrity(PreparedRoomGraph prepared)
    {
        var failures = evidence.Integrity.ToList();
        foreach (var reference in evidence.References.Where(r => r.HardFailure is not null))
            failures.Add(new(reference.Kind == PreparedReferenceKind.Node ? PreparedIntegrityFamily.Subroom : PreparedIntegrityFamily.Exit,
                reference.HardFailure!, reference.Record, reference.RecordKind, reference.RecordId, reference.RoomRecordId, reference.RelatedRooms));
        // A new canonical emitter must carry typed evidence rather than silently passing.
        var reported = failures.Select(x => x.Failure).ToHashSet(ReferenceEqualityComparer.Instance);
        if (prepared.Export.Failures.Any(x => !reported.Contains(x)))
            throw new InvalidOperationException("A canonical graph failure has no preparation integrity evidence.");
        return IntegrityChecks().Select((row, index) =>
        {
            var findings = failures.Where(x => (int)x.Family == index).Select(x => Finding(evidence,
                x.RecordId is { } id && x.RoomId is { } room && evidence.RecordTitles.ContainsKey((x.Kind, id)) ? RecordTitle(x.Kind, id, room)
                    : new(new(x.Owner), x.Kind, x.RecordId ?? default),
                x.Failure.Cause, (x.RoomId is { } owner ? new[] { owner } : []).Concat(x.RelatedRooms ?? []))).ToArray();
            return row with { Status = findings.Length == 0 ? MapCheckStatus.Pass : MapCheckStatus.Fail,
                Report = findings.Length == 0 ? null : GroupFindings(findings) };
        }).ToArray();
    }
    internal MapCheck[] RuleChecks(IReadOnlyList<PublishingDocuments.RuleIssue> issues) =>
        new[]
        {
            new MapCheck("Rule violations", "Missing Combat Requirements", "A boss or gauntlet check doesn’t include Combat Requirements."),
            new MapCheck("Rule violations", "Incorrect Pogo Difficulty", "A pogo-off requirement doesn’t use Intended difficulty for an enemy designed for pogoing.")
        }.Select((row, index) =>
        {
            var findings = issues.Where(x => x.Combat == (index == 0)).Select(x =>
                Finding(evidence, RecordTitle(x.Kind, x.RecordId, x.RoomId), x.Reason, [x.RoomId])).ToArray();
            return row with { Status = findings.Length == 0 ? MapCheckStatus.Pass : MapCheckStatus.Fail,
                Report = findings.Length == 0 ? null : GroupFindings(findings) };
        }).ToArray();

    private readonly record struct DisplayTitle(string Exact, string? Compact = null);
    private readonly record struct FindingIdentity(string Kind, Guid RecordId = default, string? GraphId = null);
    private readonly record struct FindingTitle(DisplayTitle Label, string Kind, Guid RecordId = default, string? GraphId = null)
    {
        internal FindingIdentity? Identity => RecordId != Guid.Empty || !string.IsNullOrEmpty(GraphId)
            ? new(Kind, RecordId, GraphId) : null;
    }
    private sealed record FindingEvidence(FindingTitle Title, string Detail, IReadOnlyList<MapFindingRoom> Rooms);
    private sealed record RecordEvidence(DisplayTitle Title, MapFindingRoom[] Rooms, FindingEvidence[] Reasons);
    private sealed record Location(PreparedGraphRoom Room, RoomGraphLocation Row, FindingTitle Title);
    private sealed record Route(string From, string To, string Label, string[][] Clauses, bool Unreadable);
    private readonly record struct Fact(bool IsLocation, string Id);
    private sealed record Production(Fact Output, Fact[] Inputs, Route? Route = null);

    private static Fact Node(string id) => new(false, id);
    private static Fact Check(string id) => new(true, id);

    internal void Run(Action<int, MapCheckStatus, MapFindingReport?, string?> publish, CancellationToken cancellationToken)
    {
        var locations = evidence.Rooms.SelectMany(r => r.Room.Locations.Select((l, i) =>
            new Location(r, l, RecordTitle("location", r.LocationRecordIds[i], r.RoomRecordId)))).ToArray();
        var identities = locations.Where(l => l.Row.Id.Length != 0).ToLookup(l => l.Row.Id, StringComparer.Ordinal);
        var findings = new List<FindingEvidence>();
        void Add(FindingTitle title, string detail, Guid owner, IEnumerable<Guid>? related = null) =>
            findings.Add(Finding(evidence, title, detail, related is null ? [owner] : related.Prepend(owner)));
        static IEnumerable<Guid>? Related(Guid? id) => id is { } value ? [value] : null;
        void Begin(int row) { findings.Clear(); publish(row, MapCheckStatus.Checking, null, null); }
        void End(int row, string? unavailable = null) => publish(row,
            findings.Count == 0 && unavailable is null ? MapCheckStatus.Pass : MapCheckStatus.Fail,
            findings.Count == 0 ? null : GroupFindings(findings), unavailable);

        Begin(0);
        foreach (var r in evidence.References.Where(r => r.Kind == PreparedReferenceKind.Node && r.HardFailure is null))
            Add(RecordTitle(r.RecordKind, r.RecordId, r.RoomRecordId),
                r is { Kind: PreparedReferenceKind.Node, Problem: PreparedReferenceProblem.Blank, RecordKind: "location", Field: "subroom" }
                    ? "Subroom is blank." : $"{r.Field}: {r.Requested ?? "(blank)"}. {r.Cause}.", r.RoomRecordId, r.RelatedRooms);
        End(0);
        Begin(1);
        foreach (var rule in evidence.Rules)
        {
            foreach (var failure in rule.Evidence.MissingChecks.DistinctBy(f => (f.Problem, f.Operand, f.Cause)))
                Add(RecordTitle(rule.Kind, rule.RecordId, rule.RoomRecordId), $"Operand: ‘{failure.Operand}’. {failure.Cause}", rule.RoomRecordId, Related(failure.RelatedRoom));
            foreach (var id in rule.Evidence.ResolvedIds.Order(StringComparer.Ordinal))
                if (!identities.Contains(id))
                    Add(RecordTitle(rule.Kind, rule.RecordId, rule.RoomRecordId), $"Exact prerequisite '{id}' does not exist.", rule.RoomRecordId);
        }
        End(1, evidence.TranslationAvailable ? null : NoTranslation);

        Begin(2);
        foreach (var rule in evidence.Rules)
        {
            foreach (var reason in rule.Evidence.UnreadableReasons.DistinctBy(r => (r.Cause, r.CheckOperand)))
                Add(RecordTitle(rule.Kind, rule.RecordId, rule.RoomRecordId), reason.CheckOperand is { } operand
                    ? $"Operand: {(operand.Length == 0 ? "(blank)" : $"‘{operand}’")}. {reason.Cause}"
                    : reason.Cause, rule.RoomRecordId, Related(reason.RelatedRoom));
            foreach (var id in rule.Evidence.ResolvedIds.Order(StringComparer.Ordinal))
                if (identities[id].Count() > 1)
                    Add(RecordTitle(rule.Kind, rule.RecordId, rule.RoomRecordId), $"Exact prerequisite '{id}' is not unique.", rule.RoomRecordId, identities[id].Select(l => l.Room.RoomRecordId));
        }
        End(2, evidence.TranslationAvailable ? null : NoTranslation);

        Begin(3);
        var seeds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spawn in locations.Where(l => l.Row.LocationType == "spawn"))
        {
            IEnumerable<Guid>? related = null;
            var reason = spawn.Room.IsPlayable != true ? "Its room is not in an authoritative playable area."
                : !spawn.Room.CoverageNodes.Contains(spawn.Row.NodeId) ? "Its assigned node is not a resolved playable node."
                : spawn.Row.LogicStatus != "active" ? $"Its rule is unresolved: {string.Join("; ", spawn.Row.Requirement.UnresolvedReasons)}."
                : !spawn.Row.Requirement.Dnf.Any(c => c.Count == 0) ? "Its rule has no unconditional alternative."
                : null;
            if (reason is null && (spawn.Row.Id.Length == 0 || identities[spawn.Row.Id].Count() != 1))
            {
                reason = "Its location identity is invalid or ambiguous.";
                related = identities[spawn.Row.Id].Select(l => l.Room.RoomRecordId);
            }
            if (reason is null) seeds.Add(spawn.Row.NodeId);
            else Add(spawn.Title, $"Invalid Spawn. {reason}", spawn.Room.RoomRecordId, related);
        }
        var unavailable = evidence.Document is null ? Rejected : seeds.Count == 0 ? NoStarts : null;
        var graph = evidence.Document is null ? null : new GraphIndex(evidence, locations, identities, cancellationToken);
        if (unavailable is null) graph!.FindOrphans(seeds, findings);
        End(3, unavailable);

        Begin(4);
        if (unavailable is null) graph!.FindIslands(seeds, findings);
        End(4, unavailable);

        Begin(5);
        var selfLocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loc in locations)
        {
            var row = loc.Row;
            if (loc.Room.IsPlayable != true || row.Id.Length == 0 || identities[row.Id].Count() != 1 || row.Requirement.Dnf.Count == 0) continue;
            var alternatives = row.Requirement.Dnf.Where(IsPossible).ToArray();
            if (alternatives.Length != 0 && alternatives.All(c => c.Contains(DependencyPrefix + row.Id, StringComparer.Ordinal)))
            {
                selfLocked.Add(row.Id);
                Add(loc.Title, "Every activation alternative requires this exact location to be acquired first.", loc.Room.RoomRecordId);
            }
        }
        if (unavailable is null) graph!.FindApproachLocks(seeds, selfLocked, findings);
        End(5, unavailable is null ? null : $"Approach analysis could not run: {unavailable}" +
            (!evidence.TranslationAvailable ? " Self-activation could not run: the shared requirement catalogue is invalid." : ""));

        Begin(6);
        if (unavailable is null) graph!.FindProgressionLocks(seeds, selfLocked, findings);
        End(6, unavailable);
    }

    private FindingTitle RecordTitle(string kind, Guid id, Guid owner)
    {
        var key = (kind, id);
        var known = evidence.RecordTitles.TryGetValue(key, out var compact);
        var exact = kind == "location"
            ? evidence.RoomIdentities.TryGetValue(owner, out var room) ? $"{room.Label} / location {compact ?? "Check location"}" : compact ?? "Check location"
            : evidence.RecordLabels.GetValueOrDefault(key, kind);
        return new(new(exact, compact), kind, known ? id : default);
    }

    private static FindingEvidence Finding(PreparedGraphEvidence evidence, FindingTitle title, string detail, IEnumerable<Guid> rooms)
    {
        var links = new List<MapFindingRoom>();
        foreach (var id in rooms.Distinct())
            if (evidence.RoomIdentities.TryGetValue(id, out var room)) links.Add(new(id, room.Label));
        return new(title, detail, links.ToArray());
    }

    // Freeze the view once on row completion. Place a record by the union of
    // its affected rooms, but keep each original reason and its own links.
    private MapFindingReport GroupFindings(IReadOnlyList<FindingEvidence> findings)
    {
        PreparedGraphRoomGroup? Group(MapFindingRoom room) => evidence.RoomIdentities[room.Id].Group;

        MapFindingZoneKey ZoneKey(RecordEvidence record)
        {
            if (record.Rooms.Length == 0) return new(MapFindingZoneKind.NoRoom);
            var group = Group(record.Rooms[0]);
            for (var i = 1; i < record.Rooms.Length; i++)
                if (Group(record.Rooms[i])?.Id != group?.Id) return new(MapFindingZoneKind.Multiple);
            return group is null ? new(MapFindingZoneKind.Ungrouped) : new(MapFindingZoneKind.Authored, group.Id);
        }

        const string multipleRooms = "Multiple rooms";
        const string noRoom = "No identified room";
        var records = findings.Select((reason, index) => (Reason: reason, Index: index))
            .GroupBy(entry => (entry.Reason.Title.Identity, Unknown: entry.Reason.Title.Identity is null ? entry.Index : -1))
            .Select(group =>
            {
                var reasons = group.Select(entry => entry.Reason).ToArray();
                return new RecordEvidence(reasons[0].Title.Label, reasons.SelectMany(r => r.Rooms).DistinctBy(r => r.Id).ToArray(), reasons);
            });

        var zones = records.GroupBy(ZoneKey).OrderBy(zone => zone.Key.Kind == MapFindingZoneKind.Multiple).Select(zone =>
        {
            var label = zone.Key.Kind switch
            {
                MapFindingZoneKind.Authored => Group(zone.First().Rooms[0])!.Name,
                MapFindingZoneKind.Ungrouped => "Ungrouped",
                MapFindingZoneKind.Multiple => "Multiple zones",
                _ => "No identified zone"
            };
            var sections = zone.GroupBy(r => r.Rooms.Length == 1 ? r.Rooms[0].Id : (Guid?)null).ToArray();
            var headings = AssignDisplayTitles(sections.Where(room => room.Key.HasValue), room =>
            {
                var identity = evidence.RoomIdentities[room.Key!.Value];
                return new(identity.Label, identity.Name);
            }, [multipleRooms, noRoom]);
            var rooms = sections.Select(room =>
            {
                var titles = AssignDisplayTitles(room, record => room.Key is not null ? record.Title : new(record.Title.Exact));
                var heading = room.Key.HasValue ? headings[room] : zone.Key.Kind == MapFindingZoneKind.NoRoom ? noRoom : multipleRooms;
                var fullRoomLabel = room.Key is { } id ? evidence.RoomIdentities[id].Label : null;
                return new MapFindingRoomSection(room.Key, heading, fullRoomLabel, room.Sum(r => r.Reasons.Length), room.Select(record =>
                    new MapFindingRecord(titles[record], record.Reasons.Select(r => new MapFindingReason(r.Detail, r.Rooms)).ToArray())).ToArray());
            }).ToArray();
            return new MapFindingZone(zone.Key, label, rooms.Sum(room => room.Count), rooms);
        }).ToArray();
        return new(findings.Count, zones);
    }

    private static Dictionary<TKey, string> AssignDisplayTitles<TKey>(IEnumerable<TKey> keys, Func<TKey, DisplayTitle> label,
        IReadOnlyList<string>? reserved = null)
        where TKey : notnull
    {
        var entries = keys.Select(key => (Key: key, Title: label(key))).ToArray();
        // Reserve both forms, including literal suffixes and labels of entries that
        // themselves need a fallback. Several reasons for one record share its title.
        var claims = new Dictionary<string, int>(StringComparer.Ordinal);
        var fixedTitles = (reserved ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var title in fixedTitles) Claim(title);
        foreach (var (_, title) in entries)
        {
            Claim(title.Exact);
            if (!string.IsNullOrWhiteSpace(title.Compact) && title.Compact != title.Exact) Claim(title.Compact);
        }
        var bases = entries.ToLookup(x => x.Title.Compact is { } compact && claims.GetValueOrDefault(compact) == 1
            ? compact : x.Title.Exact, x => x.Key, StringComparer.Ordinal);
        var taken = claims.Keys.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<TKey, string>();
        foreach (var group in bases)
        {
            var ordinal = 0;
            var needsOrdinal = fixedTitles.Contains(group.Key) || group.Skip(1).Any();
            foreach (var key in group)
            {
                var text = group.Key;
                if (needsOrdinal)
                {
                    do text = $"{group.Key} (#{++ordinal})";
                    while (!taken.Add(text));
                }
                result.Add(key, text);
            }
        }
        return result;

        void Claim(string text) => claims[text] = claims.GetValueOrDefault(text) + 1;
    }

    private sealed class GraphIndex
    {
        private Dictionary<string, (FindingTitle Title, Guid RoomId)> Nodes { get; } = new(StringComparer.Ordinal);
        private readonly PreparedGraphEvidence evidence;
        private readonly CancellationToken cancellationToken;
        private Dictionary<string, Location> ExecutableLocations { get; } = new(StringComparer.Ordinal);
        private readonly List<Route> routes = [];
        private ILookup<string, Route> Outgoing { get; }
        private readonly ILookup<string, Route> incoming;
        private readonly HashSet<string> coverageNodes = new(StringComparer.Ordinal);
        private readonly ILookup<string, string> coverageIncoming;
        private readonly ILookup<string, string> coverageOutgoing;
        private readonly string[] approachDependencies;
        private readonly Dictionary<Route, string[][]> approachClauses;
        private readonly Dictionary<string, string[][]> activation = new(StringComparer.Ordinal);
        private readonly Production[] readableRules;
        private readonly Production[] possibleRules;
        private Dictionary<Fact, Production?>? possible;

        internal GraphIndex(PreparedGraphEvidence evidence, IReadOnlyList<Location> locations, ILookup<string, Location> identities,
            CancellationToken cancellationToken)
        {
            this.evidence = evidence;
            this.cancellationToken = cancellationToken;
            // A known area is either physical or virtual. Virtual nodes are
            // coverage connectors only; Nodes remains the physical index.
            var admitted = evidence.Rooms.Where(r => r.IsPlayable.HasValue).ToArray();
            foreach (var room in admitted)
                foreach (var n in room.Room.Nodes.Where(n => room.CoverageNodes.Contains(n.Id)))
                {
                    coverageNodes.Add(n.Id);
                    if (room.IsPlayable == true)
                        Nodes.Add(n.Id, (new(new($"{room.Room.Name} / {n.Name} ({n.Id})", n.Name), "node", GraphId: n.Id), room.RoomRecordId));
                }
            var coverageLinks = new List<(string From, string To)>();
            var ports = admitted.SelectMany(r => r.Room.Transitions).ToDictionary(t => t.Id, StringComparer.Ordinal);
            foreach (var room in admitted)
            {
                foreach (var c in room.Room.Connections)
                    Add(c.SourceNodeId, c.TargetNodeId, room, $"connection {c.Name} ({c.Alias})", c.Requirement);
                foreach (var t in room.Room.Transitions)
                    if (t.Target is { } id && ports.TryGetValue(id, out var target))
                        Add(t.SourceNodeId, target.SourceNodeId, room, $"transition {t.Name} ({t.Alias})", t.Requirement);
            }
            foreach (var loc in locations.Where(l => l.Row.LogicStatus == "active" && Nodes.ContainsKey(l.Row.NodeId)))
            {
                var clauses = Clauses(loc.Row.Requirement);
                if (clauses.Length == 0) continue;
                ExecutableLocations.Add(loc.Row.Id, loc);
                activation.Add(loc.Row.Id, clauses);
            }
            Outgoing = routes.ToLookup(r => r.From, StringComparer.Ordinal);
            incoming = routes.ToLookup(r => r.To, StringComparer.Ordinal);
            coverageIncoming = coverageLinks.ToLookup(r => r.To, r => r.From, StringComparer.Ordinal);
            coverageOutgoing = coverageLinks.ToLookup(r => r.From, r => r.To, StringComparer.Ordinal);

            // A direct, readable proof of impossibility only. Do not derive it
            // from bad placement, unresolved rules, scope, or dependency cycles.
            var impossibleLocations = locations.Where(l => l.Room.IsPlayable == true && l.Room.CoverageNodes.Contains(l.Row.NodeId) &&
                l.Row.Id.Length != 0 && identities[l.Row.Id].Count() == 1 && l.Row.LogicStatus == "active" &&
                l.Row.Requirement.Dnf.Count != 0 && !l.Row.Requirement.Dnf.Any(IsPossible)).Select(l => l.Row.Id).ToHashSet(StringComparer.Ordinal);
            approachClauses = routes.ToDictionary(r => r, r => impossibleLocations.Count == 0 ? r.Clauses
                : r.Clauses.Where(c => !c.Any(impossibleLocations.Contains)).ToArray());
            approachDependencies = approachClauses.Values.SelectMany(a => a).SelectMany(c => c)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            readableRules = routes.SelectMany(r => Make(Node(r.To), Node(r.From), r.Clauses, r))
                .Concat(ExecutableLocations.Values.SelectMany(l => Make(Check(l.Row.Id), Node(l.Row.NodeId), activation[l.Row.Id]))).ToArray();
            possibleRules = [.. readableRules, .. routes.Where(r => r.Unreadable).Select(r => new Production(Node(r.To), [Node(r.From)]))];

            void Add(string from, string to, PreparedGraphRoom owner, string rule, RoomGraphRequirement requirement)
            {
                // Coverage counts resolved topology unless the canonical rule rules it out.
                if (coverageNodes.Contains(from) && coverageNodes.Contains(to) &&
                    (requirement.Mode == "unresolved" || requirement.Dnf.Any(IsPossible)))
                    coverageLinks.Add((from, to));
                // Executable clauses and progression walks retain physical endpoints only.
                if (Nodes.TryGetValue(from, out var source) && Nodes.TryGetValue(to, out var destination))
                {
                    var roomName = evidence.RoomIdentities[owner.RoomRecordId].Label;
                    var targetRoom = source.RoomId == destination.RoomId ? "" : evidence.RoomIdentities[destination.RoomId].Label + " / ";
                    var label = $"{roomName} / {rule} ({source.Title.Label.Compact} → {targetRoom}{destination.Title.Label.Compact})";
                    routes.Add(new(from, to, label, Clauses(requirement), requirement.Mode == "unresolved"));
                }
            }
            string[][] Clauses(RoomGraphRequirement rule) => rule.Dnf.Where(IsPossible).Select(c => c
                .Where(a => a.StartsWith(DependencyPrefix, StringComparison.Ordinal)).Select(a => a[DependencyPrefix.Length..])
                .Distinct(StringComparer.Ordinal).ToArray()).Where(c => c.All(id => identities[id].Count() == 1)).ToArray();
            // Empty DNF and impassable clauses contribute no executable alternative.
            // Other non-dependency atoms are optimistic, not modeled completions.

            static IEnumerable<Production> Make(Fact output, Fact source, string[][] clauses, Route? route = null) =>
                clauses.Select(c => new Production(output, [source, .. c.Select(Check)], route));
        }

        internal void FindOrphans(HashSet<string> seeds, List<FindingEvidence> findings)
        {
            foreach (var (id, node) in Nodes)
                if (!seeds.Contains(id) && !coverageIncoming.Contains(id))
                    findings.Add(Finding(evidence, node.Title, "No valid directed route enters this node.", [node.RoomId]));
        }

        internal void FindIslands(HashSet<string> seeds, List<FindingEvidence> findings)
        {
            var reached = new HashSet<string>(seeds, StringComparer.Ordinal);
            var queue = new Queue<string>(seeds);
            while (queue.TryDequeue(out var reachedNode))
                foreach (var target in coverageOutgoing[reachedNode])
                    if (reached.Add(target)) queue.Enqueue(target);

            // Weak components can include virtual connectors, but neither the
            // island title nor its room links include them.
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in coverageNodes)
            {
                if (reached.Contains(node) || !visited.Add(node)) continue;
                var group = new List<string>();
                var size = 0;
                queue.Enqueue(node);
                while (queue.TryDequeue(out var current))
                {
                    size++;
                    if (Nodes.ContainsKey(current)) group.Add(current);
                    foreach (var target in coverageOutgoing[current]) Link(target);
                    foreach (var source in coverageIncoming[current]) Link(source);
                }
                // A multi-node weak component has an internal link by construction.
                if (group.Count == 0 || (size == 1 && !coverageIncoming[node].Contains(node, StringComparer.Ordinal))) continue;

                var title = new FindingTitle(new(string.Join("; ", group.Select(n => Nodes[n].Title.Label.Exact))), "island", GraphId: group[0]);
                findings.Add(Finding(evidence, title, "This linked group cannot be entered from any Spawn, even with locks ignored.",
                    group.Select(n => Nodes[n].RoomId)));
            }

            void Link(string other)
            {
                if (!reached.Contains(other) && visited.Add(other)) queue.Enqueue(other);
            }
        }

        internal void FindApproachLocks(HashSet<string> seeds, HashSet<string> selfLocked, List<FindingEvidence> findings)
        {
            var baseline = Walk(seeds, route => approachClauses[route].Length != 0);
            var hasUnreadableRoutes = routes.Any(r => r.Unreadable);
            foreach (var id in approachDependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selfLocked.Contains(id) || !ExecutableLocations.TryGetValue(id, out var loc) || !baseline.Contains(loc.Row.NodeId))
                    continue;
                bool Bypasses(Route route) => approachClauses[route].Any(c => !c.Contains(id, StringComparer.Ordinal));
                var readable = Walk(seeds, Bypasses);
                // Preserve the optimistic *readable* approach comparison: a
                // readable alternate gate may instead be a mutual lock. For
                // an unreadable detour, require a realizable upper acquisition
                // of each check on its remaining gates. In a least closure A
                // cannot help reach A's own placement before it is reached.
                if (readable.Contains(loc.Row.NodeId) ||
                    (hasUnreadableRoutes && Possible(seeds).ContainsKey(Node(loc.Row.NodeId))))
                    continue;

                // Find readable crossings into the region from which this
                // check can be approached. Other gates on the same frontier
                // are not evidence about this placement.
                var leadsToCheck = new HashSet<string>(StringComparer.Ordinal) { loc.Row.NodeId };
                var pending = new Queue<string>(leadsToCheck);
                var gates = new List<Route>();
                while (pending.TryDequeue(out var node))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var route in incoming[node])
                    {
                        if (approachClauses[route].Length == 0) continue;
                        if (readable.Contains(route.From)) gates.Add(route);
                        else if (leadsToCheck.Add(route.From)) pending.Enqueue(route.From);
                    }
                }
                selfLocked.Add(id);
                findings.Add(Finding(evidence, loc.Title, $"‘{loc.Title.Label.Exact}’ is behind its own gate. Blocking rules: {string.Join("; ", gates.Select(r => r.Label).Order(StringComparer.Ordinal))}.",
                    RouteRooms(gates).Prepend(loc.Room.RoomRecordId)));
            }
        }

        private HashSet<string> Walk(IEnumerable<string> seeds, Func<Route, bool> allowed)
        {
            var reached = seeds.ToHashSet(StringComparer.Ordinal);
            var queue = new Queue<string>(reached);
            while (queue.TryDequeue(out var node))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var r in Outgoing[node])
                    if (allowed(r) && reached.Add(r.To)) queue.Enqueue(r.To);
            }
            return reached;
        }

        internal void FindProgressionLocks(HashSet<string> seeds, HashSet<string> selfLocked, List<FindingEvidence> findings)
        {
            var confirmed = new WalkIndex(readableRules, cancellationToken).Run(seeds.Select(Node));
            var upper = Possible(seeds);
            var viable = ClassifyViable(seeds);
            var candidates = viable.Where(id => !selfLocked.Contains(id) && !upper.ContainsKey(Check(id)))
                .ToHashSet(StringComparer.Ordinal);
            if (candidates.Count < 2) return;

            bool AvailableInput(Fact fact) => !fact.IsLocation || viable.Contains(fact.Id);
            var upperRules = possibleRules.Where(r => r.Inputs.All(AvailableInput)).ToLookup(r => r.Output);
            var knownRules = readableRules.Where(r => r.Inputs.All(AvailableInput)).ToLookup(r => r.Output);
            // References (including possible detours) only bound the searches.
            // They say nothing about which alternatives hold or how many repairs
            // a component needs.
            var edges = upperRules.SelectMany(g => g).Where(r => !upper.ContainsKey(r.Output))
                .SelectMany(r => r.Inputs.Where(f => !upper.ContainsKey(f) && !(f.IsLocation && selfLocked.Contains(f.Id)))
                    .Select(input => (r.Output, input))).ToArray();
            foreach (var component in Components(edges))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var checks = component.Where(f => f.IsLocation && candidates.Contains(f.Id))
                    .Select(f => f.Id).Order(StringComparer.Ordinal).ToArray();
                if (checks.Length < 2) continue;
                var local = checks.ToHashSet(StringComparer.Ordinal);
                var tryUpper = BuildProbe(checks, local, upperRules, upper);
                var tryReadable = BuildProbe(checks, local, knownRules, confirmed);
                FindObligations(checks, tryUpper, tryReadable, findings);
            }
        }

        private Dictionary<Fact, Production?> Possible(IEnumerable<string> seeds) =>
            possible ??= new WalkIndex(possibleRules, cancellationToken).Run(seeds.Select(Node));

        private HashSet<string> ClassifyViable(IEnumerable<string> seeds)
        {
            // Upper limit on conditional *inputs*, not on confirmed acquisitions.
            // Remove checks with no supported placement and full activation,
            // even when every remaining check is hypothetically supplied.
            var inputs = ExecutableLocations.Keys.ToHashSet(StringComparer.Ordinal);
            var approach = new WalkIndex(possibleRules.Where(r => !r.Output.IsLocation), cancellationToken);
            while (inputs.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reached = approach.Run(seeds.Select(Node).Concat(inputs.Select(Check)));
                if (inputs.RemoveWhere(id => !reached.ContainsKey(Node(ExecutableLocations[id].Row.NodeId)) ||
                    !activation[id].Any(c => c.All(dep => reached.ContainsKey(Check(dep))))) == 0) break;
            }
            return inputs;
        }

        private delegate Dictionary<Fact, Production?> Probe(HashSet<string> withhold, HashSet<string>? supply);

        private Probe BuildProbe(string[] checks, HashSet<string> local,
            ILookup<Fact, Production> producers, Dictionary<Fact, Production?> known)
        {
            // Only visit producers on the backward slice of candidate checks.
            // Supplied external inputs and facts already reached need no producers.
            var rules = new List<Production>();
            var seen = new HashSet<Fact>();
            var pending = new Stack<Fact>(checks.Select(Check));
            while (pending.TryPop(out var fact))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(fact) || known.ContainsKey(fact) || (fact.IsLocation && !local.Contains(fact.Id))) continue;
                foreach (var rule in producers[fact])
                {
                    rules.Add(rule);
                    foreach (var input in rule.Inputs) pending.Push(input);
                }
            }
            var knownFacts = seen.Where(known.ContainsKey).ToArray();
            var inputs = seen.Where(f => f.IsLocation && !known.ContainsKey(f)).ToArray();
            var walk = new WalkIndex(rules, cancellationToken);
            return (withhold, supply) => walk.Run(knownFacts.Concat(inputs.Where(f => !withhold.Contains(f.Id) || supply?.Contains(f.Id) == true)));
        }

        private sealed class WalkIndex
        {
            private readonly Production[] rules;
            private readonly ILookup<Fact, int> watches;
            private readonly CancellationToken token;

            internal WalkIndex(IEnumerable<Production> rules, CancellationToken token)
            {
                this.rules = rules.ToArray();
                watches = this.rules.SelectMany((rule, i) => rule.Inputs.Select(input => (input, i))).ToLookup(x => x.input, x => x.i);
                this.token = token;
            }

            internal Dictionary<Fact, Production?> Run(IEnumerable<Fact> seeds)
            {
                token.ThrowIfCancellationRequested();
                var reached = new Dictionary<Fact, Production?>();
                var waiting = rules.Select(r => r.Inputs.Length).ToArray();
                var queue = new Queue<Fact>();
                foreach (var fact in seeds) Learn(fact, null);
                while (queue.TryDequeue(out var fact))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var i in watches[fact])
                        if (--waiting[i] == 0) Learn(rules[i].Output, rules[i]);
                }
                return reached;

                // Keep one real predecessor, not the combinations of paths
                // that could produce the same fact.
                void Learn(Fact fact, Production? production)
                {
                    if (reached.TryAdd(fact, production)) queue.Enqueue(fact);
                }
            }
        }

        private IEnumerable<Guid> RouteRooms(IEnumerable<Route> routes)
        {
            foreach (var route in routes)
            {
                yield return Nodes[route.From].RoomId;
                yield return Nodes[route.To].RoomId;
            }
        }

        private void FindObligations(string[] checks, Probe tryUpper, Probe tryReadable, List<FindingEvidence> findings)
        {
            var held = checks.ToHashSet(StringComparer.Ordinal);
            while (held.Count >= 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                held.ExceptWith(ReachedChecks(tryUpper(held, null)));
                if (held.Count < 2) return;
                var anchors = held.Order(StringComparer.Ordinal).Select(id =>
                {
                    var followers = ReachedChecks(tryUpper(held, new(StringComparer.Ordinal) { id })).Where(held.Contains)
                        .ToHashSet(StringComparer.Ordinal);
                    return (Id: id, Followers: followers);
                }).OrderByDescending(t => t.Followers.Count).ToArray();

                var reported = false;
                foreach (var (anchor, followers) in anchors)
                {
                    if (followers.Count < 2) break;
                    var cut = held.ToHashSet(StringComparer.Ordinal);
                    // Try releasing other obligations first, before the checks
                    // this anchor itself would release. Keep a grant only when
                    // the anchor is still blocked; remove all newly freed checks.
                    // The resulting cut is closed under every full upper rule.
                    foreach (var id in held.Where(id => id != anchor).OrderBy(followers.Contains).ThenBy(id => id, StringComparer.Ordinal))
                    {
                        if (!cut.Remove(id)) continue;
                        var after = tryUpper(cut, null);
                        if (after.ContainsKey(Check(anchor))) cut.Add(id);
                        else cut.ExceptWith(ReachedChecks(after));
                    }
                    if (cut.Count < 2 || !cut.IsSubsetOf(followers)) continue;
                    var witnesses = ReadableWitnesses(cut, tryReadable);
                    if (witnesses.Count < 2 || !witnesses.ContainsKey(anchor)) continue;

                    Report(witnesses, findings);
                    // A single conditional release really discharges this cut.
                    // Do not grant its followers separately: those which still
                    // need a different intervention must remain withheld.
                    held.Remove(anchor);
                    reported = true;
                    break;
                }
                if (reported) continue;

                // Do not split a joint AND/OR obligation merely because it
                // contains smaller reference cycles. Where no single release
                // separates a readable cut, prove the remaining set together.
                var joint = ReadableWitnesses(held, tryReadable);
                if (joint.Count >= 2) Report(joint, findings);
                return;
            }

            static IEnumerable<string> ReachedChecks(Dictionary<Fact, Production?> reached) =>
                reached.Keys.Where(f => f.IsLocation).Select(f => f.Id);
        }

        private sealed record Witness(HashSet<string> Checks, HashSet<Route> Gates, HashSet<string> OtherChecks);

        private Dictionary<string, Witness> ReadableWitnesses(HashSet<string> cut, Probe tryReadable)
        {
            // An unreadable-only member may keep an upper alternative closed,
            // but cannot be borrowed to manufacture readable circular evidence.
            // Prune members that cannot actually produce themselves readably
            // under the other still-supported hypotheses. Outside inputs have
            // already passed viability; they are not claims of acquisition.
            var supported = cut.ToHashSet(StringComparer.Ordinal);
            while (supported.Count >= 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var found = new Dictionary<string, Witness>(StringComparer.Ordinal);
                foreach (var id in supported.Order(StringComparer.Ordinal))
                {
                    var granted = supported.Where(other => other != id).ToHashSet(StringComparer.Ordinal);
                    var read = tryReadable(cut, granted);
                    if (read.TryGetValue(Check(id), out var producer) && producer is not null)
                        found.Add(id, Trace(id, read, granted));
                }
                if (found.Count == supported.Count)
                {
                    foreach (var (id, witness) in found)
                    {
                        // Show other readable arms when they can avoid a
                        // dependency of the first. Keep only their related
                        // checks and gates, never their combinations.
                        var granted = supported.Where(other => other != id).ToHashSet(StringComparer.Ordinal);
                        foreach (var dependency in witness.Checks.Order(StringComparer.Ordinal))
                        {
                            granted.Remove(dependency);
                            var alternate = tryReadable(cut, granted);
                            if (alternate.TryGetValue(Check(id), out var producer) && producer is not null)
                            {
                                var arm = Trace(id, alternate, granted);
                                witness.Gates.UnionWith(arm.Gates);
                                witness.OtherChecks.UnionWith(arm.Checks.Where(check => !witness.Checks.Contains(check)));
                            }
                            granted.Add(dependency);
                        }
                    }
                    return found;
                }
                supported.IntersectWith(found.Keys);
            }
            return new(StringComparer.Ordinal);
        }

        private Witness Trace(string id, Dictionary<Fact, Production?> read, HashSet<string> granted)
        {
            var seen = new HashSet<Fact>();
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            var gates = new HashSet<Route>();
            var pending = new Stack<Fact>();
            pending.Push(Check(id));
            while (pending.TryPop(out var fact))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(fact)) continue;
                if (fact.IsLocation && granted.Contains(fact.Id)) dependencies.Add(fact.Id);
                if (read[fact] is not { } production) continue;
                if (production.Route is { } route && production.Inputs.Any(input => input.IsLocation && granted.Contains(input.Id)))
                    gates.Add(route);
                foreach (var input in production.Inputs) pending.Push(input);
            }
            return new(dependencies, gates, new(StringComparer.Ordinal));
        }

        private void Report(Dictionary<string, Witness> witnesses, List<FindingEvidence> findings)
        {
            var ordered = witnesses.Keys.Order(StringComparer.Ordinal).ToArray();
            var labels = AssignDisplayTitles(ordered, id => new(ExecutableLocations[id].Title.Label.Exact));
            var title = new FindingTitle(new($"Progression loop: {labels[ordered[0]]}"), "deadlock", GraphId: ordered[0]);
            var gates = ordered.SelectMany(id => witnesses[id].Gates).Distinct().OrderBy(r => r.Label, StringComparer.Ordinal).ToArray();
            var reason = "None of these checks can happen first. Readable loop steps:\n" +
                string.Join("\n", ordered.Select(id =>
                {
                    var witness = witnesses[id];
                    var step = $"‘{labels[id]}’ needs {Names(witness.Checks, " AND ")} on one readable approach or activation.";
                    return witness.OtherChecks.Count == 0 ? step
                        : $"{step} Other readable alternatives involve {Names(witness.OtherChecks, "; ")}.";
                }));
            if (gates.Length != 0)
                reason += "\nReadable gate evidence: " + string.Join("; ", gates.Select(r => r.Label)) + ".";
            findings.Add(Finding(evidence, title, reason,
                ordered.Select(id => ExecutableLocations[id].Room.RoomRecordId).Concat(RouteRooms(gates))));

            string Names(IEnumerable<string> ids, string separator) =>
                string.Join(separator, ids.Order(StringComparer.Ordinal).Select(id => $"‘{labels[id]}’"));
        }

        private IEnumerable<List<T>> Components<T>(IEnumerable<(T From, T To)> edges) where T : notnull
        {
            var pairs = edges.ToArray();
            var forward = pairs.ToLookup(e => e.From, e => e.To);
            var reverse = pairs.ToLookup(e => e.To, e => e.From);
            var visited = new HashSet<T>();
            var order = new Stack<T>();
            var walk = new Stack<(T Vertex, bool Finished)>();
            foreach (var start in forward.Select(g => g.Key))
            {
                walk.Push((start, false));
                while (walk.TryPop(out var entry))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Finished) order.Push(entry.Vertex);
                    else if (visited.Add(entry.Vertex))
                    {
                        walk.Push((entry.Vertex, true));
                        foreach (var next in forward[entry.Vertex]) walk.Push((next, false));
                    }
                }
            }
            visited.Clear();
            while (order.TryPop(out var start))
            {
                if (!visited.Add(start)) continue;
                var group = new List<T>();
                walk.Push((start, false));
                while (walk.TryPop(out var entry))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    group.Add(entry.Vertex);
                    foreach (var next in reverse[entry.Vertex])
                        if (visited.Add(next)) walk.Push((next, false));
                }
                yield return group;
            }
        }
    }
}
