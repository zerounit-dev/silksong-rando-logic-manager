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
        new("Broken references", "Broken Node Reference", "A route or check points to a node that was deleted or belongs to the wrong room."),
        new("Broken references", "Exit to Nowhere", "A doorway has no valid destination entrance, even if it names the right room."),
        new("Broken references", "Unknown Prerequisite", "A rule asks the player to complete a check that isn’t defined in the map."),
        new("Broken references", "Unreadable Rule", "A route or check has a blank or unreadable requirement, so we can’t tell when it’s usable."),
        new("Missing map data", "Orphaned Node", "We mapped a spot but gave it no incoming link, and it isn’t a starting point or story arrival."),
        new("Missing map data", "Disconnected Island", "A group of spots links together, but there’s no way to reach the group from a starting point or story arrival, even with the locks ignored. The individual spots may all have incoming links, so Orphaned Node would miss them."),
        new("Progression mistakes", "Self-Lock", "A switch is behind its own lock. An alternate route that doesn’t require the switch is enough for this check to pass."),
        new("Progression mistakes", "Progression Deadlock", "The links and prerequisites exist, but nothing in the chain can happen first. The Bell Switch needs the Gate Switch, and the Gate Switch needs the Bell Switch.")
    ];

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
    private sealed record Route(string From, string To, string Label, string[][] Clauses);
    private readonly record struct Fact(bool IsLocation, string Id);
    private sealed class Alternative(Fact output, int remaining)
    {
        internal Fact Output { get; } = output;
        internal int Remaining = remaining;
    }

    internal void Run(Action<int, MapCheckStatus, MapFindingReport?, string?> publish)
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

        for (var i = 0; i < 2; i++)
        {
            Begin(i);
            foreach (var r in evidence.References.Where(r => r.Kind == (i == 0 ? PreparedReferenceKind.Node : PreparedReferenceKind.Port)))
                Add(RecordTitle(r.RecordKind, r.RecordId, r.RoomRecordId),
                    r is { Kind: PreparedReferenceKind.Node, Problem: PreparedReferenceProblem.Blank, RecordKind: "location", Field: "subroom" }
                        ? "Subroom is blank." : $"{r.Field}: {r.Requested ?? "(blank)"}. {r.Cause}.", r.RoomRecordId, r.RelatedRooms);
            End(i);
        }
        Begin(2);
        foreach (var rule in evidence.Rules)
        {
            foreach (var failure in rule.Evidence.MissingChecks.DistinctBy(f => (f.Problem, f.Operand, f.Cause)))
                Add(RecordTitle(rule.Kind, rule.RecordId, rule.RoomRecordId), $"Operand: ‘{failure.Operand}’. {failure.Cause}", rule.RoomRecordId, Related(failure.RelatedRoom));
            foreach (var id in rule.Evidence.ResolvedIds.Order(StringComparer.Ordinal))
                if (!identities.Contains(id))
                    Add(RecordTitle(rule.Kind, rule.RecordId, rule.RoomRecordId), $"Exact prerequisite '{id}' does not exist.", rule.RoomRecordId);
        }
        End(2, evidence.TranslationAvailable ? null : NoTranslation);

        Begin(3);
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
        End(3, evidence.TranslationAvailable ? null : NoTranslation);

        Begin(4);
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
        var graph = evidence.Document is null ? null : new GraphIndex(evidence, locations, identities);
        if (unavailable is null) graph!.FindOrphans(seeds, findings);
        End(4, unavailable);

        Begin(5);
        if (unavailable is null) graph!.FindIslands(seeds, findings);
        End(5, unavailable);

        Begin(6);
        var selfActivating = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loc in locations)
        {
            var row = loc.Row;
            if (loc.Room.IsPlayable != true || row.Id.Length == 0 || identities[row.Id].Count() != 1 || row.Requirement.Dnf.Count == 0) continue;
            var alternatives = row.Requirement.Dnf.Where(IsPossible).ToArray();
            if (alternatives.Length != 0 && alternatives.All(c => c.Contains(DependencyPrefix + row.Id, StringComparer.Ordinal)))
            {
                selfActivating.Add(row.Id);
                Add(loc.Title, "Every activation alternative requires this exact location to be acquired first.", loc.Room.RoomRecordId);
            }
        }
        HashSet<string>? optimistic = null;
        var optimisticTree = new HashSet<Route>();
        if (unavailable is null)
        {
            optimistic = graph!.Walk(seeds, r => r.Clauses.Length != 0, optimisticTree);
            graph.FindApproachLocks(seeds, optimistic, findings);
        }
        End(6, unavailable is null ? null : $"Approach analysis could not run: {unavailable}" +
            (!evidence.TranslationAvailable ? " Self-activation could not run: the shared requirement catalogue is invalid." : ""));

        Begin(7);
        if (unavailable is null) graph!.FindProgressionBlocks(seeds, optimistic!, optimisticTree, selfActivating, findings);
        End(7, unavailable);
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
        internal Dictionary<string, Location> ExecutableLocations { get; } = new(StringComparer.Ordinal);
        private readonly List<Route> routes = [];
        internal ILookup<string, Route> Outgoing { get; }
        private readonly HashSet<string> coverageNodes = new(StringComparer.Ordinal);
        private readonly ILookup<string, string> coverageIncoming;
        private readonly ILookup<string, string> coverageOutgoing;
        private readonly ILookup<string, Route> approachByDependency;
        private readonly Dictionary<Route, string[][]> approachClauses;
        private readonly Dictionary<string, string[][]> activation = new(StringComparer.Ordinal);

        internal GraphIndex(PreparedGraphEvidence evidence, IReadOnlyList<Location> locations, ILookup<string, Location> identities)
        {
            this.evidence = evidence;
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
                    Add(c.SourceNodeId, c.TargetNodeId, $"{room.Room.Name} / {c.Name} ({c.Id})", c.Requirement);
                foreach (var t in room.Room.Transitions)
                    if (t.Target is { } id && ports.TryGetValue(id, out var target))
                        Add(t.SourceNodeId, target.SourceNodeId, $"{room.Room.Name} / {t.Name} ({t.Id})", t.Requirement);
            }
            foreach (var loc in locations.Where(l => l.Row.LogicStatus == "active" && Nodes.ContainsKey(l.Row.NodeId)))
            {
                var clauses = Clauses(loc.Row.Requirement);
                if (clauses.Length == 0) continue;
                ExecutableLocations.Add(loc.Row.Id, loc);
                activation.Add(loc.Row.Id, clauses);
            }
            Outgoing = routes.ToLookup(r => r.From, StringComparer.Ordinal);
            coverageIncoming = coverageLinks.ToLookup(r => r.To, r => r.From, StringComparer.Ordinal);
            coverageOutgoing = coverageLinks.ToLookup(r => r.From, r => r.To, StringComparer.Ordinal);

            // A direct, readable proof of impossibility only. Do not derive it
            // from bad placement, unresolved rules, scope, or dependency cycles.
            var impossibleLocations = locations.Where(l => l.Room.IsPlayable == true && l.Room.CoverageNodes.Contains(l.Row.NodeId) &&
                l.Row.Id.Length != 0 && identities[l.Row.Id].Count() == 1 && l.Row.LogicStatus == "active" &&
                l.Row.Requirement.Dnf.Count != 0 && !l.Row.Requirement.Dnf.Any(IsPossible)).Select(l => l.Row.Id).ToHashSet(StringComparer.Ordinal);
            approachClauses = routes.ToDictionary(r => r, r => impossibleLocations.Count == 0 ? r.Clauses
                : r.Clauses.Where(c => !c.Any(impossibleLocations.Contains)).ToArray());
            approachByDependency = routes.SelectMany(r => approachClauses[r].SelectMany(c => c).Distinct(StringComparer.Ordinal).Select(d => (Id: d, Route: r)))
                .ToLookup(x => x.Id, x => x.Route, StringComparer.Ordinal);

            void Add(string from, string to, string label, RoomGraphRequirement requirement)
            {
                // Coverage counts resolved topology unless the canonical rule rules it out.
                if (coverageNodes.Contains(from) && coverageNodes.Contains(to) &&
                    (requirement.Mode == "unresolved" || requirement.Dnf.Any(IsPossible)))
                    coverageLinks.Add((from, to));
                // Executable clauses and progression walks retain physical endpoints only.
                if (Nodes.ContainsKey(from) && Nodes.ContainsKey(to))
                    routes.Add(new(from, to, label, Clauses(requirement)));
            }
            string[][] Clauses(RoomGraphRequirement rule) => rule.Dnf.Where(IsPossible).Select(c => c
                .Where(a => a.StartsWith(DependencyPrefix, StringComparison.Ordinal)).Select(a => a[DependencyPrefix.Length..])
                .Distinct(StringComparer.Ordinal).ToArray()).Where(c => c.All(id => identities[id].Count() == 1)).ToArray();
            // Empty DNF and impassable clauses contribute no executable alternative.
            // Other non-dependency atoms are optimistic, not modeled completions.
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

        internal void FindApproachLocks(HashSet<string> seeds, HashSet<string> optimistic, List<FindingEvidence> findings)
        {
            var baseline = approachClauses.Any(p => p.Value.Length != p.Key.Clauses.Length)
                ? Walk(seeds, route => approachClauses[route].Length != 0) : optimistic;
            foreach (var routesForCheck in approachByDependency)
            {
                var id = routesForCheck.Key;
                if (!ExecutableLocations.TryGetValue(id, out var loc) || !baseline.Contains(loc.Row.NodeId)) continue;
                bool Bypasses(Route route) => approachClauses[route].Any(c => !c.Contains(id, StringComparer.Ordinal));
                var alternate = Walk(seeds, Bypasses);
                if (!alternate.Contains(loc.Row.NodeId))
                {
                    var boundary = routesForCheck.Where(r => alternate.Contains(r.From) && !alternate.Contains(r.To) && !Bypasses(r)).ToArray();
                    findings.Add(Finding(evidence, loc.Title, $"Its node is reachable only through rules requiring this location. Rules on the reachable boundary: {string.Join("; ", boundary.Select(r => r.Label))}.",
                        RouteRooms(boundary).Prepend(loc.Room.RoomRecordId)));
                }
            }
        }

        internal HashSet<string> Walk(IEnumerable<string> seeds, Func<Route, bool> allowed, HashSet<Route>? tree = null)
        {
            var reached = seeds.ToHashSet(StringComparer.Ordinal);
            var queue = new Queue<string>(reached);
            while (queue.TryDequeue(out var node))
                foreach (var r in Outgoing[node])
                    if (!reached.Contains(r.To) && allowed(r) && reached.Add(r.To))
                    {
                        tree?.Add(r);
                        queue.Enqueue(r.To);
                    }
            return reached;
        }

        internal void FindProgressionBlocks(HashSet<string> seeds, HashSet<string> optimistic, HashSet<Route> optimisticTree,
            HashSet<string> selfActivating, List<FindingEvidence> findings)
        {
            var waiting = new Dictionary<Fact, List<Alternative>>();
            var acquired = new HashSet<Fact>();
            var queue = new Queue<Fact>();
            foreach (var r in routes) Register(new(false, r.From), new(false, r.To), r.Clauses);
            foreach (var l in ExecutableLocations.Values) Register(new(false, l.Row.NodeId), new(true, l.Row.Id), activation[l.Row.Id]);
            foreach (var node in seeds) Learn(new(false, node));
            while (queue.TryDequeue(out var fact))
                if (waiting.TryGetValue(fact, out var alternatives))
                    foreach (var alternative in alternatives)
                        if (--alternative.Remaining == 0) Learn(alternative.Output);

            var viable = ClassifyViable(seeds, optimistic, optimisticTree);
            bool ViableClause(string[] clause) => clause.All(viable.Locations.Contains);
            // Report the reachable frontier and blocked acquisitions there, not
            // every location stranded behind the same gate or missing topology.
            foreach (var group in routes.Where(r => r.Clauses.Any(ViableClause) && acquired.Contains(new(false, r.From)) &&
                viable.Nodes.Contains(r.To) && !acquired.Contains(new(false, r.To))).GroupBy(r => r.To))
                findings.Add(Finding(evidence, Nodes[group.Key].Title, string.Join("\n", group.Select(r => $"{r.Label}: {Unmet(r.Clauses.Where(ViableClause))}")),
                    RouteRooms(group).Concat(UnmetRooms(group.SelectMany(r => r.Clauses.Where(ViableClause)))).Prepend(Nodes[group.Key].RoomId)));
            foreach (var l in ExecutableLocations.Values)
                if (viable.Locations.Contains(l.Row.Id) && !selfActivating.Contains(l.Row.Id) && acquired.Contains(new(false, l.Row.NodeId)) && !acquired.Contains(new(true, l.Row.Id)))
                    findings.Add(Finding(evidence, l.Title, Unmet(activation[l.Row.Id].Where(ViableClause)),
                        UnmetRooms(activation[l.Row.Id].Where(ViableClause)).Prepend(l.Room.RoomRecordId)));

            string Unmet(IEnumerable<string[]> clauses) => "Unmet prerequisites by alternative: " + string.Join(" OR ", clauses.Select(c =>
                "[" + string.Join(" AND ", c.Where(id => !acquired.Contains(new(true, id)))) + "]"));
            IEnumerable<Guid> UnmetRooms(IEnumerable<string[]> clauses) => clauses.SelectMany(c => c)
                .Where(id => !acquired.Contains(new(true, id))).Select(id => ExecutableLocations[id].Room.RoomRecordId);
            void Learn(Fact fact) { if (acquired.Add(fact)) queue.Enqueue(fact); }
            void Register(Fact source, Fact output, string[][] clauses)
            {
                foreach (var clause in clauses)
                {
                    var alternative = new Alternative(output, clause.Length + 1);
                    Watch(source, alternative);
                    foreach (var id in clause) Watch(new(true, id), alternative);
                }
            }
            void Watch(Fact fact, Alternative alternative)
            {
                if (!waiting.TryGetValue(fact, out var list)) waiting.Add(fact, list = []);
                list.Add(alternative);
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

        private (HashSet<string> Nodes, HashSet<string> Locations) ClassifyViable(HashSet<string> seeds, HashSet<string> optimistic, HashSet<Route> tree)
        {
            // Upper bound for reporting only: assume candidates until no rooted
            // placement or supported alternative remains. Cyclic prerequisites may
            // remain candidates, but unavailable facts never enter actual closure.
            var candidates = ExecutableLocations.Keys.ToHashSet(StringComparer.Ordinal);
            var placements = ExecutableLocations.Values.ToLookup(l => l.Row.NodeId, l => l.Row.Id, StringComparer.Ordinal);
            var activationCounts = activation.ToDictionary(p => p.Key, p => p.Value.Length, StringComparer.Ordinal);
            var routeCounts = routes.ToDictionary(r => r, r => r.Clauses.Length);
            var watches = new Dictionary<string, List<AvailabilityClause>>(StringComparer.Ordinal);
            foreach (var (id, clauses) in activation)
                foreach (var clause in clauses) Watch(clause, new(id, null));
            foreach (var route in routes)
                foreach (var clause in route.Clauses) Watch(clause, new(null, route));
            var removed = new Queue<string>();
            foreach (var dependency in watches.Keys)
                if (!candidates.Contains(dependency)) removed.Enqueue(dependency);
            foreach (var l in ExecutableLocations.Values)
                if (!optimistic.Contains(l.Row.NodeId)) Remove(l.Row.Id);

            var rooted = optimistic;
            // Keep a rooted witness tree. Losing a non-tree edge cannot strand a
            // node; rebuild at most once for a batch that breaks the tree, then
            // invalidate only locations placed on the newly stranded nodes.
            while (removed.Count != 0)
            {
                var treeBroken = false;
                while (removed.TryDequeue(out var id))
                {
                    if (!watches.TryGetValue(id, out var clauses)) continue;
                    foreach (var clause in clauses)
                    {
                        if (clause.Invalidated) continue;
                        clause.Invalidated = true;
                        if (clause.Location is { } location)
                        {
                            if (--activationCounts[location] == 0) Remove(location);
                        }
                        else if (clause.Route is { } route && --routeCounts[route] == 0 && tree.Contains(route))
                            treeBroken = true;
                    }
                }
                if (!treeBroken) break;
                tree.Clear();
                var next = Walk(seeds, r => routeCounts[r] != 0, tree);
                foreach (var stranded in rooted)
                    if (!next.Contains(stranded))
                        foreach (var location in placements[stranded]) Remove(location);
                rooted = next;
            }
            return (rooted, candidates);

            void Remove(string id) { if (candidates.Remove(id)) removed.Enqueue(id); }
            void Watch(string[] prerequisites, AvailabilityClause clause)
            {
                foreach (var id in prerequisites)
                {
                    if (!watches.TryGetValue(id, out var watchers)) watches.Add(id, watchers = []);
                    watchers.Add(clause);
                }
            }
        }

        private sealed record AvailabilityClause(string? Location, Route? Route)
        {
            internal bool Invalidated { get; set; }
        }
    }
}
