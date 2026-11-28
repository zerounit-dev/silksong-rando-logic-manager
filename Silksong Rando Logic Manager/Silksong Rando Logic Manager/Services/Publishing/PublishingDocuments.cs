using Microsoft.EntityFrameworkCore;
using Silksong_Rando_Logic_Manager.Data;
using System.Text;
using System.Text.RegularExpressions;
using ExplanationRules = SilksongRandoLogicManager.Contracts.ExplanationRules;
using V2Severity = Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts.V2Severity;

namespace Silksong_Rando_Logic_Manager.Services.Publishing;

internal sealed class PublishingDocuments
{
    internal List<RoomGroup> Groups = [];
    internal List<Room> Rooms = [];
    internal List<Subroom> Subrooms = [];
    internal List<RoomTransition> Transitions = [];
    internal List<SubroomConnection> Connections = [];
    internal List<CheckLocation> Checks = [];
    internal Dictionary<Guid, string> Paths = [];
    internal Dictionary<Guid, string> GroupPaths = [];
    private List<RequirementSoul> souls = [];
    private ILookup<Guid?, Room> roomsByGroup = null!;
    private ILookup<Guid, Room> roomsById = null!;
    private ILookup<Guid, Subroom> subroomsById = null!;
    internal ILookup<Guid, Subroom> SubroomsByRoom { get; private set; } = null!;
    internal ILookup<Guid, RoomTransition> TransitionsByRoom { get; private set; } = null!;
    internal ILookup<Guid, SubroomConnection> ConnectionsByRoom { get; private set; } = null!;
    internal ILookup<Guid, CheckLocation> ChecksByRoom { get; private set; } = null!;
    internal static async Task<PublishingDocuments> LoadAsync(PublishingSnapshotFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var d = new PublishingDocuments
        {
            Groups = await db.RoomGroups.OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            Rooms = await db.Rooms.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            Subrooms = await db.Subrooms.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            Transitions = await db.RoomTransitions.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            Connections = await db.SubroomConnections.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            Checks = await db.CheckLocations.Where(x => !x.IsArchived).OrderBy(x => x.SortOrder).ThenBy(x => x.FriendlyName).ThenBy(x => x.Id).ToListAsync(),
            souls = await db.RequirementSouls.ToListAsync()
        };
        // Lookup buckets retain the already ordered frozen lists, including first ID matches.
        d.roomsByGroup = d.Rooms.ToLookup(x => x.RoomGroupId);
        d.roomsById = d.Rooms.ToLookup(x => x.Id);
        d.subroomsById = d.Subrooms.ToLookup(x => x.Id);
        d.SubroomsByRoom = d.Subrooms.ToLookup(x => x.RoomId);
        d.TransitionsByRoom = d.Transitions.ToLookup(x => x.RoomId);
        d.ConnectionsByRoom = d.Connections.ToLookup(x => x.RoomId);
        d.ChecksByRoom = d.Checks.ToLookup(x => x.RoomId);
        var groupNames = new HashSet<string>(StringComparer.Ordinal) { "00-annotations", "01-geometry", ".git" };
        foreach (var group in d.Groups)
        {
            d.GroupPaths[group.Id] = Unique(group.FriendlyName, groupNames);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var room in d.roomsByGroup[group.Id])
                d.Paths[room.Id] = d.GroupPaths[group.Id] + "/" + Unique(room.FriendlyName, names) + ".md";
        }
        // Canonical graph integrity reports ungrouped rooms before Write can run.
        return d;
    }
    private static string Unique(string name, HashSet<string> used)
    {
        var stem = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (stem.Length == 0) stem = "untitled";
        var result = stem; var number = 2;
        while (!used.Add(result)) result = stem + "-" + number++;
        return result;
    }
    internal static string Title(Room r) => Regex.Replace(r.FriendlyName + (string.IsNullOrEmpty(r.InGameId) ? "" : $" ({r.InGameId})"), @"\s+", " ").Trim();
    internal sealed record RuleIssue(bool Combat, Guid RoomId, string Kind, Guid RecordId, string Reason);
    internal List<RuleIssue> Validate(PreparedGraphEvidence graph)
    {
        var issues = new List<RuleIssue>();
        var protectedNames = new HashSet<string>(["driftlin", "mnemonid", "mnemonord", "skullwing", "wingmould", "barnak"]);
        static string Normalize(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
        var aliases = protectedNames.ToDictionary(x => x, x => x);
        foreach (var soul in souls.Where(x => protectedNames.Contains(Normalize(x.Name))))
            foreach (var alias in soul.Aliases.Split(',').Where(x => !string.IsNullOrWhiteSpace(x))) aliases[Normalize(alias)] = Normalize(soul.Name);
        var pogo = new Regex(@"\b(?:(?<difficulty>(?!enemy\b)[a-z]+)\s+)?(?:enemy\s+)?pogo\s+off\s+(?<enemies>\[[^\]]+\]|(?:(?!\s+\b(?:AND|OR)\b)[^()\r\n])+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var room in Rooms)
        {
            foreach (var check in ChecksByRoom[room.Id].Where(x => new[] { "boss", "gauntlet" }.Contains(x.LocationType?.Trim().ToLowerInvariant())))
                if (!Regex.IsMatch(RuleText("location", check.Id, check.Requirements), @"(?<!\w)Combat\s+Requirements(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    issues.Add(new(true, room.Id, "location", check.Id, $"{check.FriendlyName} [{check.LocationType}] must include Combat Requirements."));
            var rows = TransitionsByRoom[room.Id].Select(x => (x.FriendlyName, x.Requirements, Kind: "transition", x.Id))
                .Concat(ConnectionsByRoom[room.Id].Select(x => (x.FriendlyName, x.Requirements, Kind: "connection", x.Id)))
                .Concat(ChecksByRoom[room.Id].Select(x => (x.FriendlyName, x.Requirements, Kind: "location", x.Id)));
            foreach (var row in rows)
                foreach (Match match in pogo.Matches(RuleText(row.Kind, row.Id, row.Requirements)))
                {
                    var names = match.Groups["enemies"].Value.Trim('[', ']').Split(',').Select(Normalize).Where(aliases.ContainsKey).Select(x => aliases[x]).Distinct().Order(StringComparer.Ordinal).ToArray();
                    if (names.Length > 0 && !match.Groups["difficulty"].Value.Equals("intended", StringComparison.OrdinalIgnoreCase))
                        issues.Add(new(false, room.Id, row.Kind, row.Id, $"{string.Join(", ", names)} pogo-off must use Intended."));
                }
        }
        return issues;

        string RuleText(string kind, Guid id, string raw)
        {
            // Only the ephemeral business-rule scan needs operand text without
            // suffixes. Use only confidently recognized frozen operand ranges;
            // raw Markdown/JSON/SQLite and existing unreadable evidence stay intact.
            if (!graph.ExplanationSpans.TryGetValue((kind, id), out var spans)) return raw;
            var operandText = new StringBuilder(raw);
            foreach (var span in spans.OrderByDescending(x => x.Start.Value))
            {
                var (start, length) = span.GetOffsetAndLength(raw.Length);
                operandText.Remove(start, length);
            }
            return operandText.ToString();
        }
    }
    internal void Write(PublishingStorage storage, string directory, string captures, string font, PreparedGraphEvidence graph,
        Action<string?, int, int> progress, PublishingTimings timings)
    {
        var index = new StringBuilder("# Silksong Randomizer Notes\n\nGenerated from the non-archived records in the accompanying frozen SQLite snapshot.\n\n[Composite Master](./MASTER.md)\n\n");
        var master = new StringBuilder("# Silksong Randomizer Logic\n\nGenerated from the database alongside the individual room pages.\n\n");
        var explanationsByRoom = graph.Rooms.ToDictionary(x => x.RoomRecordId, x => x.Room.Explanations);
        var count = 0;
        foreach (var group in Groups)
        {
            index.Append($"## {group.FriendlyName}\n\n"); master.Append($"## {group.FriendlyName}\n\n");
            var rooms = roomsByGroup[group.Id].ToArray();
            foreach (var room in rooms)
            {
                progress(Title(room), count, Rooms.Count);
                var taskTiming = storage.Timing;
                using var timing = timings.Begin("Room generation", room.Id.ToString("D"), taskTiming);
                storage.Timing = timing;
                try
                {
                var images = PublishingRenderer.Render(storage, this, room, captures, directory, font, timing);
                var explanations = explanationsByRoom[room.Id];
                var markdown = timing.Measure(PublishingMetric.Markdown, () => Markdown(room, images, false, explanations, graph));
                WriteText(storage, directory, Paths[room.Id], markdown);
                using var masterTime = timing.Segment(PublishingMetric.Master);
                master.Append($"<a name=\"{Anchor(Paths[room.Id])}\"></a>\n\n");
                foreach (var line in Markdown(room, images, true, explanations, graph).TrimEnd().Split('\n'))
                    master.Append(line.StartsWith('#') ? "##" + line : line).Append('\n');
                master.Append('\n');
                index.Append($"- [{Title(room)}](./{Paths[room.Id]})\n"); count++;
                masterTime.Complete();
                timing.Complete();
                }
                finally { storage.Timing = taskTiming; }
            }
            if (rooms.Length == 0) index.Append("No rooms defined.\n");
            index.Append('\n');
        }
        index.Append("## Image Credits\n\nMassive thank you to **RainingChain** and **IdoManti** for allowing us to\ngenerate room annotations using the 25%-resolution image of their amazing\nhigh-detail Silksong map. The room images in this documentation are generated\nfrom that map through Silksong Rando Logic Manager.\n\n[Explore their interactive Silksong map and their incredible work.](https://scripterswar.com/silksong/map)\n\nThe underlying map imagery is not covered by this repository's license.\nReuse, modification, or redistribution of that imagery requires permission\nfrom its owners.\n");
        progress(null, count, Rooms.Count);
        WriteIndex("README.md", () => index.ToString());
        WriteIndex("MASTER.md", () => master.ToString().TrimEnd() + "\n");
        void WriteIndex(string path, Func<string> generate)
        {
            var taskTiming = storage.Timing;
            using var timing = timings.Begin(path + " final generation and write", parent: taskTiming);
            storage.Timing = timing;
            try
            {
                var text = timing.Measure(path == "README.md" ? PublishingMetric.Markdown : PublishingMetric.Master, generate);
                WriteText(storage, directory, path, text); timing.Complete();
            }
            finally { storage.Timing = taskTiming; }
        }
    }
    private static void WriteText(PublishingStorage storage, string directory, string path, string value)
    {
        var destination = PublishingStorage.Resolve(directory, path);
        var bytes = storage.Timing is { } timing ? timing.Measure(PublishingMetric.TextEncoding, () => new UTF8Encoding(false).GetBytes(value)) : new UTF8Encoding(false).GetBytes(value);
        storage.AtomicGeneratedCandidate(destination, bytes);
    }
    private static string Anchor(string path) => "room-" + path[..^3].Replace("/", "--");
    private static string Cell(object? value) => (value?.ToString() ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ").Trim();
    // Character references keep plain authored text literal in both a GFM
    // table cell and a link label, including pipes, brackets and backslashes.
    private static string ExplanationText(string text) =>
        EncodeMarkdown(text.Replace("\r", "").Replace("\n", " "),
            c => char.IsAscii(c) && !char.IsAsciiLetterOrDigit(c) && !char.IsWhiteSpace(c));

    private static string EncodeMarkdown(string text, Func<char, bool> needsEscape)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
            if (needsEscape(c)) result.Append(FormattableString.Invariant($"&#{(int)c};"));
            else result.Append(c);
        return result.ToString();
    }

    private static string ExplanationLabel(RoomGraphExplanation explanation)
    {
        var label = ExplanationText(string.IsNullOrWhiteSpace(explanation.Name) ? explanation.Reference : explanation.Name).Trim();
        if (ExplanationRules.LinkSeverity(explanation.Link) != V2Severity.Neutral || label.Length == 0) return label;
        // Keep authored URL punctuation (including query separators) as data;
        // Markdown resolves the character references inside the destination.
        var destination = EncodeMarkdown(explanation.Link.Trim(), c => c is '(' or ')' or '<' or '>' or '|' or '\\' or '&');
        return $"[{label}]({destination})";
    }

    private static string Verified(bool? value) => value is true ? "Verified" : value is false ? "Needs verification" : "";
    private string SubroomName(Guid? id, string? fallback) => (id is Guid key ? subroomsById[key].FirstOrDefault()?.FriendlyName : null) ?? fallback ?? "";
    private string Markdown(Room room, PublishingImageResult images, bool master, IReadOnlyList<RoomGraphExplanation> explanations,
        PreparedGraphEvidence graph)
    {
        var b = new StringBuilder($"# {Title(room)}\n\n");
        if (master) b.Append($"[View Room Page](./{Paths[room.Id]})\n\n");
        if (!string.IsNullOrEmpty(room.InGameId)) b.Append($"**Game ID:** {room.InGameId}\n\n");
        if (!string.IsNullOrEmpty(room.Contributors)) b.Append($"**Contributors:** {room.Contributors}\n\n");
        string Mark(Guid id) => images.Annotated.Contains(id) ? "✓" : "";
        void Table(string title, string[] headers, IEnumerable<object?[]> rows, string empty)
        {
            b.Append($"## {title}\n\n"); var entries = rows.ToArray();
            if (entries.Length == 0) b.Append(empty).Append('\n');
            else
            {
                b.Append("| ").AppendJoin(" | ", headers).Append(" |\n| ").AppendJoin(" | ", headers.Select(_ => "---")).Append(" |\n");
                foreach (var row in entries) b.Append("| ").AppendJoin(" | ", row.Select(Cell)).Append(" |\n");
            }
            b.Append('\n');
        }
        var subs = SubroomsByRoom[room.Id].ToArray();
        Table("Subrooms", ["No.", "Subroom", "Annotated"], subs.Select((x, i) => new object?[] { $"S{i + 1}", x.FriendlyName, Mark(x.Id) }), "No subrooms defined.");
        foreach (var s in subs.Where(x => !string.IsNullOrEmpty(x.Notes))) b.Append($"- **{s.FriendlyName}:** {s.Notes}\n");
        if (subs.Any(x => !string.IsNullOrEmpty(x.Notes))) b.Append('\n');
        if (explanations.Count != 0)
            Table("Explanations", ["Name", "Reference", "Notes"], explanations.Select(x =>
                new object?[] { ExplanationLabel(x), ExplanationText(x.Reference), ExplanationText(x.Notes) }), "");

        string Requirement(string kind, Guid id, string raw)
        {
            if (!graph.ExplanationSuffixes.TryGetValue((kind, id), out var suffixes)) return raw;
            // Treat authored fragments as literal whenever inserting generated
            // content, including a plain label with Markdown character references.
            var rendered = new StringBuilder(raw.Length);
            var offset = 0;
            foreach (var suffix in suffixes.OrderBy(x => x.DisplaySpan.Start.Value))
            {
                var (start, length) = suffix.DisplaySpan.GetOffsetAndLength(raw.Length);
                // The same frozen graph parse established uniqueness and retained
                // the exact exported Reference, even for an unreadable whole rule.
                var definition = explanations.Single(x => x.Reference == suffix.Reference);
                rendered.Append(ExplanationText(raw[offset..start]));
                rendered.Append("(see ").Append(ExplanationLabel(definition)).Append(')');
                offset = start + length;
            }
            rendered.Append(ExplanationText(raw[offset..]));
            return rendered.ToString();
        }

        string Destination(RoomTransition t)
        {
            var target = t.ResolvedDestinationRoomId is Guid key ? roomsById[key].FirstOrDefault() : null;
            if (target is null) return string.IsNullOrEmpty(t.DestinationRoomReferenceText) ? "TODO" : t.DestinationRoomReferenceText;
            var path = Paths[target.Id];
            return $"[{Title(target)}]({(master ? "#" + Anchor(path) : target.RoomGroupId == room.RoomGroupId ? path.Split('/')[1] : "../" + path)})";
        }
        Table("Room Transitions", ["Alias", "Name", "From subroom", "Destination", "Destination alias", "Requirements", "TODO", "Verification", "Annotated", "Notes"],
            TransitionsByRoom[room.Id].Select(x => new object?[] { x.Alias, x.FriendlyName, SubroomName(x.ResolvedSourceSubroomId, x.SourceSubroomReferenceText), Destination(x), x.DestinationTransitionAliasText, Requirement("transition", x.Id, x.Requirements), x.IsTodo ? "TODO" : "", Verified(x.IsVerified), Mark(x.Id), x.Notes }), "No room transitions defined.");
        Table("Subroom Connections", ["Alias", "Name", "Source", "Destination", "Requirements", "TODO", "Verification", "Annotated", "Notes"],
            ConnectionsByRoom[room.Id].Select(x => new object?[] { x.Alias, x.FriendlyName, SubroomName(x.ResolvedSourceSubroomId, x.SourceSubroomReferenceText), SubroomName(x.ResolvedDestinationSubroomId, x.DestinationSubroomReferenceText), Requirement("connection", x.Id, x.Requirements), x.IsTodo ? "TODO" : "", Verified(x.IsVerified), Mark(x.Id), x.Notes }), "No subroom connections defined.");
        Table("Check Locations", ["No.", "Check", "Subroom", "Requirements", "TODO", "Verification", "Location Type", "Annotated", "Notes"],
            ChecksByRoom[room.Id].Select((x, i) => new object?[] { i + 1, x.FriendlyName, SubroomName(x.ResolvedSubroomId, x.SubroomReferenceText), Requirement("location", x.Id, x.Requirements), x.IsTodo ? "TODO" : "", Verified(x.IsVerified), x.LocationType, Mark(x.Id), x.Notes }), "No check locations defined.");
        if (!string.IsNullOrEmpty(room.Comments)) b.Append($"## Notes\n\n{room.Comments.Trim()}\n\n");
        if (!master)
        {
            b.Append("## Room Images\n\n");
            if (images.Paths.Count == 0) b.Append("Scene image unavailable.\n");
            foreach (var (kind, label) in new[] { ("connections", "Connections"), ("checks", "Checks"), ("scene", "Scene") })
                if (images.Paths.TryGetValue(kind, out var path)) b.Append($"### {label}\n\n[![{label} for {Cell(Title(room))}](../{path})](../{path})\n\n");
        }
        return b.ToString().TrimEnd() + "\n";
    }
}
